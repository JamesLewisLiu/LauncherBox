@echo off
setlocal EnableExtensions DisableDelayedExpansion

set "FX=%SystemRoot%\Microsoft.NET\Framework64\v4.0.30319"
if not exist "%FX%\csc.exe" (
  echo Building LauncherBox requires 64-bit Windows with .NET Framework 4.x. >&2
  exit /b 1
)

set "OUT=%~dp0dist"
if not exist "%OUT%\" mkdir "%OUT%" || exit /b 1
pushd "%~dp0source" || exit /b 1
"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -NonInteractive -Command ^
  "$ErrorActionPreference='Stop'; $writer=New-Object System.Resources.ResourceWriter -ArgumentList (Join-Path $env:OUT 'LauncherBox.g.resources'); try { $writer.AddResource('fonts/vt323-regular.ttf', [IO.File]::OpenRead((Join-Path (Get-Location) 'fonts\VT323-Regular.ttf')), $true) } finally { $writer.Dispose() }"
if not errorlevel 1 call :compile
set "RESULT=%ERRORLEVEL%"
popd
exit /b %RESULT%

:compile
"%FX%\csc.exe" /nologo /target:winexe /platform:x64 /optimize+ /codepage:65001 ^
  /win32manifest:app.manifest /win32icon:LauncherBox.ico /out:"%OUT%\LauncherBox.exe" ^
  /r:"%FX%\WPF\PresentationCore.dll" /r:"%FX%\WPF\PresentationFramework.dll" ^
  /r:"%FX%\WPF\WindowsBase.dll" /r:"%FX%\System.Xaml.dll" /r:"%FX%\Microsoft.CSharp.dll" ^
  /resource:Main.xaml,Main.xaml /resource:Manager.xaml,Manager.xaml ^
  /resource:Theme.xaml,Theme.xaml /resource:LauncherBox.ico,LauncherBox.ico ^
  /resource:"%OUT%\LauncherBox.g.resources",LauncherBox.g.resources ^
  Launcher.cs Input.cs DirectInput.cs MidiInput.cs Settings.cs Sqlite.cs Diagnostics.cs GamepadWorker.cs
if errorlevel 1 exit /b %ERRORLEVEL%
copy /Y "fonts\OFL.txt" "%OUT%\VT323-LICENSE.txt" >nul
exit /b %ERRORLEVEL%
