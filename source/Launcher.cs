using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml;
using System.Xml.Serialization;
using Microsoft.Win32;

namespace LauncherBox {
    public sealed class Game {
        public string Name { get; set; }
        public string Bat { get; set; }
        public string Hash { get; set; }
        public string Art { get; set; }
    }

    internal static class Store {
        internal static readonly string Sid = WindowsIdentity.GetCurrent().User.Value;
        internal static readonly string Root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "LauncherBox");
        internal static readonly string Exe = Path.Combine(Root, "LauncherBox.exe");
        internal static readonly string Profile = Path.Combine(Root, "Profiles", Sid);
        private static readonly string Catalog = Path.Combine(Profile, "games.xml");

        internal static bool Install() {
            string self = Assembly.GetExecutingAssembly().Location;
            if (String.Equals(Path.GetFullPath(self), Exe, StringComparison.OrdinalIgnoreCase)) return true;
            if (MessageBox.Show("Install LauncherBox to " + Root + " and create a desktop shortcut?", "Install LauncherBox", MessageBoxButton.OKCancel) != MessageBoxResult.OK) return false;
            Directory.CreateDirectory(Root);
            string staging = Path.Combine(Root, Guid.NewGuid().ToString("N") + ".tmp");
            try {
                File.Copy(self, staging);
                if (File.Exists(Exe)) File.Replace(staging, Exe, null);
                else File.Move(staging, Exe);
            } finally { if (File.Exists(staging)) File.Delete(staging); }
            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            if (Directory.Exists(desktop)) {
                dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell", true));
                dynamic link = shell.CreateShortcut(Path.Combine(desktop, "LauncherBox.lnk"));
                link.TargetPath = Exe;
                link.WorkingDirectory = Root;
                link.Save();
            }
            Process.Start(new ProcessStartInfo(Exe) { UseShellExecute = true, WorkingDirectory = Root });
            return false;
        }

        internal static Window WindowFrom(string name) {
            using (Stream input = Resource(name)) return (Window)XamlReader.Load(input);
        }

