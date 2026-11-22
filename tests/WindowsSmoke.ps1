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
try {
    $window.Show()
    if ($window.WindowStyle -ne 'None' -or $window.WindowState -ne 'Maximized' -or
        $window.Background.Color.ToString() -ne '#FF000000' -or $null -ne $window.FindName('Cover').Source) {
        throw 'LauncherBox must open fullscreen with a black background and no image.'
    }
    $fullscreen.Invoke($launcher, $null)
    if ($window.WindowState -ne 'Normal') { throw 'Fullscreen did not toggle off.' }
    $deviceInput = $launcherType.GetField('devices', $instance).GetValue($launcher)
    $direct = $deviceInput.GetType().GetField('direct', $instance).GetValue($deviceInput)
    $direct.GetType().GetMethod('Refresh', $instance).Invoke($direct, $null)
    if ($null -eq $direct.GetType().GetField('input', $instance).GetValue($direct)) {
        throw 'DirectInput did not initialize.'
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
Write-Host 'Verified fullscreen startup, an empty library, Windows DirectInput initialization, and portable settings.'
