using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace LauncherBox {
    internal sealed class GamepadWorker : IDisposable {
        private static readonly InputSignal[] Empty = new InputSignal[0];
        private readonly IntPtr window;
        private readonly bool useDirectInput;
        private readonly string name;
        private readonly object sync = new object();
        private readonly TaskCompletionSource<bool> firstPoll = NewCompletion();
        private TaskCompletionSource<bool> suspension;
        private volatile bool active;
        private volatile bool stopped;
        private volatile bool rescan = true;
        private volatile InputSignal[] pressed = Empty;
        private volatile Operation operation;

        internal GamepadWorker(IntPtr window, bool useDirectInput) {
            this.window = window;
            this.useDirectInput = useDirectInput;
            name = useDirectInput ? "DirectInput" : "XInput";
            new Thread(Run) { IsBackground = true, Name = "LauncherBox " + name }.Start();
        }

        internal bool Active {
            set { active = value; }
        }

        internal void Rescan() {
            rescan = true;
        }

        internal Task<bool> FirstPoll { get { return firstPoll.Task; } }

        internal void Read(List<InputSignal> destination) {
            destination.AddRange(pressed);
            Operation current = operation;
            if (current != null && !current.Reported && current.Clock.ElapsedMilliseconds >= 2000) {
                current.Reported = true;
                Log.Info(name + " is waiting for " + current.Name + "; the launcher can still accept keyboard input.");
            }
        }

        internal Task Suspend() {
            lock (sync) {
                active = false;
                pressed = Empty;
                if (stopped) return Task.FromResult(true);
                if (suspension == null) suspension = NewCompletion();
                return suspension.Task;
            }
        }

        private static TaskCompletionSource<bool> NewCompletion() {
            return new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        private void CompleteSuspension() {
            lock (sync) {
                if (suspension == null) return;
                suspension.TrySetResult(true);
                suspension = null;
            }
        }

        private void Run() {
            DirectInput direct = null;
            try {
                bool acquired = false;
                Stopwatch sinceScan = Stopwatch.StartNew();
                Log.Info(name + " worker started.");
                while (!stopped) {
                    if (!active) {
                        if (acquired) {
                            operation = new Operation("device release");
                            direct.Unacquire();
                            acquired = false;
                            operation = null;
                        }
                        pressed = Empty;
                        CompleteSuspension();
                        Thread.Sleep(25);
                        continue;
                    }

                    List<InputSignal> current = new List<InputSignal>();
                    if (useDirectInput) {
                        if (direct == null) {
                            operation = new Operation("driver initialization");
                            Log.Info("Initializing the DirectInput driver.");
                            direct = new DirectInput(window);
                        }
                        if (rescan || sinceScan.ElapsedMilliseconds >= 2000) {
                            rescan = false;
                            operation = new Operation("device enumeration");
                            if (!firstPoll.Task.IsCompleted) Log.Info("Enumerating DirectInput controllers.");
                            direct.Refresh();
                            sinceScan.Restart();
                        }
                        operation = new Operation("controller polling");
                        if (!firstPoll.Task.IsCompleted) Log.Info("Polling DirectInput controllers for the first time.");
                        direct.Read(current);
                        acquired = true;
                    } else {
                        operation = new Operation("controller polling");
                        XInput.Read(current);
                    }
                    operation = null;
                    pressed = active && current.Count > 0 ? current.ToArray() : Empty;
                    if (firstPoll.TrySetResult(true)) Log.Info(name + " initialization and first poll finished.");
                    Thread.Sleep(16);
                }
            } catch (Exception ex) {
                Log.Error(name + " failed during " + (operation == null ? "input processing" : operation.Name), ex);
            } finally {
                try {
                    if (direct != null) direct.Dispose();
                } catch (Exception ex) {
                    Log.Error(name + " cleanup failed", ex);
                } finally {
                    operation = null;
                    pressed = Empty;
                    firstPoll.TrySetResult(false);
                    Dispose();
                    CompleteSuspension();
                }
            }
        }

        public void Dispose() {
            stopped = true;
        }

        private sealed class Operation {
            internal readonly string Name;
            internal readonly Stopwatch Clock = Stopwatch.StartNew();
            internal bool Reported;

            internal Operation(string name) {
                Name = name;
            }
        }
    }
}
