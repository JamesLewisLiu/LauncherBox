using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;

namespace LauncherBox {
    internal static class Log {
        private static readonly BlockingCollection<string> Messages = new BlockingCollection<string>(256);
        private static readonly int Pid = Process.GetCurrentProcess().Id;
        private static readonly Thread Writer = StartWriter();

        internal static void Info(string message) {
            try {
                Messages.TryAdd(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz") + " [" + Pid + ":" +
                    Thread.CurrentThread.ManagedThreadId + "] " + message);
            } catch (InvalidOperationException) { }
        }

        internal static void Error(string operation, Exception exception) {
            Info(operation + ": " + exception);
        }

        internal static void Finish() {
            Messages.CompleteAdding();
            Writer.Join(250);
        }

        private static Thread StartWriter() {
            Thread thread = new Thread(Write) { IsBackground = true, Name = "LauncherBox log" };
            thread.Start();
            return thread;
        }

        private static void Write() {
            string directory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            string path = Path.Combine(directory, "LauncherBox.log");
            string fallback = Path.Combine(Path.GetTempPath(), "LauncherBox-" + Pid + ".log");
            foreach (string message in Messages.GetConsumingEnumerable()) {
                if (!Append(path, message) && path != fallback) {
                    path = fallback;
                    Append(path, message);
                }
            }
        }

        private static bool Append(string path, string message) {
            try {
                FileInfo file = new FileInfo(path);
                if (file.Exists) {
                    if ((file.Attributes & FileAttributes.ReparsePoint) != 0) return false;
                    if (file.Length >= 1024 * 1024) {
                        string previous = path + ".1";
                        File.Delete(previous);
                        File.Move(path, previous);
                    }
                }
                using (FileStream stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                using (StreamWriter writer = new StreamWriter(stream)) writer.WriteLine(message);
                return true;
            } catch (IOException) {
                return false;
            } catch (UnauthorizedAccessException) {
                return false;
            }
        }
    }
}
