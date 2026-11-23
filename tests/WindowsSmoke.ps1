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
$lock = $sqlite.GetConstructor($instance, $null, @([string]), $null).Invoke(@("$dbPath"))
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
    $pixelFontLoaded = $false
    foreach ($typeface in $window.FindName('GameName').FontFamily.GetTypefaces()) {
        $glyph = $null
        if ($typeface.TryGetGlyphTypeface([ref]$glyph) -and $glyph.FamilyNames.Values -contains 'VT323') {
            $pixelFontLoaded = $true
            break
        }
    }
    if (!$pixelFontLoaded -or $window.FindName('GameName').Foreground.Color.ToString() -ne '#FF33FF33' -or
        $window.FindName('Previous').Content.Height -ne 50 -or $window.FindName('Next').Content.Height -ne 50) {
        throw 'The embedded pixel font, terminal green, or enlarged arrows did not load.'
    }
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
    $settingsChrome = [System.Windows.Shell.WindowChrome]::GetWindowChrome($settings)
    if ($settings.WindowStyle -ne 'None' -or $null -eq $settingsChrome -or $settingsChrome.CaptionHeight -le 0) {
        throw 'The settings window must be borderless and retain a draggable header.'
    }
    $settings.Close()
} finally {
    $lock.Dispose()
    $window.Close()
}

$game = [Activator]::CreateInstance($assembly.GetType('LauncherBox.Game', $true))
$game.Name = 'Portable ' + [char]0x6E38 + "'s game"
$startDir = Join-Path $portable 'Launch BAT'
$settingsDir = Join-Path $portable 'Setting BAT'
$cmdDir = Join-Path $portable 'Launch CMD'
$exeDir = Join-Path $portable 'Launch EXE'
New-Item -ItemType Directory -Path $startDir, $settingsDir, $cmdDir, $exeDir | Out-Null
$launchBat = Join-Path $startDir 'start game.bat'
$settingBat = Join-Path $settingsDir 'game setting.bat'
$launchCmd = Join-Path $cmdDir 'start game.CMD'
$launchExe = Join-Path $exeDir 'start game.exe'
@'
@echo off
setlocal DisableDelayedExpansion
for %%G in (1 2 3 4 5 6 7 8 9) do if "%~1"=="%%G" goto game%%G
if "%~1"=="0" goto game10
if not "%~1"=="" exit /b 2
>manual.marker echo MENU
exit /b 0
:game1
>start-1.marker echo "%~2" "%~3" "%~4"
exit /b 0
:game10
>start-0.marker echo ZERO
exit /b 0
'@ | Set-Content -Path $launchBat -Encoding ASCII
'@echo off', '>setting.marker echo "%~1"' | Set-Content -Path $settingBat -Encoding ASCII
'@echo off', '>cmd.marker echo "%~1"' | Set-Content -Path $launchCmd -Encoding ASCII
Add-Type -OutputAssembly $launchExe -OutputType WindowsApplication -TypeDefinition @'
using System.IO;

public static class LaunchProbe {
    public static void Main(string[] args) {
        File.WriteAllLines("exe.marker", args);
    }
}
'@
$startMarker = Join-Path $startDir 'start-1.marker'
$zeroMarker = Join-Path $startDir 'start-0.marker'
$manualMarker = Join-Path $startDir 'manual.marker'
$settingMarker = Join-Path $settingsDir 'setting.marker'
$cmdMarker = Join-Path $cmdDir 'cmd.marker'
$exeMarker = Join-Path $exeDir 'exe.marker'
$game.Bat = $launchBat
$game.Hash = (Get-FileHash $launchBat).Hash
$game.Art = [Guid]::NewGuid().ToString('N') + '.png'
$png = [Convert]::FromBase64String('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAIAAACQd1PeAAAADElEQVR4nGNgmMkAAAE2AJrwJL1AAAAAAElFTkSuQmCC')
$updated = [Activator]::CreateInstance($games.GetType())
$updated.Add($game)
$otherGame = [Activator]::CreateInstance($game.GetType())
$otherGame.Name = 'Zero game'
$otherGame.Bat = $game.Bat
$otherGame.Hash = $game.Hash
$otherGame.LaunchArguments = '0'
$updated.Add($otherGame)
$manualGame = [Activator]::CreateInstance($game.GetType())
$manualGame.Name = 'Shared BAT menu'
$manualGame.Bat = $game.Bat
$manualGame.Hash = $game.Hash
$updated.Add($manualGame)
$store.GetMethod('Save', $static).Invoke($null, @($games, $updated, $game.Art, $png))
$saved = $load.Invoke($null, @($false))
$image = $store.GetMethod('Image', $static)
if ($saved.Count -ne 3 -or $saved[0].Name -ne $game.Name -or $saved[0].Bat -ne $game.Bat -or
    $saved[1].Bat -ne $game.Bat -or $saved[1].LaunchArguments -ne '0' -or
    $null -eq $image.Invoke($null, @($saved[0].Art)) -or (Test-Path $legacyRoot)) {
    throw 'The SQLite game library and screenshot did not round trip.'
}

