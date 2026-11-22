$ErrorActionPreference = 'Stop'
$portable = Join-Path $env:RUNNER_TEMP ('LauncherBox SQLite check ' + [Guid]::NewGuid())
New-Item -ItemType Directory -Path $portable -Force | Out-Null
$exe = Join-Path $portable 'LauncherBox.exe'
Copy-Item 'dist/LauncherBox.exe' -Destination $exe
$assembly = [Reflection.Assembly]::LoadFrom($exe)
$static = [Reflection.BindingFlags]'NonPublic, Static'
$instance = [Reflection.BindingFlags]'NonPublic, Instance'
$store = $assembly.GetType('LauncherBox.Store', $true)
$load = $store.GetMethod('Load', $static)
$games = $load.Invoke($null, @($false))
$dbPath = Join-Path $portable 'LauncherBox.db'
$legacyRoot = Join-Path $portable 'Profiles'
if ($games.Count -ne 0 -or !(Test-Path $dbPath -PathType Leaf) -or (Test-Path $legacyRoot)) {
    throw 'The portable database must start empty beside the executable.'
}
$header = [Text.Encoding]::ASCII.GetString([IO.File]::ReadAllBytes($dbPath), 0, 15)
if ($header -ne 'SQLite format 3') { throw 'LauncherBox.db is not a SQLite database.' }

$launcherType = $assembly.GetType('LauncherBox.Launcher', $true)
$launcher = [Activator]::CreateInstance($launcherType, $instance, $null, @($false, $false), $null)
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
function Wait-ForWindow([scriptblock]$Condition, [string]$Description) {
    $clock = [Diagnostics.Stopwatch]::StartNew()
    while (!(& $Condition)) {
        if ($clock.ElapsedMilliseconds -gt 6000) { throw "Timed out waiting for $Description." }
        $window.Dispatcher.Invoke([Action]{}, [System.Windows.Threading.DispatcherPriority]::Background)
        Start-Sleep -Milliseconds 20
    }
}

