# Using LauncherBox

Extract the ZIP to a folder of your choice. Run `LauncherBox.exe` directly on 64-bit Windows 10 or 11 and approve the administrator prompt. Game settings and screenshots live in the `Profiles` folder next to the EXE. To update, close the app and replace only the EXE.

If you used the earlier installed build, you can copy its `Profiles` folder from `%ProgramFiles%\LauncherBox` to the folder containing the portable EXE. Games with BAT files that moved need to be selected again in **Manage Games**.

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

Sign in with the local administrator account that will play the games. Keep the portable files in an administrator-controlled folder on a fixed local drive for elevated sign-in startup. Save at least one game, then choose **Enable Startup** in **Manage Games**. At the next sign-in, Task Scheduler opens the library fullscreen at the highest available privileges without another UAC prompt. Run the EXE manually to edit the library.

To troubleshoot, check the trigger, account, and last run result of `LauncherBox-Logon-<user SID>` in Task Scheduler. The action should point to the portable EXE and use `--logon`. If startup points to an older copy or you move the folder, disable startup and enable it again from the new location.

## Remove LauncherBox

If you enabled sign-in startup, disable it under each account that uses it. Close LauncherBox and delete the portable folder. Back up `Profiles` first if you want to keep the library and screenshots.
