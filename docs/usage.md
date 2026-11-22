# Using LauncherBox

Run `LauncherBox.exe` on 64-bit Windows 10 or 11 and approve the administrator prompt. Confirm installation to `%ProgramFiles%\LauncherBox`. Use the desktop shortcut for subsequent manual launches or close the installed app and run a newer build to update it.

Press **F2** to open **Manage Games**. Edit a sample or choose **Add**, enter a name, select a local `.bat` file and an optional PNG, JPEG, or BMP screenshot, then choose **Save Game**. Press **Esc** to leave fullscreen.

## BAT files

The BAT runs with administrator privileges in its own folder. Use trusted scripts and keep them and their dependencies in locations standard users cannot modify. LauncherBox checks the BAT file's SHA-256 hash. If you edit it, select it again in **Manage Games** and save.

LauncherBox returns when the BAT process exits. If the BAT uses `start` to launch a game, add `/wait` to return after the game closes.

```bat
@echo off
start "" /wait "%~dp0MyGame.exe"
exit /b %errorlevel%
```

## Start at sign-in

Sign in with the local administrator account that will play the games. Save at least one game, then choose **Enable Startup** in **Manage Games**. At the next sign-in, Task Scheduler opens the library fullscreen at the highest available privileges without another UAC prompt. The library waits for you to select a game. Open the desktop shortcut manually to edit the library.

To troubleshoot, check the trigger, account, and last run result of `LauncherBox-Logon-<user SID>` in Task Scheduler. The action should run the installed `LauncherBox.exe` with `--logon`.

## Uninstall

Choose **Disable Startup** under each account that enabled it. Close LauncherBox, remove its desktop shortcut, and delete `%ProgramFiles%\LauncherBox`. Back up its `Profiles` folder first if you want to keep your library and screenshots.