$sqlite = $assembly.GetType('LauncherBox.Sqlite', $true)
$lock = [Activator]::CreateInstance($sqlite, $instance, $null, @($dbPath), $null)
$execute = $sqlite.GetMethod('Execute', $instance)
$execute.Invoke($lock, @('BEGIN EXCLUSIVE', [object[]]@()))
$bindingsField = $launcherType.GetField('Bindings', $instance)
$logPath = Join-Path $portable 'LauncherBox.log'
try {
    $window.Show()
    if ($window.WindowStyle -ne 'None' -or $window.WindowState -ne 'Maximized' -or
        $window.Background.Color.ToString() -ne '#FF000000' -or
        $null -ne $window.FindName('Cover').Source -or $null -eq $window.Icon) {
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
    if ($null -ne $bindingsField.GetValue($launcher)) { throw 'Startup settings loaded while SQLite was locked.' }
    $execute.Invoke($lock, @('COMMIT', [object[]]@()))
    Wait-ForWindow { $null -ne $bindingsField.GetValue($launcher) } 'the unlocked SQLite settings'
    $window.Activate() | Out-Null
    $deviceInput = $launcherType.GetField('devices', $instance).GetValue($launcher)
    foreach ($field in 'direct', 'xinput') {
        $worker = $deviceInput.GetType().GetField($field, $instance).GetValue($deviceInput)
        $firstPoll = $worker.GetType().GetProperty('FirstPoll', $instance).GetValue($worker)
        Wait-ForWindow { $firstPoll.IsCompleted } "$field initialization"
        if (!$firstPoll.Result) { throw "$field did not initialize. $(Get-Content $logPath -Tail 12)" }
    }
    Wait-ForWindow { (Test-Path $logPath) -and ((Get-Content $logPath -Raw) -match 'Launcher ready') } 'the portable startup log'
    $midiType = $assembly.GetType('LauncherBox.MidiInput', $true)
    $midiInput = [Activator]::CreateInstance($midiType, $instance, $null, @([IntPtr]::Zero), $null)
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
    $lock.Dispose()
    $window.Close()
}

$game = [Activator]::CreateInstance($assembly.GetType('LauncherBox.Game', $true))
$game.Name = 'Portable ' + [char]0x6E38 + "'s game"
$game.Bat = 'D:\Games\portable demo.bat'
$game.Art = [Guid]::NewGuid().ToString('N') + '.png'
$png = [Convert]::FromBase64String('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAIAAACQd1PeAAAADElEQVR4nGNgmMkAAAE2AJrwJL1AAAAAAElFTkSuQmCC')
$updated = [Activator]::CreateInstance($games.GetType())
$updated.Add($game)
$store.GetMethod('Save', $static).Invoke($null, @($games, $updated, $game.Art, $png))
$saved = $load.Invoke($null, @($false))
$image = $store.GetMethod('Image', $static)
if ($saved.Count -ne 1 -or $saved[0].Name -ne $game.Name -or $saved[0].Bat -ne $game.Bat -or
    $null -eq $image.Invoke($null, @($saved[0].Art)) -or (Test-Path $legacyRoot)) {
    throw 'The SQLite game library and screenshot did not round trip.'
}

$keyboardLauncher = [Activator]::CreateInstance($launcherType, $instance, $null, @($false, $true), $null)
$keyboardWindow = $launcherType.GetField('Win', $instance).GetValue($keyboardLauncher)
try {
    $keyboardWindow.Show()
    Wait-ForWindow { $null -ne $keyboardWindow.FindName('Cover').Source } 'the asynchronously loaded screenshot'
    $inputs = $launcherType.GetField('devices', $instance).GetValue($keyboardLauncher)
    foreach ($field in 'direct', 'xinput', 'midi') {
        if ($null -ne $inputs.GetType().GetField($field, $instance).GetValue($inputs)) {
            throw 'Keyboard-only mode initialized a hardware input backend.'
        }
    }
} finally {
    $keyboardWindow.Close()
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
if ($null -eq $match -or $match.Action -ne $play) {
    throw 'The SQLite control binding did not round trip.'
}

Move-Item $dbPath (Join-Path $portable 'fresh.db')
$legacy = Join-Path $legacyRoot 'S-1-5-21-12345-23456-34567-1001'
New-Item -ItemType Directory -Path $legacy -Force | Out-Null
$legacyGames = Join-Path $legacy 'games.xml'
@"
<ArrayOfGame>
  <Game><Name>Moon Bay Rally</Name><Art>rally.png</Art></Game>
  <Game><Name>Legacy game</Name><Bat>D:\Games\legacy.bat</Bat><Hash>ABC123</Hash><Art>$($game.Art)</Art></Game>
</ArrayOfGame>
"@ | Set-Content -Path $legacyGames -Encoding UTF8
@'
<ArrayOfInputBinding>
  <InputBinding><Action>Play</Action><Source>Midi</Source><Device>test-port</Device>
    <Code>1:note:60</Code><Label>Legacy MIDI</Label></InputBinding>
</ArrayOfInputBinding>
'@ | Set-Content -Path (Join-Path $legacy 'bindings.xml') -Encoding UTF8
[IO.File]::WriteAllBytes((Join-Path $legacy $game.Art), $png)
$legacyHash = (Get-FileHash $legacyGames).Hash
$migrated = $load.Invoke($null, @($false))
$legacyMap = [Activator]::CreateInstance($mapType, $true)
$legacyMatch = $mapType.GetMethod('Match', $instance).Invoke($legacyMap, @($signal))
if ($migrated.Count -ne 1 -or $migrated[0].Name -ne 'Legacy game' -or $migrated[0].Hash -ne 'ABC123' -or
    $legacyMatch.Label -ne 'Legacy MIDI' -or (Get-FileHash $legacyGames).Hash -ne $legacyHash) {
    throw 'The legacy game and binding were not imported intact.'
}
Move-Item $legacyRoot (Join-Path $portable 'legacy-backup')
if ($null -eq $image.Invoke($null, @($migrated[0].Art)) -or $load.Invoke($null, @($false)).Count -ne 1) {
    throw 'The migrated library still depends on its legacy profile.'
}
Write-Host 'Verified fullscreen, responsive SQLite startup, input workers, keyboard-only mode, logging, and profile migration.'
