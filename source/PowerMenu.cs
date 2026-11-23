using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace LauncherBox {
    internal enum PowerAction { Exit, Shutdown, Restart }

    internal sealed class PowerMenu {
        private readonly Window win = Store.WindowFrom("Power.xaml");
        private readonly Button[] choices;
        private readonly TextBlock prompt;
        private int selected;
        private PowerAction? confirmation;
        internal PowerAction? Result { get; private set; }
        internal bool IsActive { get { return win.IsActive; } }

        internal PowerMenu(Window owner) {
            win.Owner = owner;
            prompt = (TextBlock)win.FindName("Prompt");
            choices = new[] {
                (Button)win.FindName("FirstChoice"),
                (Button)win.FindName("SecondChoice"),
                (Button)win.FindName("ThirdChoice")
            };
            for (int i = 0; i < choices.Length; i++) {
                int choice = i;
                choices[i].Click += delegate { Pick(choice); };
                choices[i].GotKeyboardFocus += delegate { Select(choice, false); };
            }
            win.Loaded += delegate { choices[selected].Focus(); };
            win.PreviewKeyDown += OnKey;
            ShowChoices(null);
        }

        internal PowerAction? Show() {
            win.ShowDialog();
            return Result;
        }

        internal bool Handle(LauncherAction action) {
            int count = confirmation.HasValue ? 2 : 3;
            switch (action) {
                case LauncherAction.Previous: Select((selected + count - 1) % count, true); return true;
                case LauncherAction.Next: Select((selected + 1) % count, true); return true;
                case LauncherAction.Play: Pick(selected); return true;
                case LauncherAction.Exit: Back(); return true;
                case LauncherAction.PowerMenu: win.Close(); return true;
                default: return false;
            }
        }

        private void OnKey(object sender, KeyEventArgs e) {
            // The launcher routes configured inputs here while this window is active.
            if (e.Key == Key.Left || e.Key == Key.Right || e.Key == Key.Enter || e.Key == Key.Space)
                e.Handled = true;
            else if (e.Key == Key.Escape) {
                e.Handled = true;
                if (!e.IsRepeat) Back();
            }
        }

        private void Back() {
            if (confirmation.HasValue) ShowChoices(null);
            else win.Close();
        }

        private void ShowChoices(PowerAction? action) {
            confirmation = action;
            string command = action == PowerAction.Shutdown ? "Shut down" : "Restart";
            prompt.Text = action.HasValue ? command + " this PC?" : "Power";
            choices[0].Content = action.HasValue ? "Cancel" : "Exit LauncherBox";
            choices[1].Content = action.HasValue ? command : "Shut down";
            choices[2].Content = "Restart";
            choices[2].Visibility = action.HasValue ? Visibility.Collapsed : Visibility.Visible;
            // Keep the confirmation action away from the original shutdown and restart buttons.
            Grid.SetColumn(choices[0], action.HasValue ? 2 : 0);
            Grid.SetColumn(choices[1], action.HasValue ? 0 : 1);
            Select(0, true);
        }

        private void Select(int index, bool focus) {
            selected = index;
            for (int i = 0; i < choices.Length; i++) {
                if (i == selected) choices[i].SetResourceReference(Control.ForegroundProperty, "Accent");
                else choices[i].ClearValue(Control.ForegroundProperty);
            }
            if (focus && win.IsLoaded) choices[index].Focus();
        }

        private void Pick(int index) {
            if (confirmation.HasValue) {
                if (index == 0) { ShowChoices(null); return; }
                Result = confirmation;
            } else {
                if (index != 0) { ShowChoices(index == 1 ? PowerAction.Shutdown : PowerAction.Restart); return; }
                Result = PowerAction.Exit;
            }
            win.Close();
        }

        internal static ProcessStartInfo CreateCommand(PowerAction action) {
            if (action != PowerAction.Shutdown && action != PowerAction.Restart)
                throw new ArgumentOutOfRangeException("action");

            return new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "shutdown.exe")) {
                Arguments = action == PowerAction.Shutdown ? "/s /t 0" : "/r /t 0",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true
            };
        }
    }
}
