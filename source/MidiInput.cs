using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace LauncherBox {
    internal sealed class MidiInput : IDisposable {
        private readonly IntPtr window;
        private readonly object sync = new object();
        private volatile Dictionary<IntPtr, Port> ports = new Dictionary<IntPtr, Port>();
        private readonly HashSet<string> active = new HashSet<string>();
        private int pending;
        private bool disposed;

        internal MidiInput(IntPtr window) {
            this.window = window;
        }

        internal void Refresh() {
            if (Interlocked.CompareExchange(ref pending, 1, 0) != 0) return;
            ThreadPool.QueueUserWorkItem(delegate {
                try {
                    lock (sync) {
                        if (!disposed && Volatile.Read(ref pending) == 1) Scan();
                    }
                } finally {
                    Interlocked.Exchange(ref pending, 0);
                }
            });
        }

        private void Scan() {
            Dictionary<IntPtr, Port> updated = new Dictionary<IntPtr, Port>(ports);
            Dictionary<string, int> duplicates = new Dictionary<string, int>();
            HashSet<string> seen = new HashSet<string>();
            uint count = midiInGetNumDevs();
            for (uint index = 0; index < count; index++) {
                Caps caps;
                if (midiInGetDevCaps(new UIntPtr(index), out caps, Marshal.SizeOf(typeof(Caps))) != 0) continue;
                string model = caps.Manufacturer + ":" + caps.Product + ":" + caps.Name;
                int number;
                duplicates.TryGetValue(model, out number);
                duplicates[model] = number + 1;
                string id = model + ":" + number;
                seen.Add(id);
                if (Contains(updated, id)) continue;
                IntPtr handle;
                // CALLBACK_WINDOW delivers short WinMM messages to the launcher's HWND.
                if (midiInOpen(out handle, index, window, IntPtr.Zero, 0x10000) != 0) continue;
                if (midiInStart(handle) == 0) updated[handle] = new Port { Id = id, Name = caps.Name };
                else midiInClose(handle);
            }

            foreach (KeyValuePair<IntPtr, Port> port in ports) {
                if (seen.Contains(port.Value.Id)) continue;
                updated.Remove(port.Key);
                Close(port.Key);
            }
            ports = updated;
        }

        private static bool Contains(Dictionary<IntPtr, Port> ports, string id) {
            foreach (Port port in ports.Values) {
                if (port.Id == id) return true;
            }
            return false;
        }

        internal InputSignal Decode(IntPtr handle, uint data) {
            Port port;
            if (!ports.TryGetValue(handle, out port)) return null;
            int channel = (int)(data & 0x0F) + 1;
            int message = (int)(data & 0xF0);
            int first = (int)((data >> 8) & 0x7F);
            int second = (int)((data >> 16) & 0x7F);
            switch (message) {
                case 0x90:
                    // Each nonzero Note On is a strike. Drum pads may omit Note Off.
                    return second > 0 ? Signal(port, channel, "note:" + first, "Note " + first) : null;
                case 0xB0:
                    if (first >= 120) return null; // Channel mode messages are not controls.
                    return Threshold(port, channel, "cc:" + first, "CC " + first, second, 64, 48);
                case 0xE0:
                    int bend = ((second << 7) | first) - 8192;
                    InputSignal lower = Threshold(port, channel, "pitch:-", "Pitch Bend -", -bend, 3072, 2048);
                    InputSignal upper = Threshold(port, channel, "pitch:+", "Pitch Bend +", bend, 3072, 2048);
                    return lower ?? upper;
                default:
                    return null;
            }
        }

        private InputSignal Threshold(Port port, int channel, string code, string name, int value, int on, int off) {
            string identity = port.Id + "|" + channel + "|" + code;
            if (value <= off) active.Remove(identity);
            return value >= on && active.Add(identity) ? Signal(port, channel, code, name) : null;
        }

        private static InputSignal Signal(Port port, int channel, string code, string name) {
            return new InputSignal(InputSource.Midi, port.Id, channel + ":" + code, port.Name + " · Ch " + channel + " · " + name);
        }

        internal void Suspend() {
            lock (sync) {
                Interlocked.CompareExchange(ref pending, 2, 1);
                Dictionary<IntPtr, Port> closing = ports;
                ports = new Dictionary<IntPtr, Port>();
                foreach (IntPtr port in closing.Keys) Close(port);
            }
            active.Clear();
        }

        private static void Close(IntPtr handle) {
            midiInReset(handle);
            midiInClose(handle);
        }

        public void Dispose() {
            lock (sync) disposed = true;
            Suspend();
        }

        private sealed class Port {
            internal string Id, Name;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct Caps {
            internal ushort Manufacturer, Product;
            internal uint Version;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] internal string Name;
            internal uint Support;
        }

        [DllImport("winmm.dll")]
        private static extern uint midiInGetNumDevs();
        [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
        private static extern uint midiInGetDevCaps(UIntPtr device, out Caps caps, int size);
        [DllImport("winmm.dll")]
        private static extern uint midiInOpen(out IntPtr input, uint device, IntPtr callback, IntPtr instance, uint flags);
        [DllImport("winmm.dll")]
        private static extern uint midiInStart(IntPtr input);
        [DllImport("winmm.dll")]
        private static extern uint midiInReset(IntPtr input);
        [DllImport("winmm.dll")]
        private static extern uint midiInClose(IntPtr input);
    }
}
