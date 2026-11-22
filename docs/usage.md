# Using LauncherBox

Extract the ZIP to a folder of your choice. Run `LauncherBox.exe` directly on 64-bit Windows 10 or 11 and approve the administrator prompt. Games, screenshots, and control bindings live in `LauncherBox.db`, a SQLite database next to the EXE. Close LauncherBox before copying the database to another folder or PC. To update, close the app and replace only the EXE. To build from source, run `Build.cmd` on Windows.

On first run, LauncherBox imports the old `Profiles` data beside the EXE. It uses the current Windows account’s profile or the only legacy profile with settings. If several legacy profiles exist and none matches the current account, move the ones you want to skip to a backup folder and restart. The source files are kept. If you moved a BAT file, select it again in **Settings → Games**.

LauncherBox opens fullscreen with an empty library. Open the gear menu or press **F2** to open **Settings**. In **Games**, choose **Add**, enter a name, select a local `.bat` file and an optional PNG, JPEG, or BMP screenshot, then choose **Save Game**.

## Controls

**← / →** browse, **Enter** plays, and **F2** opens settings by default. **F11** or **Alt+Enter** toggles fullscreen. **Esc** or **Alt+F4** exits from the library.

In **Settings → Controls**, select **Naive keyboard**, **DirectInput**, **XInput**, or **MIDI**, then select the binding next to an action and send an input. Naive keyboard reads Windows virtual keys while the launcher has focus. Each action has one binding per input type. Changes save immediately. **Esc** cancels capture, and **Clear** removes a binding. Leave sticks and pedals at rest when connecting a DirectInput device.

MIDI supports note strikes, control-change values crossing 64, and pitch bends in either direction. A zero-velocity Note On is ignored. MIDI bindings include the port and channel. XInput bindings follow the controller slot.

## BAT files

The BAT runs with administrator privileges in its own folder. Use trusted scripts and keep them and their dependencies in locations standard users cannot modify. LauncherBox checks the BAT file's SHA-256 hash. If you edit it, select it again in **Settings → Games** and save.

LauncherBox returns when the BAT process exits. If the BAT uses `start` to launch a game, add `/wait` to return after the game closes.

```bat
@echo off
start "" /wait "%~dp0MyGame.exe"
exit /b %errorlevel%
```

## Start at sign-in

Sign in with the local administrator account that will play the games. Keep the portable files in an administrator-controlled folder on a fixed local drive for elevated sign-in startup. Save at least one game, then choose **Enable Startup** in **Settings → Games**. At the next sign-in, Task Scheduler opens the library fullscreen at the highest available privileges without another UAC prompt. Run the EXE manually to edit the library.

To troubleshoot, check the trigger, account, and last run result of `LauncherBox-Logon-<user SID>` in Task Scheduler. The action should point to the portable EXE and use `--logon`. If startup points to an older copy or you move the folder, disable startup and enable it again from the new location.

## Remove LauncherBox

If you enabled sign-in startup, disable it under each account that uses it. Close LauncherBox and delete the portable folder. Back up `LauncherBox.db` first to keep your settings.