$keyboardLauncher = [Activator]::CreateInstance($launcherType, $instance, $null, @($false, $true), $null)
$keyboardWindow = $launcherType.GetField('Win', $instance).GetValue($keyboardLauncher)
$running = $launcherType.GetField('running', $instance)
try {
    $keyboardWindow.Show()
    Wait-ForWindow { $null -ne $keyboardWindow.FindName('Cover').Source } 'the asynchronously loaded screenshot'
    $inputs = $launcherType.GetField('devices', $instance).GetValue($keyboardLauncher)
    foreach ($field in 'direct', 'xinput', 'midi') {
        if ($null -ne $inputs.GetType().GetField($field, $instance).GetValue($inputs)) {
            throw 'Keyboard-only mode initialized a hardware input backend.'
        }
    }
    $editorType = $assembly.GetType('LauncherBox.Editor', $true)
    $editor = $editorType.GetConstructor($instance, $null, @($launcherType), $null).Invoke(@($keyboardLauncher))
    $editorWindow = $editorType.GetField('win', $instance).GetValue($editor)
    $arguments = '1 "Band & Stage" "literal %TEMP%" "!LIVE!"'
    try {
        $editorWindow.Show()
        $editorWindow.UpdateLayout()
        $editorBounds = New-Object 'WindowBounds+Rect'
        $ownerBounds = New-Object 'WindowBounds+Rect'
        $editorHandle = [System.Windows.Interop.WindowInteropHelper]::new($editorWindow).Handle
        $ownerHandle = [System.Windows.Interop.WindowInteropHelper]::new($keyboardWindow).Handle
        if ($editorWindow.WindowState -ne 'Maximized' -or
            ![WindowBounds]::GetWindowRect($editorHandle, [ref]$editorBounds) -or
            ![WindowBounds]::GetWindowRect($ownerHandle, [ref]$ownerBounds) -or
            !$editorBounds.Equals($ownerBounds) -or $editorWindow.FindName('GameFields').ScrollableHeight -gt 0.5) {
            throw 'Fullscreen settings must cover the launcher and show all game fields without scrolling.'
        }
        $editorWindow.FindName('SettingsBatBox').Text = $settingBat
        $editorWindow.FindName('LaunchArgumentsBox').Text = $arguments
        $saveGame = $editorType.GetMethod('Save', $instance)
        $saveGame.Invoke($editor, $null)
        foreach ($entry in @(
            @{ Name = 'CMD game'; File = $launchCmd; Arguments = '"Band & Stage"' },
            @{ Name = 'EXE game'; File = $launchExe; Arguments = '"D:\Game data\" "A&B" "" "%TEMP%"' }
        )) {
            $editorType.GetMethod('Rebind', $instance).Invoke($editor, @($null))
            $editorWindow.FindName('TitleBox').Text = $entry.Name
            $editorWindow.FindName('LaunchFileBox').Text = $entry.File
            $editorWindow.FindName('LaunchArgumentsBox').Text = $entry.Arguments
            $saveGame.Invoke($editor, $null)
        }
        $entries = $editorWindow.FindName('Entries')
        $entries.SelectedIndex = 0
        $firstGame = $entries.SelectedItem
        $titleBox = $editorWindow.FindName('TitleBox')
        $draftName = $firstGame.Name + ' (draft)'
        $titleBox.Text = $draftName
        $moveGame = $editorType.GetMethod('MoveGame', $instance)
        $moveGame.Invoke($editor, @($firstGame, $entries.Items.Count))
        $reordered = $load.Invoke($null, @($false))
        if (!$entries.AllowDrop -or $reordered[0].Name -ne $otherGame.Name -or $reordered[4].Name -ne $firstGame.Name -or
            $reordered[4].Art -ne $firstGame.Art -or $entries.SelectedItem -ne $firstGame -or $titleBox.Text -ne $draftName -or
            $launcherType.GetField('Index', $instance).GetValue($keyboardLauncher) -ne 4) {
            throw 'Reordering must persist the new game order and preserve the current selection and draft.'
        }
        $moveGame.Invoke($editor, @($firstGame, 0))
        $titleBox.Text = $firstGame.Name
        $saveGame.Invoke($editor, $null)
    } finally {
        $editorWindow.Close()
    }
    $withSetting = $load.Invoke($null, @($false))
    if ($withSetting[0].SettingsBat -ne $settingBat -or $withSetting[0].LaunchArguments -ne $arguments -or
        $withSetting[0].SettingsHash -ne (Get-FileHash $settingBat).Hash -or $withSetting[0].Hash -ne $game.Hash -or
        $withSetting[3].Bat -ne $launchCmd -or $withSetting[3].Hash -ne (Get-FileHash $launchCmd).Hash -or
        $withSetting[4].Bat -ne $launchExe -or $withSetting[4].Hash -ne (Get-FileHash $launchExe).Hash) {
        throw 'The game editor did not save and hash each supported launch file.'
    }

    $start = $keyboardWindow.FindName('StartGame')
    $setting = $keyboardWindow.FindName('GameSetting')
    $click = [System.Windows.Controls.Primitives.ButtonBase]::ClickEvent
    $start.RaiseEvent([System.Windows.RoutedEventArgs]::new($click, $start))
    if ($null -eq $running.GetValue($keyboardLauncher) -or $keyboardWindow.WindowState -ne 'Maximized') {
        throw 'Launching a game should keep the fullscreen launcher window maximized.'
    }
    Wait-ForWindow { (Test-Path $startMarker) -and $null -eq $running.GetValue($keyboardLauncher) } 'the screenshot to run the Launch BAT'
    if ($keyboardWindow.WindowState -ne 'Maximized') { throw 'The launcher should return to fullscreen after the game.' }
    if ((Get-Content $startMarker -Raw).Trim() -ne '"Band & Stage" "literal %TEMP%" "!LIVE!"') {
        throw 'The BAT did not receive the launch arguments literally and in order.'
    }
    if (Test-Path $settingMarker) { throw 'Clicking the screenshot ran the Setting BAT.' }
    Remove-Item $startMarker

    $setting.RaiseEvent([System.Windows.RoutedEventArgs]::new($click, $setting))
    Wait-ForWindow { (Test-Path $settingMarker) -and $null -eq $running.GetValue($keyboardLauncher) } 'Setting to run its BAT'
    if ((Test-Path $startMarker) -or (Get-Content $settingMarker -Raw).Trim() -ne '""') {
        throw 'Clicking Setting should call only the Setting BAT without launch arguments.'
    }
    Remove-Item $settingMarker

    $actionType = $assembly.GetType('LauncherBox.LauncherAction', $true)
    $gameSettingAction = [Enum]::Parse($actionType, 'GameSetting')
    $keyboardSource = [Enum]::Parse($assembly.GetType('LauncherBox.InputSource', $true), 'Keyboard')
    $signalForKey = $assembly.GetType('LauncherBox.InputSignal', $true).GetMethod('KeyboardKey', $static)
    $settingSignal = $signalForKey.Invoke($null, @([System.Windows.Input.Key]::S))
    $currentMap = $bindingsField.GetValue($keyboardLauncher)
    $mappingType = $currentMap.GetType()
    if ($mappingType.GetField('Actions', $static).GetValue($null) -notcontains $gameSettingAction) {
        throw 'Game Setting is missing from the bindable actions.'
    }
    $mappingType.GetMethod('Set', $instance).Invoke($currentMap, @($gameSettingAction, $keyboardSource, $settingSignal))
    $savedMap = [Activator]::CreateInstance($mappingType, $true)
    if ($mappingType.GetMethod('Match', $instance).Invoke($savedMap, @($settingSignal)).Action -ne $gameSettingAction) {
        throw 'The Game Setting binding did not persist.'
    }
    $routeInput = $launcherType.GetMethod('RouteInput', $instance)
    $keyboardWindow.Activate() | Out-Null
    Wait-ForWindow { $keyboardWindow.IsActive } 'keyboard focus for Game Setting'
    if (!$routeInput.Invoke($keyboardLauncher, @($settingSignal))) { throw 'The Game Setting input was not handled.' }
    Wait-ForWindow { (Test-Path $settingMarker) -and $null -eq $running.GetValue($keyboardLauncher) } 'the Game Setting binding to run its BAT'
    if ((Test-Path $startMarker) -or (Get-Content $settingMarker -Raw).Trim() -ne '""') {
        throw 'The Game Setting binding should call only the Setting BAT without launch arguments.'
    }

    $next = $keyboardWindow.FindName('Next')
    $next.RaiseEvent([System.Windows.RoutedEventArgs]::new($click, $next))
    $fullscreen.Invoke($keyboardLauncher, $null)
    $windowedEditor = $editorType.GetConstructor($instance, $null, @($launcherType), $null).Invoke(@($keyboardLauncher))
    $windowedSettings = $editorType.GetField('win', $instance).GetValue($windowedEditor)
    try {
        if ($windowedSettings.Width -ne $keyboardWindow.ActualWidth -or
            $windowedSettings.Height -ne $keyboardWindow.ActualHeight -or $windowedSettings.WindowState -ne 'Normal') {
            throw 'Windowed settings must open at the size of the launcher.'
        }
        $windowedSettings.Show()
        $windowedSettings.UpdateLayout()
        $viewport = $windowedSettings.FindName('SettingsViewport')
        $scaleProbe = [System.Windows.Rect]::new(0, 0, 100, 100)
        $initial = $viewport.Child.TransformToAncestor($viewport).TransformBounds($scaleProbe)
        $windowedSettings.Width *= 0.75
        $windowedSettings.Height *= 0.75
        Wait-ForWindow {
            $windowedSettings.UpdateLayout()
            $resized = $viewport.Child.TransformToAncestor($viewport).TransformBounds($scaleProbe)
            $resized.Width -lt $initial.Width -and [Math]::Abs($resized.Width - $resized.Height) -lt 0.01
        } 'the settings layout to scale uniformly'
        if ($windowedSettings.FindName('GameFields').ScrollableHeight -gt 0.5) {
            throw 'Shrinking the settings window clipped the game fields.'
        }
    } finally {
        $windowedSettings.Close()
    }
    $keyboardWindow.Activate() | Out-Null
    Wait-ForWindow { $keyboardWindow.IsActive } 'keyboard focus after returning to the launcher'
    $keyboardSignal = $signalForKey.Invoke($null, @([System.Windows.Input.Key]::Enter))
    if (!$routeInput.Invoke($keyboardLauncher, @($keyboardSignal))) {
        throw 'The default START input was not handled.'
    }
    if ($null -eq $running.GetValue($keyboardLauncher) -or $keyboardWindow.WindowState -ne 'Normal') {
        throw 'Launching a game should keep the windowed launcher open.'
    }
    Wait-ForWindow { (Test-Path $zeroMarker) -and $null -eq $running.GetValue($keyboardLauncher) } 'START to run the shared Launch BAT with argument 0'
    if ($keyboardWindow.WindowState -ne 'Normal') { throw 'The launcher should return to windowed mode after the game.' }
    if (Test-Path $startMarker) { throw 'The second game ran with the first game arguments.' }

    $next.RaiseEvent([System.Windows.RoutedEventArgs]::new($click, $next))
    $start.RaiseEvent([System.Windows.RoutedEventArgs]::new($click, $start))
    Wait-ForWindow { (Test-Path $manualMarker) -and $null -eq $running.GetValue($keyboardLauncher) } 'the shared Launch BAT to run without arguments'

    $next.RaiseEvent([System.Windows.RoutedEventArgs]::new($click, $next))
    $start.RaiseEvent([System.Windows.RoutedEventArgs]::new($click, $start))
    Wait-ForWindow { (Test-Path $cmdMarker) -and $null -eq $running.GetValue($keyboardLauncher) } 'the CMD file to run'
    if ((Get-Content $cmdMarker -Raw).Trim() -ne '"Band & Stage"') {
        throw 'The CMD file did not receive its launch argument.'
    }

    $next.RaiseEvent([System.Windows.RoutedEventArgs]::new($click, $next))
    $start.RaiseEvent([System.Windows.RoutedEventArgs]::new($click, $start))
    Wait-ForWindow { (Test-Path $exeMarker) -and $null -eq $running.GetValue($keyboardLauncher) } 'the EXE file to run'
    $exeArguments = [IO.File]::ReadAllLines($exeMarker)
    if ($exeArguments.Count -ne 4 -or $exeArguments[0] -ne 'D:\Game data\' -or
        $exeArguments[1] -ne 'A&B' -or $exeArguments[2] -ne '' -or $exeArguments[3] -ne '%TEMP%') {
        throw 'The EXE file did not receive its launch arguments intact.'
    }
} finally {
    $keyboardWindow.Close()
}

