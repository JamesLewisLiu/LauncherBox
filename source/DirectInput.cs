using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace LauncherBox {
    internal sealed class DirectInput : IDisposable {
        private const int AbsoluteAxis = 0x02, Button = 0x0C, Pov = 0x10;
        private const int ObjectIdMask = 0x00FFFFFF;
        private readonly IntPtr window;
        private readonly Dictionary<Guid, Controller> controllers = new Dictionary<Guid, Controller>();
        private readonly IInput input;

        internal DirectInput(IntPtr window) {
            this.window = window;
            Guid iid = typeof(IInput).GUID;
            IInput native;
            if (DirectInput8Create(GetModuleHandle(null), 0x0800, ref iid, out native, IntPtr.Zero) >= 0) input = native;
        }

        internal void Refresh() {
            if (input == null) return;
            Dictionary<Guid, string> attached = new Dictionary<Guid, string>();
            // DI8DEVCLASS_GAMECTRL, DIEDFL_ATTACHEDONLY
            if (input.EnumDevices(4, delegate(IntPtr data, IntPtr context) {
                DeviceInstance info = (DeviceInstance)Marshal.PtrToStructure(data, typeof(DeviceInstance));
                attached[info.Id] = info.Name;
                return 1;
            }, IntPtr.Zero, 1) < 0) return;

            foreach (Guid id in new List<Guid>(controllers.Keys)) {
                if (attached.ContainsKey(id)) continue;
                controllers[id].Dispose();
                controllers.Remove(id);
            }
            foreach (KeyValuePair<Guid, string> item in attached) {
                if (controllers.ContainsKey(item.Key)) continue;
                Guid id = item.Key;
                IDevice device;
                if (input.CreateDevice(ref id, out device, IntPtr.Zero) < 0) continue;
                Controller controller = new Controller(device, id, item.Value);
                if (controller.Initialize(window)) controllers.Add(id, controller);
                else controller.Dispose();
            }
        }

        internal void Read(List<InputSignal> pressed) {
            foreach (Controller controller in controllers.Values) controller.Read(pressed);
        }

        internal void Unacquire() {
            foreach (Controller controller in controllers.Values) controller.Unacquire();
        }

        public void Dispose() {
            foreach (Controller controller in controllers.Values) controller.Dispose();
            controllers.Clear();
            if (input != null) Marshal.ReleaseComObject(input);
        }

        private sealed class Controller : IDisposable {
            private static readonly string[] Directions = {
                "Up", "Up Right", "Right", "Down Right", "Down", "Down Left", "Left", "Up Left"
            };
            private readonly IDevice device;
            private readonly string id, name;
            private readonly List<Control> controls = new List<Control>();
            private IntPtr data;
            private int dataSize;

            internal Controller(IDevice device, Guid id, string name) {
                this.device = device;
                this.id = id.ToString("D");
                this.name = name;
            }

            internal bool Initialize(IntPtr window) {
                if (device.EnumObjects(delegate(IntPtr value, IntPtr context) {
                    DeviceObject obj = (DeviceObject)Marshal.PtrToStructure(value, typeof(DeviceObject));
                    controls.Add(new Control {
                        Id = obj.Id & ObjectIdMask, Offset = controls.Count * sizeof(int), Name = obj.Name
                    });
                    return 1;
                }, IntPtr.Zero, AbsoluteAxis | Button | Pov) < 0 || controls.Count == 0) return false;

                int objectSize = Marshal.SizeOf(typeof(ObjectFormat));
                IntPtr formats = Marshal.AllocHGlobal(objectSize * controls.Count);
                dataSize = controls.Count * sizeof(int);
                try {
                    foreach (Control control in controls) {
                        Marshal.StructureToPtr(new ObjectFormat { Offset = control.Offset, Type = control.Id },
                            IntPtr.Add(formats, control.Offset / sizeof(int) * objectSize), false);
                    }
                    DataFormat format = new DataFormat {
                        Size = Marshal.SizeOf(typeof(DataFormat)), ObjectSize = objectSize,
                        Flags = 1, DataSize = dataSize, Count = controls.Count, Objects = formats
                    };
                    // DIDF_ABSAXIS, DISCL_NONEXCLUSIVE | DISCL_BACKGROUND
                    if (device.SetDataFormat(ref format) < 0 || device.SetCooperativeLevel(window, 0x0A) < 0) return false;
                } finally {
                    Marshal.FreeHGlobal(formats);
                }

                foreach (Control control in controls) {
                    if ((control.Id & 0xFF) != AbsoluteAxis) continue;
                    Range range = new Range { Size = 24, HeaderSize = 16, Object = control.Id, How = 2, Min = -32768, Max = 32767 };
                    // DIPROP_RANGE is a special GUID argument with the pointer value 4.
                    if (device.SetProperty(new IntPtr(4), ref range) >= 0 || device.GetProperty(new IntPtr(4), ref range) >= 0)
                        control.Threshold = Math.Max(1, (int)(((long)range.Max - range.Min) / 4));
                }
                data = Marshal.AllocHGlobal(dataSize);
                return true;
            }

            internal void Read(List<InputSignal> pressed) {
                if (device.Poll() < 0 && (device.Acquire() < 0 || device.Poll() < 0)) return;
                if (device.GetDeviceState(dataSize, data) < 0) {
                    if (device.Acquire() < 0 || device.GetDeviceState(dataSize, data) < 0) return;
                }

                foreach (Control control in controls) {
                    int type = control.Id & 0xFF;
                    if ((type & Button) != 0) {
                        if ((Marshal.ReadByte(data, control.Offset) & 0x80) != 0) Add(pressed, control, "");
                        continue;
                    }
                    int value = Marshal.ReadInt32(data, control.Offset);
                    if (type == Pov) {
                        if (value >= 0 && value < 36000) Add(pressed, control, Directions[((value + 2250) / 4500) % 8]);
                    } else if (type == AbsoluteAxis && control.Threshold > 0) {
                        if (!control.Neutral.HasValue) control.Neutral = value;
                        long movement = (long)value - control.Neutral.Value;
                        if (Math.Abs(movement) >= control.Threshold) Add(pressed, control, movement < 0 ? "-" : "+");
                    }
                }
            }

            private void Add(List<InputSignal> pressed, Control control, string direction) {
                string code = control.Id.ToString("X") + ":" + direction;
                string label = name + " · " + control.Name + (direction.Length > 0 ? " " + direction : "");
                pressed.Add(new InputSignal(InputSource.DirectInput, id, code, label));
            }

            internal void Unacquire() {
                device.Unacquire();
            }

            public void Dispose() {
                Unacquire();
                Marshal.ReleaseComObject(device);
                if (data != IntPtr.Zero) Marshal.FreeHGlobal(data);
            }
        }

        private sealed class Control {
            internal int Id, Offset, Threshold;
            internal int? Neutral;
            internal string Name;
        }

        // The callbacks only consume the common prefixes of DIDEVICEINSTANCEW and DIDEVICEOBJECTINSTANCEW.
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DeviceInstance {
            internal int Size;
            internal Guid Id, Product;
            internal int Type;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] internal string Name;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DeviceObject {
            internal int Size;
            internal Guid Type;
            internal int Offset, Id, Flags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] internal string Name;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ObjectFormat {
            internal IntPtr Guid;
            internal int Offset, Type, Flags;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DataFormat {
            internal int Size, ObjectSize, Flags, DataSize, Count;
            internal IntPtr Objects;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Range {
            internal int Size, HeaderSize, Object, How, Min, Max;
        }

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int Enumerate(IntPtr info, IntPtr context);

        [ComImport, Guid("BF798031-483A-4DA2-AA99-5D64ED369700"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IInput {
            [PreserveSig] int CreateDevice(ref Guid device, out IDevice result, IntPtr outer);
            [PreserveSig] int EnumDevices(int deviceClass, Enumerate callback, IntPtr context, int flags);
        }

        [ComImport, Guid("54D41081-DC15-4833-A41B-748F73A38179"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IDevice {
            // Unused COM slots are declared to preserve the native vtable order.
            [PreserveSig] int GetCapabilities(IntPtr capabilities);
            [PreserveSig] int EnumObjects(Enumerate callback, IntPtr context, int flags);
            [PreserveSig] int GetProperty(IntPtr property, ref Range range);
            [PreserveSig] int SetProperty(IntPtr property, ref Range range);
            [PreserveSig] int Acquire();
            [PreserveSig] int Unacquire();
            [PreserveSig] int GetDeviceState(int size, IntPtr state);
            [PreserveSig] int GetDeviceData(int size, IntPtr data, IntPtr count, int flags);
            [PreserveSig] int SetDataFormat(ref DataFormat format);
            [PreserveSig] int SetEventNotification(IntPtr eventHandle);
            [PreserveSig] int SetCooperativeLevel(IntPtr window, int flags);
            void GetObjectInfo();
            void GetDeviceInfo();
            void RunControlPanel();
            void Initialize();
            void CreateEffect();
            void EnumEffects();
            void GetEffectInfo();
            void GetForceFeedbackState();
            void SendForceFeedbackCommand();
            void EnumCreatedEffectObjects();
            void Escape();
            [PreserveSig] int Poll();
        }

        [DllImport("dinput8.dll")]
        private static extern int DirectInput8Create(IntPtr module, int version, ref Guid iid, out IInput input, IntPtr outer);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandle(string name);
    }
}
