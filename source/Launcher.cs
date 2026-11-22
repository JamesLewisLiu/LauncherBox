using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace LauncherBox {
    public sealed class Game {
        public string Name { get; set; }
        public string Bat { get; set; }
        public string Hash { get; set; }
        public string Art { get; set; }
        public string SettingsBat { get; set; }
        public string SettingsHash { get; set; }
        public string LaunchArguments { get; set; }
    }

    internal static partial class Store {
        internal static readonly string Sid = WindowsIdentity.GetCurrent().User.Value;
        internal static readonly string Exe = Assembly.GetExecutingAssembly().Location;
        internal static readonly string Root = Path.GetDirectoryName(Exe);
        internal static readonly string DatabasePath = Path.Combine(Root, "LauncherBox.db");
        private static readonly string LegacyRoot = Path.Combine(Root, "Profiles");
        private static readonly char[] BatchSeparators = { ' ', '\t', '&', '|', '<', '>', '^', '(', ')', '=', ',', ';', '%', '!' };

        internal static Window WindowFrom(string name) {
            Log.Info("Loading " + name + " layout.");
            Window window;
            using (Stream input = Resource(name)) window = (Window)XamlReader.Load(input);
            using (Stream theme = Resource("Theme.xaml"))
                window.Resources.MergedDictionaries.Add((ResourceDictionary)XamlReader.Load(theme));
            window.FontFamily = new FontFamily(new Uri("pack://application:,,,/LauncherBox;component/"),
                "./fonts/#VT323, Microsoft YaHei UI, Segoe UI");
            using (Stream icon = Resource("LauncherBox.ico"))
                window.Icon = BitmapFrame.Create(icon, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            Log.Info(name + " layout loaded.");
            return window;
        }

        internal static Stream Resource(string name) {
            Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name);
            if (stream == null) throw new InvalidDataException("Missing embedded resource " + name);
            return stream;
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

        internal static string FormatLaunchArguments(string input) {
            if (String.IsNullOrEmpty(input)) return "";
            if (input.Length > 2048) throw new InvalidDataException("Launch arguments must be 2048 characters or fewer.");
            List<string> words = new List<string>();
            StringBuilder word = new StringBuilder();
            bool inQuotes = false;
            bool started = false;
            foreach (char character in input) {
                if (Char.IsControl(character) && character != '\t')
                    throw new InvalidDataException("Launch arguments must be on a single line without control characters.");
                if (character == '"') {
                    inQuotes = !inQuotes;
                    started = true;
                } else if (!inQuotes && (character == ' ' || character == '\t')) {
                    if (!started) continue;
                    words.Add(QuoteBatchArgument(word.ToString()));
                    word.Clear();
                    started = false;
                } else {
                    word.Append(character);
                    started = true;
                }
            }
            if (inQuotes) throw new InvalidDataException("Close each double quote in the launch arguments.");
            if (started) words.Add(QuoteBatchArgument(word.ToString()));
            return String.Join(" ", words);
        }

        private static string QuoteBatchArgument(string word) {
            return word.Length == 0 || word.IndexOfAny(BatchSeparators) >= 0 ? "\"" + word + "\"" : word;
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

        internal static byte[] ImportArt(string path) {
            BitmapImage image;
            using (FileStream file = File.OpenRead(path)) image = Decode(file);
            PngBitmapEncoder png = new PngBitmapEncoder();
            png.Frames.Add(BitmapFrame.Create(image));
            using (MemoryStream output = new MemoryStream()) {
                png.Save(output);
                return output.ToArray();
            }
        }

        internal static BitmapImage Image(string key) {
            byte[] png = ReadScreenshot(key);
            if (png == null) return null;
            using (MemoryStream stream = new MemoryStream(png, false)) return Decode(stream);
        }
    }

    internal static class Startup {
        private static string Name { get { return "LauncherBox-Logon-" + Store.Sid; } }

        private const FileSystemRights ChangeEntry = FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles |
            FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership | FileSystemRights.WriteAttributes |
            FileSystemRights.WriteExtendedAttributes;
        private const FileSystemRights ChangeContents = ChangeEntry | FileSystemRights.WriteData | FileSystemRights.AppendData;
        private const AccessControlSections AccessAndOwner = AccessControlSections.Access | AccessControlSections.Owner;

        private static bool TrustedOwner(SecurityIdentifier sid) {
            return sid != null && (sid.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid) ||
                sid.IsWellKnown(WellKnownSidType.LocalSystemSid) ||
                sid.Value == "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464"); // TrustedInstaller
        }

        internal static void RequireProtectedEntry(FileSystemInfo entry, FileSystemRights changes = ChangeContents) {
            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Sign-in startup requires a folder without junctions or symbolic links: " + entry.FullName);
            DirectoryInfo dir = entry as DirectoryInfo;
            FileSystemSecurity security = dir != null ? (FileSystemSecurity)dir.GetAccessControl(AccessAndOwner) :
                ((FileInfo)entry).GetAccessControl(AccessAndOwner);
            RawSecurityDescriptor descriptor = new RawSecurityDescriptor(security.GetSecurityDescriptorBinaryForm(), 0);
            if (!TrustedOwner(descriptor.Owner) || descriptor.DiscretionaryAcl == null)
                throw new InvalidOperationException("Sign-in startup requires administrator-controlled files: " + entry.FullName);
            foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier))) {
                if (rule.AccessControlType == AccessControlType.Allow && (rule.PropagationFlags & PropagationFlags.InheritOnly) == 0 &&
                    (rule.FileSystemRights & changes) != 0 && !TrustedOwner((SecurityIdentifier)rule.IdentityReference))
                    throw new InvalidOperationException("Sign-in startup requires files and folders that standard users cannot modify: " + entry.FullName);
            }
        }

        internal static void RequireProtectedLocation() {
            string volume = Path.GetPathRoot(Store.Root);
            if (volume.Length != 3 || new DriveInfo(volume).DriveType != DriveType.Fixed)
                throw new InvalidOperationException("Sign-in startup requires a fixed local drive.");
            RequireProtectedEntry(new FileInfo(Store.Exe), ChangeContents);
            // Ancestors may allow new siblings as long as users cannot replace the existing path.
            DirectoryInfo root = new DirectoryInfo(Store.Root);
            RequireProtectedEntry(root, ChangeContents);
            for (DirectoryInfo parent = root.Parent; parent != null; parent = parent.Parent)
                RequireProtectedEntry(parent, ChangeEntry);
            FileInfo appConfig = new FileInfo(Store.Exe + ".config");
            if (appConfig.Exists) RequireProtectedEntry(appConfig, ChangeContents);
            foreach (string suffix in new[] { "", "-journal", "-wal", "-shm" }) {
                FileInfo settings = new FileInfo(Store.DatabasePath + suffix);
                if (settings.Exists) RequireProtectedEntry(settings);
            }
        }

        private static dynamic Service() {
            dynamic scheduler = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service", true));
            scheduler.Connect();
            return scheduler;
        }

        internal static string EnabledPath() {
            dynamic root = Service().GetFolder("\\");
            try {
                dynamic task = root.GetTask(Name);
                return task.Enabled ? (string)task.Definition.Actions.Item(1).Path : null;
            }
            catch (Exception ex) {
                if (Marshal.GetHRForException(ex) == unchecked((int)0x80070002)) return null;
                throw;
            }
        }

        internal static void Set(bool enable) {
            dynamic scheduler = Service();
            dynamic root = scheduler.GetFolder("\\");
            if (!enable) { root.DeleteTask(Name, 0); return; }
            RequireProtectedLocation();
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
        internal KeyMap Bindings;
        internal List<Game> Games = new List<Game>();
        internal int Index;
        private readonly bool logon;
        private Editor editor;
        private DeviceInput devices;
        private Process running;
        private FileStream batchLock;
        private bool full;
        private bool closed;
        private string displayedArt;
        private WindowState previousState;
        private T Find<T>(string id) where T : class { return (T)Win.FindName(id); }

        internal Launcher(bool automatic, bool keyboardOnly) {
            logon = automatic;
            Find<Button>("Previous").Click += delegate { Step(-1); };
            Find<Button>("Next").Click += delegate { Step(1); };
            Find<Button>("StartGame").Click += delegate { RunGameScript(false); };
            Find<Button>("GameSetting").Click += delegate { RunGameScript(true); };
            Find<Button>("Manage").Click += delegate { Manage(); };
            Find<Button>("Manage").Visibility = logon ? Visibility.Collapsed : Visibility.Visible;
            Find<Button>("Manage").IsEnabled = false;
            Win.PreviewKeyDown += OnKey;
            Win.SourceInitialized += delegate {
                Log.Info("Main window handle created.");
                HwndSource source = HwndSource.FromHwnd(new WindowInteropHelper(Win).Handle);
                devices = new DeviceInput(source, HasInputFocus, RouteInput, keyboardOnly);
                Log.Info(keyboardOnly ? "Keyboard-only mode active." : "Input monitors registered.");
            };
            Win.Loaded += LoadSettings;
            Win.ContentRendered += OnContentRendered;
            Win.Closed += delegate {
                closed = true;
                Log.Info("Main window closed.");
                if (devices != null) devices.Dispose();
                Release();
            };
            Fullscreen();
            Draw("Loading settings...");
        }

        private void OnContentRendered(object sender, EventArgs e) {
            Win.ContentRendered -= OnContentRendered;
            Log.Info("Main window rendered.");
        }

        private async void LoadSettings(object sender, RoutedEventArgs e) {
            Win.Loaded -= LoadSettings;
            try {
                Log.Info("Loading the SQLite game library.");
                List<Game> games = await Task.Run(() => Store.Load(logon));
                Log.Info("Game library loaded (" + games.Count + " games). Loading input bindings.");
                KeyMap bindings = await Task.Run(() => new KeyMap());
                if (closed) return;
                Games = games;
                Bindings = bindings;
                Find<Button>("Manage").IsEnabled = true;
                Draw(null);
                Log.Info("Launcher ready.");
            } catch (Exception ex) {
                Log.Error("Loading settings failed", ex);
                if (!closed) Draw("Could not load settings. See LauncherBox.log.");
            }
        }

        private void OnKey(object sender, KeyEventArgs e) {
            Key key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Alt) {
                e.Handled = true;
                if (!e.IsRepeat) Fullscreen();
                return;
            }
            if (e.Key == Key.System || (Keyboard.Modifiers & (ModifierKeys.Alt | ModifierKeys.Control | ModifierKeys.Windows)) != 0) return;
            if (key == Key.F11 || key == Key.Escape) {
                e.Handled = true;
                if (!e.IsRepeat) Act(key == Key.F11 ? LauncherAction.Fullscreen : LauncherAction.Exit);
                return;
            }
            if (key == Key.Tab) { showTabFocus = true; return; }
            // The Win32 poll handles these bindings; do not let WPF move button focus as well.
            if (key == Key.Left || key == Key.Right) { e.Handled = true; return; }
            // Keep normal button activation after Tab navigation.
            if ((key == Key.Enter || key == Key.Space) && !HasButtonFocus()) e.Handled = true;
        }

        private bool HasButtonFocus() {
            return showTabFocus && Keyboard.FocusedElement is Button;
        }

        internal void BeginCapture() {
            if (devices != null) devices.ResetHeld();
        }

        private bool HasInputFocus() {
            return Bindings != null && running == null && (editor == null ? Win.IsActive : editor.IsActive);
        }

        private bool RouteInput(InputSignal signal) {
            if (!HasInputFocus()) return false;
            if (editor != null) return editor.Capture(signal);
            if (signal.Source == InputSource.Keyboard && HasButtonFocus() &&
                (signal.Code == Key.Enter.ToString() || signal.Code == Key.Space.ToString())) return false;
            InputBinding binding = Bindings.Match(signal);
            if (binding == null) return false;
            if (binding.Action == LauncherAction.Settings) Win.Dispatcher.BeginInvoke(new Action(Manage));
            else Act(binding.Action);
            return true;
        }

        private void Act(LauncherAction action) {
            switch (action) {
                case LauncherAction.Previous: Step(-1); break;
                case LauncherAction.Next: Step(1); break;
                case LauncherAction.Play: RunGameScript(false); break;
                case LauncherAction.Settings: Manage(); break;
                case LauncherAction.Fullscreen: Fullscreen(); break;
                case LauncherAction.Exit: Win.Close(); break;
            }
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
        }

        internal void Draw(string notice) {
            Index = Math.Max(0, Math.Min(Index, Games.Count - 1));
            Game game = Games.Count > 0 ? Games[Index] : null;
            bool ready = game != null && !String.IsNullOrEmpty(game.Bat);
            bool hasSetting = game != null && !String.IsNullOrEmpty(game.SettingsBat);
            Find<TextBlock>("GameName").Text = game != null ? game.Name : "";
            Find<TextBlock>("GameName").Visibility = game == null ? Visibility.Collapsed : Visibility.Visible;
            Find<TextBlock>("EmptyLibrary").Text = logon ? "No games." : "No games. Open Settings to add a game.";
            Find<TextBlock>("EmptyLibrary").Visibility = game == null && Bindings != null ? Visibility.Visible : Visibility.Collapsed;
            Find<Button>("Previous").Visibility = Find<Button>("Next").Visibility = Games.Count > 1 ? Visibility.Visible : Visibility.Hidden;
            Find<Button>("StartGame").Visibility = game != null ? Visibility.Visible : Visibility.Collapsed;
            Find<Button>("StartGame").IsEnabled = running == null && (ready || !logon);
            Find<Button>("GameSetting").Visibility = game != null && (hasSetting || !logon) ? Visibility.Visible : Visibility.Collapsed;
            Find<Button>("GameSetting").IsEnabled = running == null;
            Find<TextBlock>("Status").Text = notice ?? "";
            string art = game == null ? null : game.Art;
            if (art != displayedArt) ShowScreenshot(art);
        }

        private async void ShowScreenshot(string art) {
            displayedArt = art;
            Find<Image>("Cover").Source = null;
            if (String.IsNullOrEmpty(art)) return;
            try {
                BitmapImage image = await Task.Run(() => Store.Image(art));
                if (!closed && displayedArt == art) Find<Image>("Cover").Source = image;
            } catch (Exception ex) {
                Log.Error("Loading a game screenshot failed", ex);
                if (!closed && displayedArt == art)
                    Find<TextBlock>("Status").Text = "Could not load the screenshot. Choose it again in Settings.";
            }
        }

        private void Manage() {
            if (logon || editor != null || Bindings == null || running != null) return;
            editor = new Editor(this);
            try { editor.Show(); }
            finally { editor = null; }
            Draw(null);
        }

        private async void RunGameScript(bool settings) {
            if (running != null || editor != null || Bindings == null) return;
            if (Games.Count == 0) { Manage(); return; }
            Game game = Games[Index];
            string path = settings ? game.SettingsBat : game.Bat;
            string hash = settings ? game.SettingsHash : game.Hash;
            if (String.IsNullOrEmpty(path)) { Manage(); return; }
            string action = settings ? "Opening the game setting" : "Launching the game";
            try {
                string arguments = settings ? "" : Store.FormatLaunchArguments(game.LaunchArguments);
                batchLock = Store.OpenBatch(path);
                if (!String.Equals(Store.Digest(batchLock), hash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("The BAT file has changed. Open LauncherBox manually, select the file again in Settings, and save.");
                ProcessStartInfo info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"));
                info.UseShellExecute = false;
                info.WorkingDirectory = Path.GetDirectoryName(path);
                info.EnvironmentVariables["LAUNCHERBOX_SELECTED_BATCH"] = path;
                // Expand the path and safely quoted arguments once with delayed expansion disabled.
                string command = "\"%LAUNCHERBOX_SELECTED_BATCH%\"";
                if (arguments.Length > 0) {
                    info.EnvironmentVariables["LAUNCHERBOX_BATCH_ARGUMENTS"] = arguments;
                    command += " %LAUNCHERBOX_BATCH_ARGUMENTS%";
                }
                info.Arguments = "/d /v:off /s /c \"" + command + "\"";
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
                if (devices != null) await devices.ReleaseForGame();
                if (closed) return;
                process.Start();
                Log.Info(action + ": BAT process started.");
                beforeGame = Win.WindowState;
                if (settings) Win.WindowState = WindowState.Minimized;
                Draw(null);
            } catch (Exception ex) {
                Log.Error(action + " failed", ex);
                Release();
                if (!closed) MessageBox.Show(Win, ex.Message, action + " failed", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        private WindowState beforeGame;
        private void Release() {
            if (running != null) { running.Dispose(); running = null; }
            if (batchLock != null) { batchLock.Dispose(); batchLock = null; }
        }
    }
    internal sealed class Editor {
        private const string BatchFilter = "Windows batch files (*.bat)|*.bat";
        private readonly Window win = Store.WindowFrom("Manager.xaml");
        private readonly Launcher launcher;
        private readonly ListBox list;
        private Game selected;
        private string pendingArt;
        private bool pickedBatch;
        private bool pickedSettingsBatch;
        private bool binding;
        private bool dirty;
        private bool autoEnabled;
        private InputSource inputSource;
        private LauncherAction? awaiting;
        private readonly Dictionary<InputSource, Button> sourceButtons = new Dictionary<InputSource, Button>();
        internal bool IsActive { get { return win.IsActive; } }
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
            Find<TextBox>("LaunchArgumentsBox").TextChanged += delegate { if (!binding) dirty = true; };
            Find<Button>("Add").Click += delegate {
                if (!Discard()) return;
                Rebind(null);
                Find<TextBox>("TitleBox").Focus();
            };
            Find<Button>("BrowseBat").Click += delegate {
                string file = Pick(BatchFilter);
                if (file != null) { Find<TextBox>("BatBox").Text = file; pickedBatch = true; dirty = true;
                    if (String.IsNullOrWhiteSpace(Find<TextBox>("TitleBox").Text)) Find<TextBox>("TitleBox").Text = Path.GetFileNameWithoutExtension(file); }
            };
            Find<Button>("BrowseSettingsBat").Click += delegate {
                string file = Pick(BatchFilter);
                if (file == null) return;
                Find<TextBox>("SettingsBatBox").Text = file;
                Find<Button>("ClearSettingsBat").IsEnabled = true;
                pickedSettingsBatch = true;
                dirty = true;
            };
            Find<Button>("ClearSettingsBat").Click += delegate {
                Find<TextBox>("SettingsBatBox").Clear();
                Find<Button>("ClearSettingsBat").IsEnabled = false;
                dirty = true;
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
            Find<Button>("GamesTab").Click += delegate { ShowControls(false); };
            Find<Button>("ControlsTab").Click += delegate { ShowControls(true); };
            AddSource("KeyboardSource", InputSource.Keyboard);
            AddSource("DirectInputSource", InputSource.DirectInput);
            AddSource("XInputSource", InputSource.XInput);
            AddSource("MidiSource", InputSource.Midi);
            Find<Button>("ResetBindings").Click += delegate { Guard(delegate {
                launcher.Bindings.Reset(inputSource);
                awaiting = null;
                DrawBindings("Defaults restored.");
            }); };
            win.PreviewKeyDown += CaptureKey;
            win.Closing += delegate(object s, CancelEventArgs e) { e.Cancel = !Discard(); };
            Rebind(launcher.Games.Count == 0 ? null : launcher.Games[launcher.Index]);
            AutoStatus();
            ShowControls(false);
        }

        private void AddSource(string id, InputSource source) {
            Button button = Find<Button>(id);
            sourceButtons.Add(source, button);
            button.Click += delegate {
                inputSource = source;
                awaiting = null;
                DrawBindings(null);
            };
        }

        private void SelectButton(Button button, bool selected) {
            if (selected) button.SetResourceReference(Control.ForegroundProperty, "Accent");
            else button.ClearValue(Control.ForegroundProperty);
        }

        private void ShowControls(bool controls) {
            awaiting = null;
            Find<Grid>("GamesPane").Visibility = controls ? Visibility.Collapsed : Visibility.Visible;
            Find<Grid>("ControlsPane").Visibility = controls ? Visibility.Visible : Visibility.Collapsed;
            SelectButton(Find<Button>("GamesTab"), !controls);
            SelectButton(Find<Button>("ControlsTab"), controls);
            if (controls) DrawBindings(null);
        }

        private void DrawBindings(string notice) {
            foreach (KeyValuePair<InputSource, Button> source in sourceButtons)
                SelectButton(source.Value, source.Key == inputSource);
            StackPanel rows = Find<StackPanel>("BindingRows");
            rows.Children.Clear();
            foreach (LauncherAction action in KeyMap.Actions) {
                LauncherAction target = action;
                string label = KeyMap.Label(action);
                InputBinding binding = launcher.Bindings.Get(action, inputSource);
                DockPanel row = new DockPanel { Margin = new Thickness(0, 3, 0, 3) };
                Button clear = new Button { Content = "Clear", IsEnabled = binding != null };
                AutomationProperties.SetName(clear, "Clear " + label);
                DockPanel.SetDock(clear, Dock.Right);
                clear.Click += delegate { Guard(delegate {
                    launcher.Bindings.Set(target, inputSource, null);
                    awaiting = null;
                    DrawBindings(null);
                }); };
                row.Children.Add(clear);
                TextBlock name = new TextBlock { Text = label, Width = 145, VerticalAlignment = VerticalAlignment.Center };
                DockPanel.SetDock(name, Dock.Left);
                row.Children.Add(name);
                TextBlock caption = new TextBlock {
                    Text = binding == null ? "Bind" : binding.Label,
                    TextTrimming = TextTrimming.CharacterEllipsis
                };
                Button assign = new Button {
                    Content = caption, ToolTip = "Bind " + label, HorizontalContentAlignment = HorizontalAlignment.Left
                };
                AutomationProperties.SetName(assign, "Bind " + label + (binding == null ? "" : ", " + binding.Label));
                assign.Click += delegate {
                    launcher.BeginCapture();
                    awaiting = target;
                    Find<TextBlock>("BindingStatus").Text = "Send an input for " + label + ". Esc cancels.";
                };
                row.Children.Add(assign);
                rows.Children.Add(row);
            }
            Find<TextBlock>("BindingStatus").Text = notice ??
                (inputSource == InputSource.Keyboard ? "F11, Alt+Enter, Esc and Alt+F4 are always available." : "");
        }

        private void CaptureKey(object sender, KeyEventArgs e) {
            if (e.Key == Key.Escape) {
                e.Handled = true;
                if (e.IsRepeat) return;
                if (awaiting.HasValue) {
                    awaiting = null;
                    DrawBindings("Binding canceled.");
                } else win.Close();
                return;
            }
            if (!awaiting.HasValue || inputSource != InputSource.Keyboard || e.Key == Key.System ||
                (Keyboard.Modifiers & (ModifierKeys.Alt | ModifierKeys.Control | ModifierKeys.Windows)) != 0) return;
            e.Handled = true;
            if (!e.IsRepeat && NaiveKeyboard.Reserved(e.Key))
                Find<TextBlock>("BindingStatus").Text = "Choose another key. This key is reserved.";
        }

        internal bool Capture(InputSignal signal) {
            if (!awaiting.HasValue || signal.Source != inputSource) return false;
            LauncherAction target = awaiting.Value;
            awaiting = null;
            Guard(delegate {
                launcher.Bindings.Set(target, inputSource, signal);
                DrawBindings("Bound " + KeyMap.Label(target) + ".");
            });
            return true;
        }
        private void Guard(Action action) {
            try { action(); }
            catch (Exception ex) {
                Log.Error("Updating settings failed", ex);
                MessageBox.Show(win, ex.Message, "Action Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        private bool Discard() {
            return !dirty || MessageBox.Show(win, "Discard unsaved changes to this game?", "Settings", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
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
            pickedSettingsBatch = false;
            pendingArt = null;
            if (game != null) launcher.Index = launcher.Games.IndexOf(game);
            Find<TextBox>("TitleBox").Text = game == null ? "" : game.Name;
            Find<TextBox>("BatBox").Text = game == null ? "" : game.Bat;
            Find<TextBox>("LaunchArgumentsBox").Text = game == null ? "" : game.LaunchArguments;
            Find<TextBox>("SettingsBatBox").Text = game == null ? "" : game.SettingsBat;
            Find<Button>("ClearSettingsBat").IsEnabled = game != null && !String.IsNullOrEmpty(game.SettingsBat);
            Find<TextBox>("ImageBox").Text = game != null && !String.IsNullOrEmpty(game.Art) ? "Saved screenshot" : "";
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
            string arguments = Find<TextBox>("LaunchArgumentsBox").Text.Trim();
            Store.FormatLaunchArguments(arguments);
            string settingsPath = Find<TextBox>("SettingsBatBox").Text;
            Game game = new Game {
                Name = name, Bat = path, Hash = BatchHash(path, selected == null ? null : selected.Hash, pickedBatch),
                Art = selected == null ? null : selected.Art,
                LaunchArguments = arguments.Length > 0 ? arguments : null
            };
            if (!String.IsNullOrWhiteSpace(settingsPath)) {
                game.SettingsBat = settingsPath;
                game.SettingsHash = BatchHash(settingsPath, selected == null ? null : selected.SettingsHash, pickedSettingsBatch);
            }
            byte[] png = String.IsNullOrEmpty(pendingArt) ? null : Store.ImportArt(pendingArt);
            if (png != null) game.Art = Guid.NewGuid().ToString("N") + ".png";
            List<Game> updated = new List<Game>(launcher.Games);
            if (selected != null) updated[updated.IndexOf(selected)] = game; else updated.Add(game);
            Store.Save(launcher.Games, updated, game.Art, png);
            launcher.Games = updated;
            Rebind(game);
        }

        private static string BatchHash(string path, string previous, bool picked) {
            if (!picked && !String.IsNullOrEmpty(previous)) return previous;
            using (FileStream stream = Store.OpenBatch(path)) return Store.Digest(stream);
        }
        private void Remove() {
            if (selected == null || MessageBox.Show(win, "Remove '" + selected.Name + "' from the library? The game and BAT file will stay on disk.", "Remove Game", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
            List<Game> updated = new List<Game>(launcher.Games);
            updated.Remove(selected);
            Store.Save(launcher.Games, updated);
            launcher.Games = updated;
            Rebind(updated.Count > 0 ? updated[Math.Min(launcher.Index, updated.Count - 1)] : null);
        }
        private void AutoStatus() {
            try {
                string path = Startup.EnabledPath();
                autoEnabled = path != null;
                Find<Button>("ToggleAuto").Content = autoEnabled ? "Disable Startup" : "Enable Startup";
                Find<Button>("ToggleAuto").IsEnabled = true;
                Find<TextBlock>("AutoState").Text = autoEnabled && !String.Equals(path, Store.Exe, StringComparison.OrdinalIgnoreCase) ?
                    "Enabled for another copy: " + path : (autoEnabled ? "Enabled" : "Disabled") + "  ·  " + WindowsIdentity.GetCurrent().Name;
            } catch (Exception ex) {
                Log.Error("Reading the sign-in task failed", ex);
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
            AppDomain.CurrentDomain.UnhandledException += delegate(object sender, UnhandledExceptionEventArgs e) {
                Log.Info("Unhandled exception: " + e.ExceptionObject);
                Log.Finish();
            };
            try {
                bool logon = Array.IndexOf(args, "--logon") >= 0;
                bool keyboardOnly = Array.IndexOf(args, "--keyboard-only") >= 0;
                Log.Info("LauncherBox starting (" + (logon ? "sign-in" : "manual") +
                    (keyboardOnly ? ", keyboard only" : "") + "). Windows " + Environment.OSVersion.Version + ".");
                if (!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
                    throw new InvalidOperationException("Run LauncherBox as administrator.");
                if (logon) {
                    Log.Info("Checking the protected sign-in location.");
                    Startup.RequireProtectedLocation();
                }
                bool created;
                using (Mutex mutex = new Mutex(true, "Local\\LauncherBox." + Store.Sid + (logon ? ".Display" : ".Manager"), out created)) {
                    if (!created) { if (!logon) MessageBox.Show("LauncherBox is already open.", "LauncherBox"); return; }
                    try {
                        Application app = new Application();
                        Launcher launcher = new Launcher(logon, keyboardOnly);
                        Log.Info("Starting the window message loop.");
                        app.Run(launcher.Win);
                    } finally { mutex.ReleaseMutex(); }
                }
            } catch (Exception ex) {
                Log.Error("LauncherBox stopped unexpectedly", ex);
                Environment.ExitCode = 1;
                MessageBox.Show(ex.GetBaseException().Message + "\n\nSee LauncherBox.log.", "LauncherBox", MessageBoxButton.OK, MessageBoxImage.Error);
            } finally {
                Log.Info("LauncherBox exiting.");
                Log.Finish();
            }
        }
    }
}