$mapType = $assembly.GetType('LauncherBox.KeyMap', $true)
$map = [Activator]::CreateInstance($mapType, $true)
$play = [Enum]::Parse($assembly.GetType('LauncherBox.LauncherAction', $true), 'Play')
if ($mapType.GetMethod('Label', $static).Invoke($null, @($play)) -ne 'START') {
    throw 'The existing launch binding should appear as START.'
}
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

Move-Item $dbPath (Join-Path $portable 'legacy.db')
foreach ($schema in 1, 2) {
    $old = $sqlite.GetConstructor($instance, $null, @([string]), $null).Invoke(@("$dbPath"))
    try {
        $ddl = 'CREATE TABLE games (position INTEGER PRIMARY KEY, name TEXT, bat TEXT, hash TEXT, art TEXT'
        $row = "INSERT INTO games VALUES (0, 'Existing game', 'D:\Games\start.bat', 'LAUNCHHASH', NULL"
        $expectedSetting = $null
        if ($schema -eq 2) {
            $expectedSetting = 'D:\Games\setting.bat'
            $ddl += ', settings_bat TEXT, settings_hash TEXT'
            $row += ", '$expectedSetting', 'SETTINGHASH'"
        }
        foreach ($sql in @(
            "$ddl)",
            "$row)",
            'CREATE TABLE settings (id INTEGER PRIMARY KEY, bindings_saved INTEGER NOT NULL)',
            'INSERT INTO settings VALUES (1, 1)',
            'CREATE TABLE bindings (position INTEGER PRIMARY KEY, action INTEGER NOT NULL, source INTEGER NOT NULL, device TEXT, code TEXT, label TEXT)',
            "INSERT INTO bindings VALUES (0, 2, 3, 'test-port', '1:note:60', 'Existing START')",
            'CREATE TABLE screenshots (id TEXT PRIMARY KEY, png BLOB NOT NULL)',
            "PRAGMA user_version = $schema"
        )) {
            $execute.Invoke($old, @([string]$sql, [object[]]@()))
        }
    } finally {
        $old.Dispose()
    }
    $upgraded = $load.Invoke($null, @($false))
    $upgradedMap = [Activator]::CreateInstance($mapType, $true)
    $upgradedBinding = $mapType.GetMethod('Match', $instance).Invoke($upgradedMap, @($signal))
    if ($upgraded.Count -ne 1 -or $upgraded[0].Hash -ne 'LAUNCHHASH' -or
        $upgraded[0].SettingsBat -ne $expectedSetting -or $null -ne $upgraded[0].LaunchArguments -or
        $upgradedBinding.Label -ne 'Existing START') {
        throw "The SQLite upgrade from version $schema did not preserve existing settings."
    }
    Move-Item $dbPath (Join-Path $portable "upgraded-$schema.db")
}
Write-Host 'Verified fullscreen, native inputs, shared BAT arguments, independent Setting BAT, and database upgrades.'