        internal static Stream Resource(string name) {
            Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name);
            if (stream == null) throw new InvalidDataException("Missing embedded resource " + name);
            return stream;
        }

        internal static List<Game> Load() {
            Directory.CreateDirectory(Profile);
            if (!File.Exists(Catalog)) return new List<Game> {
                new Game { Name = "Moon Bay Rally", Art = "rally.png" },
                new Game { Name = "Starport Breakout", Art = "orbit.png" }
            };
            using (XmlReader xml = XmlReader.Create(Catalog, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null })) {
                return (List<Game>)new XmlSerializer(typeof(List<Game>)).Deserialize(xml);
            }
        }

        internal static void Save(List<Game> games) {
            string temp = Path.Combine(Profile, Guid.NewGuid().ToString("N") + ".tmp");
            try {
                using (FileStream file = File.Create(temp)) {
                    new XmlSerializer(typeof(List<Game>)).Serialize(file, games);
                    file.Flush(true);
                }
                if (File.Exists(Catalog)) File.Replace(temp, Catalog, null);
                else File.Move(temp, Catalog);
            } finally { if (File.Exists(temp)) File.Delete(temp); }
        }

        internal static string Digest(Stream stream) {
            using (SHA256 sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "");
        }

        internal static FileStream OpenBatch(string path) {
            if (String.IsNullOrWhiteSpace(path) || path.Length < 4 || path[1] != ':' || path[2] != '\\' ||
                !String.Equals(Path.GetExtension(path), ".bat", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Select a .bat file on a local drive.");
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        }

        internal static BitmapImage Decode(Stream input) {
            BitmapImage image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 1600;
            image.StreamSource = input;
            image.EndInit();
            image.Freeze();
            return image;
        }

        internal static string ImportArt(string path) {
            string key = Guid.NewGuid().ToString("N") + ".png";
            BitmapImage image;
            using (FileStream file = File.OpenRead(path)) image = Decode(file);
            PngBitmapEncoder png = new PngBitmapEncoder();
            png.Frames.Add(BitmapFrame.Create(image));
            using (FileStream file = File.Create(Path.Combine(Profile, key))) png.Save(file);
            return key;
        }

        internal static BitmapImage Image(string key) {
            if (key == "rally.png" || key == "orbit.png") {
                using (Stream stream = Resource(key)) return Decode(stream);
            }
            Guid id;
            if (String.IsNullOrEmpty(key) || !Guid.TryParseExact(Path.GetFileNameWithoutExtension(key), "N", out id)
                || key != id.ToString("N") + ".png") return null;
            using (FileStream stream = File.OpenRead(Path.Combine(Profile, key))) return Decode(stream);
        }
    }

    internal static class Startup {
        private static string Name { get { return "LauncherBox-Logon-" + Store.Sid; } }

        private static dynamic Service() {
            dynamic scheduler = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service", true));
            scheduler.Connect();
            return scheduler;
        }

        internal static bool Enabled() {
            dynamic root = Service().GetFolder("\\");
            try { return root.GetTask(Name).Enabled; }
            catch (Exception ex) {
                if (Marshal.GetHRForException(ex) == unchecked((int)0x80070002)) return false;
                throw;
            }
        }

        internal static void Set(bool enable) {
            dynamic scheduler = Service();
            dynamic root = scheduler.GetFolder("\\");
            if (!enable) { root.DeleteTask(Name, 0); return; }
            dynamic task = scheduler.NewTask(0);
            task.RegistrationInfo.Description = "Open the LauncherBox library when this account signs in.";
            task.Principal.UserId = Store.Sid;
            task.Principal.LogonType = 3; // Interactive token
            task.Principal.RunLevel = 1;  // Highest available
            dynamic trigger = task.Triggers.Create(9); // Logon
            trigger.UserId = Store.Sid;
            dynamic action = task.Actions.Create(0);
            action.Path = Store.Exe;
            action.Arguments = "--logon";
            action.WorkingDirectory = Store.Root;
            task.Settings.DisallowStartIfOnBatteries = false;
            task.Settings.StopIfGoingOnBatteries = false;
            task.Settings.ExecutionTimeLimit = "PT0S";
            task.Settings.MultipleInstances = 2; // IgnoreNew
            root.RegisterTaskDefinition(Name, task, 6 | 16, Store.Sid, null, 3,
                "D:P(A;;GA;;;SY)(A;;GA;;;BA)");
        }
    }

    internal sealed class Launcher {
        internal readonly Window Win = Store.WindowFrom("Main.xaml");
        internal List<Game> Games;
        internal int Index;
        private readonly bool logon;
        private Process running;
        private FileStream batchLock;
        private bool full;
        private WindowState previousState;
        private T Find<T>(string id) where T : class { return (T)Win.FindName(id); }

        internal Launcher(bool automatic) {
            logon = automatic;
            Games = Store.Load();
            Find<Button>("Previous").Click += delegate { Step(-1); };
            Find<Button>("Next").Click += delegate { Step(1); };
            Find<Button>("Play").Click += delegate { Launch(); };
            Find<Button>("Manage").Click += delegate { Manage(); };
            Find<Button>("Screen").Click += delegate { Fullscreen(); };
            Find<Button>("Quit").Click += delegate { Win.Close(); };
            Find<Button>("Manage").Visibility = logon ? Visibility.Collapsed : Visibility.Visible;
            Win.PreviewKeyDown += OnKey;
            Win.Closed += delegate { if (batchLock != null) batchLock.Dispose(); if (running != null) running.Dispose(); };
            if (logon) Fullscreen();
            Draw(null);
        }

        private void OnKey(object sender, KeyEventArgs e) {
            if ((Keyboard.Modifiers & (ModifierKeys.Alt | ModifierKeys.Control)) != 0) return;
            if (e.Key == Key.Left || e.Key == Key.Right) Step(e.Key == Key.Left ? -1 : 1);
            else if (e.Key == Key.F11 || (e.Key == Key.Escape && full)) { if (!e.IsRepeat) Fullscreen(); }
            else if (e.Key == Key.F2 && !logon) { if (!e.IsRepeat) Manage(); }
            else if (e.Key == Key.Enter) {
                // Keep normal keyboard activation when Tab has moved focus to another button.
                Button button = Keyboard.FocusedElement as Button;
                if (button != null && button.IsKeyboardFocused && KeyboardNavigation.GetIsTabStop(button) &&
                    button != Find<Button>("Play") && showTabFocus) return;
                if (!e.IsRepeat) Launch();
            }
            else { if (e.Key == Key.Tab) showTabFocus = true; return; }
            e.Handled = true;
        }
        private bool showTabFocus;
        private void Step(int n) {
            showTabFocus = false;
            if (Games.Count > 0) Index = (Index + n + Games.Count) % Games.Count;
            Draw(null);
        }

        private void Fullscreen() {
            if (!full) {
                previousState = Win.WindowState;
                Win.WindowState = WindowState.Normal;
                Win.WindowStyle = WindowStyle.None;
                Win.ResizeMode = ResizeMode.NoResize;
                Win.WindowState = WindowState.Maximized;
            } else {
                Win.WindowState = WindowState.Normal;
                Win.WindowStyle = WindowStyle.SingleBorderWindow;
                Win.ResizeMode = ResizeMode.CanResize;
                Win.WindowState = previousState;
            }
            full = !full;
            Find<Button>("Screen").Content = full ? "Exit Fullscreen" : "Fullscreen";
        }

        internal void Draw(string notice) {
            Index = Math.Max(0, Math.Min(Index, Games.Count - 1));
            Game game = Games.Count > 0 ? Games[Index] : null;
            bool ready = game != null && !String.IsNullOrEmpty(game.Bat);
            Find<TextBlock>("Counter").Text = String.Format("{0:D2}  /  {1:D2}", game == null ? 0 : Index + 1, Games.Count);
            Find<TextBlock>("GameName").Text = game != null ? game.Name : "No games in your library";
            Find<TextBlock>("LibraryCount").Text = "Games: " + Games.Count;
            Find<Button>("Previous").IsEnabled = Find<Button>("Next").IsEnabled = Games.Count > 1;
            Find<Button>("Play").Content = ready ? "▶   Play" : (game == null ? "Add Game" : "Choose a BAT File");
            Find<Button>("Play").IsEnabled = running == null && (ready || !logon);
            try { Find<Image>("Cover").Source = Store.Image(game == null ? null : game.Art); }
            catch (Exception) { Find<Image>("Cover").Source = null; notice = "Could not load the screenshot. Choose it again in Manage Games."; }
            Find<TextBlock>("EmptyCover").Visibility = Find<Image>("Cover").Source == null ? Visibility.Visible : Visibility.Collapsed;
            Find<TextBlock>("Status").Text = notice ?? (running != null ? "The batch file is running." : ready ? "Press Enter to play" :
                logon ? "Open LauncherBox manually to assign a BAT file to this game." : "Choose a local .bat file to play.");
        }

        private void Manage() {
            if (logon) return;
            new Editor(this).Show();
            Draw(null);
        }

        private void Launch() {
            if (running != null) return;
            if (Games.Count == 0 || String.IsNullOrEmpty(Games[Index].Bat)) { Manage(); return; }
            Game game = Games[Index];
            try {
                batchLock = Store.OpenBatch(game.Bat);
                if (!String.Equals(Store.Digest(batchLock), game.Hash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("The BAT file has changed. Open LauncherBox manually, select the file again in Manage Games, and save.");
                ProcessStartInfo info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"));
                info.UseShellExecute = false;
                info.WorkingDirectory = Path.GetDirectoryName(game.Bat);
                info.EnvironmentVariables["LAUNCHERBOX_SELECTED_BATCH"] = game.Bat;
                // Single expansion preserves literal %, !, &, spaces and parentheses in the selected path.
                info.Arguments = "/d /v:off /s /c \"\"%LAUNCHERBOX_SELECTED_BATCH%\"\"";
                Process process = new Process { StartInfo = info, EnableRaisingEvents = true };
                process.Exited += delegate {
                    Win.Dispatcher.BeginInvoke(new Action(delegate {
                        if (!Win.IsLoaded || running != process) return;
                        int code = process.ExitCode;
                        Release();
                        Win.WindowState = full ? WindowState.Maximized : beforeGame;
                        Win.Activate();
                        Draw(code == 0 ? "The batch file has finished." : "Batch file exit code: " + code);
                    }));
                };
                running = process;
                process.Start();
                beforeGame = Win.WindowState;
                Win.WindowState = WindowState.Minimized;
                Draw(null);
            } catch (Exception ex) {
                Release();
                MessageBox.Show(Win, ex.Message, "Could Not Launch Game", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        private WindowState beforeGame;
        private void Release() {
            if (running != null) { running.Dispose(); running = null; }
            if (batchLock != null) { batchLock.Dispose(); batchLock = null; }
        }
    }
    internal sealed class Editor {
        private readonly Window win = Store.WindowFrom("Manager.xaml");
        private readonly Launcher launcher;
        private readonly ListBox list;
        private Game selected;
        private string pendingArt;
        private bool pickedBatch;
        private bool binding;
        private bool dirty;
        private bool autoEnabled;
        private T Find<T>(string id) where T : class { return (T)win.FindName(id); }
        internal Editor(Launcher owner) {
            launcher = owner;
            win.Owner = owner.Win;
            list = Find<ListBox>("Entries");
            list.SelectionChanged += delegate {
                if (binding) return;
                Game next = list.SelectedItem as Game;
                if (!Discard()) { binding = true; list.SelectedItem = selected; binding = false; return; }
                Populate(next);
            };
            Find<TextBox>("TitleBox").TextChanged += delegate { if (!binding) dirty = true; };
            Find<Button>("Add").Click += delegate {
                if (!Discard()) return;
                Rebind(null);
                Find<TextBox>("TitleBox").Focus();
            };
            Find<Button>("BrowseBat").Click += delegate {
                string file = Pick("Windows batch files (*.bat)|*.bat");
                if (file != null) { Find<TextBox>("BatBox").Text = file; pickedBatch = true; dirty = true;
                    if (String.IsNullOrWhiteSpace(Find<TextBox>("TitleBox").Text)) Find<TextBox>("TitleBox").Text = Path.GetFileNameWithoutExtension(file); }
            };
            Find<Button>("BrowseCover").Click += delegate {
                string file = Pick("Game screenshots (*.png;*.jpg;*.jpeg;*.bmp)|*.png;*.jpg;*.jpeg;*.bmp");
                if (file != null) { pendingArt = file; Find<TextBox>("ImageBox").Text = file; dirty = true; }
            };
            Find<Button>("Save").Click += delegate { Guard(Save); };
            Find<Button>("Remove").Click += delegate { Guard(Remove); };
            Find<Button>("ToggleAuto").Click += delegate { Guard(delegate {
                if (!autoEnabled && !launcher.Games.Exists(g => !String.IsNullOrEmpty(g.Bat))) {
                    throw new InvalidOperationException("Save at least one game with a BAT file before enabling sign-in startup.");
                }
                Startup.Set(!autoEnabled);
                AutoStatus();
            }); };
            Find<Button>("Done").Click += delegate { win.Close(); };
            win.Closing += delegate(object s, CancelEventArgs e) { e.Cancel = !Discard(); };
            Rebind(launcher.Games.Count == 0 ? null : launcher.Games[launcher.Index]);
            AutoStatus();
        }
        private void Guard(Action action) {
            try { action(); }
            catch (Exception ex) { MessageBox.Show(win, ex.Message, "Action Failed", MessageBoxButton.OK, MessageBoxImage.Warning); }
        }
        private bool Discard() {
            return !dirty || MessageBox.Show(win, "Discard unsaved changes to this game?", "Manage Games", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
        }
        private void Rebind(Game value) {
            binding = true;
            list.ItemsSource = null;
            list.ItemsSource = launcher.Games;
            list.SelectedItem = value;
            Populate(value);
        }
        private void Populate(Game game) {
            binding = true;
            selected = game;
            pickedBatch = false;
            pendingArt = null;
            if (game != null) launcher.Index = launcher.Games.IndexOf(game);
            Find<TextBox>("TitleBox").Text = game == null ? "" : game.Name;
            Find<TextBox>("BatBox").Text = game == null ? "" : game.Bat;
            Find<TextBox>("ImageBox").Text = game == null || String.IsNullOrEmpty(game.Art) ? "" :
                game.Art == "rally.png" || game.Art == "orbit.png" ? "Built-in screenshot" : "Saved screenshot";
            Find<Button>("Remove").IsEnabled = game != null;
            binding = false;
            dirty = false;
        }
        private string Pick(string filter) {
            OpenFileDialog dialog = new OpenFileDialog { Filter = filter, CheckFileExists = true, Multiselect = false };
            return dialog.ShowDialog(win) == true ? dialog.FileName : null;
        }
        private void Save() {
            string name = Find<TextBox>("TitleBox").Text.Trim();
            string path = Find<TextBox>("BatBox").Text;
            if (name.Length == 0) throw new InvalidDataException("Enter a game name.");
            if (String.IsNullOrWhiteSpace(path)) throw new InvalidDataException("Select a .bat file to launch this game.");
            string hash = selected == null ? null : selected.Hash;
            if (pickedBatch || String.IsNullOrEmpty(hash))
                using (FileStream stream = Store.OpenBatch(path)) hash = Store.Digest(stream);
            Game game = new Game { Name = name, Bat = path, Hash = hash, Art = selected == null ? null : selected.Art };
            if (!String.IsNullOrEmpty(pendingArt)) game.Art = Store.ImportArt(pendingArt);
            List<Game> updated = new List<Game>(launcher.Games);
            if (selected != null) updated[updated.IndexOf(selected)] = game; else updated.Add(game);
            Store.Save(updated);
            launcher.Games = updated;
            Rebind(game);
        }
        private void Remove() {
            if (selected == null || MessageBox.Show(win, "Remove '" + selected.Name + "' from the library? The game and BAT file will stay on disk.", "Remove Game", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
            List<Game> updated = new List<Game>(launcher.Games);
            updated.Remove(selected);
            Store.Save(updated);
            launcher.Games = updated;
            Rebind(updated.Count > 0 ? updated[Math.Min(launcher.Index, updated.Count - 1)] : null);
        }
        private void AutoStatus() {
            try {
                autoEnabled = Startup.Enabled();
                Find<Button>("ToggleAuto").Content = autoEnabled ? "Disable Startup" : "Enable Startup";
                Find<Button>("ToggleAuto").IsEnabled = true;
                Find<TextBlock>("AutoState").Text = (autoEnabled ? "Enabled" : "Disabled") + "  ·  " + WindowsIdentity.GetCurrent().Name;
            } catch (Exception ex) {
                Find<Button>("ToggleAuto").Content = "Unavailable";
                Find<Button>("ToggleAuto").IsEnabled = false;
                Find<TextBlock>("AutoState").Text = "Could not read the scheduled task: " + ex.Message;
            }
        }
        internal void Show() { win.ShowDialog(); }
    }

    public static class Program {
        [STAThread]
        public static void Main(string[] args) {
            try {
                if (!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
                    throw new InvalidOperationException("Run LauncherBox as administrator.");
                if (!Store.Install()) return;
                bool logon = Array.IndexOf(args, "--logon") >= 0;
                bool created;
                using (Mutex mutex = new Mutex(true, "Local\\LauncherBox." + Store.Sid + (logon ? ".Display" : ".Manager"), out created)) {
                    if (!created) { if (!logon) MessageBox.Show("LauncherBox is already open.", "LauncherBox"); return; }
                    try {
                        Application app = new Application();
                        Launcher launcher = new Launcher(logon);
                        app.Run(launcher.Win);
                    } finally { mutex.ReleaseMutex(); }
                }
            } catch (Exception ex) {
                Environment.ExitCode = 1;
                MessageBox.Show(ex.GetBaseException().Message, "LauncherBox", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}
