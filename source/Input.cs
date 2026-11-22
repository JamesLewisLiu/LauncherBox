using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;

namespace LauncherBox {
    public enum InputSource { Keyboard, DirectInput, XInput, Midi }
    public enum LauncherAction { Previous, Next, Play, Settings, Fullscreen, Exit }

    public sealed class InputBinding {
        public LauncherAction Action { get; set; }
        public InputSource Source { get; set; }
        public string Device { get; set; }
        public string Code { get; set; }
        public string Label { get; set; }

        internal bool Matches(InputSignal signal) {
            return Source == signal.Source && Device == signal.Device && Code == signal.Code;
        }
    }

    internal sealed class InputSignal {
        internal readonly InputSource Source;
        internal readonly string Device;
        internal readonly string Code;
        internal readonly string Label;

        internal InputSignal(InputSource source, string device, string code, string label) {
            Source = source;
            Device = device;
            Code = code;
            Label = label;
        }

        internal static InputSignal KeyboardKey(Key key) {
            string code = key.ToString();
            return new InputSignal(InputSource.Keyboard, "keyboard", code, code);
        }

        internal string Identity {
            get { return Source + "|" + Device + "|" + Code; }
        }
    }

    internal sealed class KeyMap {
        internal static readonly LauncherAction[] Actions = {
            LauncherAction.Previous, LauncherAction.Next, LauncherAction.Play,
            LauncherAction.Settings, LauncherAction.Fullscreen, LauncherAction.Exit
        };
        private List<InputBinding> bindings;

        internal KeyMap() {
            bindings = Store.Read<List<InputBinding>>(Store.Bindings) ?? Defaults();
        }

        private static InputBinding Make(LauncherAction action, InputSignal signal) {
            return new InputBinding {
                Action = action, Source = signal.Source, Device = signal.Device,
                Code = signal.Code, Label = signal.Label
            };
        }

        private static List<InputBinding> Defaults() {
            return new List<InputBinding> {
                Make(LauncherAction.Previous, InputSignal.KeyboardKey(Key.Left)),
                Make(LauncherAction.Next, InputSignal.KeyboardKey(Key.Right)),
                Make(LauncherAction.Play, InputSignal.KeyboardKey(Key.Enter)),
                Make(LauncherAction.Settings, InputSignal.KeyboardKey(Key.F2))
            };
        }

        internal static string Label(LauncherAction action) {
            switch (action) {
                case LauncherAction.Previous: return "Previous game";
                case LauncherAction.Next: return "Next game";
                case LauncherAction.Fullscreen: return "Toggle fullscreen";
                default: return action.ToString();
            }
        }

        internal InputBinding Get(LauncherAction action, InputSource source) {
            return bindings.Find(b => b.Action == action && b.Source == source);
        }

        internal InputBinding Match(InputSignal signal) {
            return bindings.Find(b => b.Matches(signal));
        }

        internal void Set(LauncherAction action, InputSource source, InputSignal signal) {
            List<InputBinding> updated = new List<InputBinding>(bindings);
            updated.RemoveAll(b => (b.Action == action && b.Source == source) || (signal != null && b.Matches(signal)));
            if (signal != null) updated.Add(Make(action, signal));
            Commit(updated);
        }

        internal void Reset(InputSource source) {
            List<InputBinding> updated = bindings.FindAll(b => b.Source != source);
            updated.AddRange(Defaults().FindAll(b => b.Source == source));
            Commit(updated);
        }

        private void Commit(List<InputBinding> updated) {
            Store.Write(Store.Bindings, updated);
            bindings = updated;
        }
    }

    internal sealed class DeviceInput : IDisposable {
        private readonly HwndSource window;
        private readonly DispatcherTimer timer;
        private readonly DirectInput direct;
        private readonly MidiInput midi;
        private readonly Func<bool> active;
        private readonly Func<InputSignal, bool> route;
        private HashSet<string> held;
        private DateTime nextScan;

