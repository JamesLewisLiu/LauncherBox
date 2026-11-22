$ErrorActionPreference = 'Stop'
$portable = Join-Path $env:RUNNER_TEMP 'LauncherBox portable check'
New-Item -ItemType Directory -Path $portable -Force | Out-Null
$exe = Join-Path $portable 'LauncherBox.exe'
Copy-Item 'dist/LauncherBox.exe' -Destination $exe
$assembly = [Reflection.Assembly]::LoadFrom($exe)
$static = [Reflection.BindingFlags]'NonPublic, Static'
$instance = [Reflection.BindingFlags]'NonPublic, Instance'
$store = $assembly.GetType('LauncherBox.Store', $true)
$load = $store.GetMethod('Load', $static)
$games = $load.Invoke($null, $null)
$sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$profile = Join-Path $portable "Profiles\$sid"
if ($games.Count -ne 0 -or !(Test-Path $profile -PathType Container)) {
    throw 'The portable profile must start with an empty library.'
}

$launcherType = $assembly.GetType('LauncherBox.Launcher', $true)
$launcher = [Activator]::CreateInstance($launcherType, $instance, $null, @($false), $null)
$window = $launcherType.GetField('Win', $instance).GetValue($launcher)
$fullscreen = $launcherType.GetMethod('Fullscreen', $instance)
Add-Type -AssemblyName System.Windows.Forms
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class WindowBounds {
    [StructLayout(LayoutKind.Sequential)]
    public struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr handle, out Rect rect);
}
'@
try {
    $window.Show()
    if ($window.WindowStyle -ne 'None' -or $window.WindowState -ne 'Maximized' -or
        $window.Background.Color.ToString() -ne '#FF000000' -or $null -ne $window.FindName('Cover').Source) {
        throw 'LauncherBox must open fullscreen with a black background and no image.'
    }
    $hwnd = [System.Windows.Interop.WindowInteropHelper]::new($window).Handle
    $monitor = [System.Windows.Forms.Screen]::FromHandle($hwnd).Bounds
    $bounds = New-Object 'WindowBounds+Rect'
    if (![WindowBounds]::GetWindowRect($hwnd, [ref]$bounds) -or
        $bounds.Left -ne $monitor.Left -or $bounds.Top -ne $monitor.Top -or
        $bounds.Right -ne $monitor.Right -or $bounds.Bottom -ne $monitor.Bottom) {
        throw "Fullscreen bounds $($bounds.Left),$($bounds.Top),$($bounds.Right),$($bounds.Bottom) did not cover the monitor $monitor."
    }
    $fullscreen.Invoke($launcher, $null)
    if ($window.WindowState -ne 'Normal') { throw 'Fullscreen did not toggle off.' }
    $deviceInput = $launcherType.GetField('devices', $instance).GetValue($launcher)
    $direct = $deviceInput.GetType().GetField('direct', $instance).GetValue($deviceInput)
    $direct.GetType().GetMethod('Refresh', $instance).Invoke($direct, $null)
    if ($null -eq $direct.GetType().GetField('input', $instance).GetValue($direct)) {
        throw 'DirectInput did not initialize.'
    }
    foreach ($type in 'NaiveKeyboard', 'XInput') {
        $read = $assembly.GetType("LauncherBox.$type", $true).GetMethod('Read', $static)
        $signals = [Activator]::CreateInstance($read.GetParameters()[0].ParameterType)
        $read.Invoke($null, (,$signals))
    }
    $midiInput = $deviceInput.GetType().GetField('midi', $instance).GetValue($deviceInput)
    $midiType = $midiInput.GetType()
    $port = [Activator]::CreateInstance($midiType.GetNestedType('Port', [Reflection.BindingFlags]::NonPublic), $true)
    $port.GetType().GetField('Id', $instance).SetValue($port, 'test-port')
    $port.GetType().GetField('Name', $instance).SetValue($port, 'Test Port')
    $ports = $midiType.GetField('ports', $instance).GetValue($midiInput)
    $handle = [IntPtr]123
    $ports.Add($handle, $port)
    try {
        $decode = $midiType.GetMethod('Decode', $instance)
        $note = $decode.Invoke($midiInput, @($handle, [uint32]0x643C90))
        if ($null -eq $note -or $note.GetType().GetField('Code', $instance).GetValue($note) -ne '1:note:60' -or
            $null -ne $decode.Invoke($midiInput, @($handle, [uint32]0x003C90)) -or
            $null -eq $decode.Invoke($midiInput, @($handle, [uint32]0x4001B0)) -or
            $null -ne $decode.Invoke($midiInput, @($handle, [uint32]0x4101B0))) {
            throw 'MIDI note and control change decoding did not match the expected input edges.'
        }
    } finally {
        [void]$ports.Remove($handle)
    }
    $settings = $store.GetMethod('WindowFrom', $static).Invoke($null, @('Manager.xaml'))
    if ($null -eq $settings.FindName('ControlsPane') -or $null -eq $settings.FindResource('Accent')) {
        throw 'The settings layout and shared theme did not load.'
    }
    $settings.Close()
} finally {
    $window.Close()
}

$game = [Activator]::CreateInstance($assembly.GetType('LauncherBox.Game', $true))
$game.Name = 'Portable test game'
$games.Add($game)
$store.GetMethod('Save', $static).Invoke($null, (,$games))
$saved = $load.Invoke($null, $null)
if ($saved.Count -ne 1 -or $saved[0].Name -ne $game.Name -or !(Test-Path (Join-Path $profile 'games.xml') -PathType Leaf)) {
    throw 'The portable game library did not round trip.'
}

$mapType = $assembly.GetType('LauncherBox.KeyMap', $true)
$map = [Activator]::CreateInstance($mapType, $true)
$play = [Enum]::Parse($assembly.GetType('LauncherBox.LauncherAction', $true), 'Play')
$midi = [Enum]::Parse($assembly.GetType('LauncherBox.InputSource', $true), 'Midi')
$signalType = $assembly.GetType('LauncherBox.InputSignal', $true)
$signal = [Activator]::CreateInstance($signalType, $instance, $null, @($midi, 'test-port', '1:note:60', 'Test Port · Note 60'), $null)
$mapType.GetMethod('Set', $instance).Invoke($map, @($play, $midi, $signal))
$reloaded = [Activator]::CreateInstance($mapType, $true)
$match = $mapType.GetMethod('Match', $instance).Invoke($reloaded, @($signal))
if ($null -eq $match -or $match.Action -ne $play -or !(Test-Path (Join-Path $profile 'bindings.xml') -PathType Leaf)) {
    throw 'The portable control binding did not round trip.'
}
Write-Host 'Verified monitor-sized startup, an empty library, Windows input initialization, MIDI decoding, and portable settings.'