        internal DeviceInput(HwndSource window, Func<bool> active, Func<InputSignal, bool> route) {
            this.window = window;
            this.active = active;
            this.route = route;
            direct = new DirectInput(window.Handle);
            midi = new MidiInput(window.Handle);
            window.AddHook(OnMessage);
            timer = new DispatcherTimer(DispatcherPriority.Input, window.Dispatcher);
            timer.Interval = TimeSpan.FromMilliseconds(16);
            timer.Tick += Tick;
            timer.Start();
        }

        private void Tick(object sender, EventArgs e) {
            if (!active()) {
                held = null;
                return;
            }
            DateTime now = DateTime.UtcNow;
            if (now >= nextScan) {
                direct.Refresh();
                midi.Refresh();
                nextScan = now.AddSeconds(2);
            }
            List<InputSignal> pressed = new List<InputSignal>();
            XInput.Read(pressed);
            direct.Read(pressed);
            HashSet<string> current = new HashSet<string>();
            foreach (InputSignal signal in pressed) current.Add(signal.Identity);
            HashSet<string> previous = held;
            held = current;
            if (previous == null) return;
            foreach (InputSignal signal in pressed) {
                if (!previous.Contains(signal.Identity) && route(signal)) break;
            }
        }

        private IntPtr OnMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled) {
            if (message == 0x0219) nextScan = DateTime.MinValue; // WM_DEVICECHANGE
            if (message == 0x03C3 || message == 0x03CC) { // MM_MIM_DATA and MM_MIM_MOREDATA
                InputSignal signal = midi.Decode(wParam, unchecked((uint)lParam.ToInt64()));
                if (signal != null && active()) route(signal);
                handled = true;
            }
            return IntPtr.Zero;
        }

        internal void ReleaseForGame() {
            held = null;
            direct.Unacquire();
            midi.Suspend();
            nextScan = DateTime.MinValue;
        }

        public void Dispose() {
            timer.Stop();
            if (!window.IsDisposed) window.RemoveHook(OnMessage);
            direct.Dispose();
            midi.Dispose();
        }
    }

    internal static class XInput {
        [StructLayout(LayoutKind.Sequential)]
        private struct State {
            internal uint Packet;
            internal ushort Buttons;
            internal byte LeftTrigger, RightTrigger;
            internal short LeftX, LeftY, RightX, RightY;
        }

        [DllImport("xinput1_4.dll")]
        private static extern uint XInputGetState(uint slot, out State state);

        private static readonly ushort[] Masks = {
            0x0001, 0x0002, 0x0004, 0x0008, 0x0010, 0x0020, 0x0040, 0x0080,
            0x0100, 0x0200, 0x1000, 0x2000, 0x4000, 0x8000
        };
        private static readonly string[] Names = {
            "D-pad Up", "D-pad Down", "D-pad Left", "D-pad Right", "Start", "Back", "Left Stick", "Right Stick",
            "Left Bumper", "Right Bumper", "A", "B", "X", "Y"
        };

        internal static void Read(List<InputSignal> pressed) {
            for (uint slot = 0; slot < 4; slot++) {
                State state;
                if (XInputGetState(slot, out state) != 0) continue;
                for (int i = 0; i < Masks.Length; i++) {
                    if ((state.Buttons & Masks[i]) != 0) Add(pressed, slot, Names[i]);
                }
                if (state.LeftTrigger >= 128) Add(pressed, slot, "Left Trigger");
                if (state.RightTrigger >= 128) Add(pressed, slot, "Right Trigger");
                Stick(pressed, slot, "Left Stick", state.LeftX, state.LeftY);
                Stick(pressed, slot, "Right Stick", state.RightX, state.RightY);
            }
        }

        private static void Stick(List<InputSignal> pressed, uint slot, string name, int x, int y) {
            const int threshold = 18000;
            if (Math.Abs(x) >= threshold) Add(pressed, slot, name + (x < 0 ? " Left" : " Right"));
            if (Math.Abs(y) >= threshold) Add(pressed, slot, name + (y < 0 ? " Down" : " Up"));
        }

        private static void Add(List<InputSignal> pressed, uint slot, string control) {
            pressed.Add(new InputSignal(InputSource.XInput, slot.ToString(), control, "Controller " + (slot + 1) + " · " + control));
        }
    }
}
