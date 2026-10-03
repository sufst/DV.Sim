param([switch]$Fullscreen, [switch]$SettingsOnly)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationFramework, PresentationCore, WindowsBase
Add-Type @'
using System;
using System.Runtime.InteropServices;
namespace DVSim {
    public static class WindowPlacement {
        [StructLayout(LayoutKind.Sequential)] public struct Point { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] public struct MonitorInfo { public int Size; public Rect Monitor, Work; public uint Flags; }
        [DllImport("user32.dll")] static extern IntPtr MonitorFromPoint(Point point, uint flags);
        [DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int cx, int cy, uint flags);
        public static bool MoveTopLeft(IntPtr window) {
            var info = new MonitorInfo(); info.Size = Marshal.SizeOf(info);
            if (!GetMonitorInfo(MonitorFromPoint(new Point(), 1), ref info)) return false;
            return SetWindowPos(window, IntPtr.Zero, info.Work.Left, info.Work.Top, 0, 0, 0x0015);
        }
    }
}
'@
. (Join-Path $PSScriptRoot 'RunConfiguration.ps1')
$script:repoRoot = Split-Path -Parent $PSScriptRoot
$script:localDirectory = Join-Path $script:repoRoot 'FSOnline\Saved\RunSettings'
$script:preferencePath = Join-Path $script:localDirectory 'preferences.json'
$script:runtimePath = Join-Path $script:localDirectory 'settings.json'
$script:basePath = Join-Path $script:repoRoot 'settings.json'
$script:dirty = $false
$script:page = 'Home'
$script:mutex = $null
$script:placementTimer = New-Object Windows.Threading.DispatcherTimer
$script:placementTimer.Interval = [TimeSpan]::FromMilliseconds(250)
$script:placementTimer.Add_Tick({
    if ([DateTime]::UtcNow -gt $script:placementDeadline) { $script:placementTimer.Stop(); return }
    $simulator = Get-SimulatorProcesses | Where-Object { $_.ProcessName -eq 'Blocks' -and $_.MainWindowHandle -ne [IntPtr]::Zero } | Select-Object -First 1
    if ($simulator) {
        if (-not $script:windowSeenAt) { $script:windowSeenAt = [DateTime]::UtcNow }
        # UE can recenter its window during startup; finish positioning once that settles.
        if (([DateTime]::UtcNow - $script:windowSeenAt).TotalSeconds -ge 2) {
            [void][DVSim.WindowPlacement]::MoveTopLeft($simulator.MainWindowHandle)
            $script:placementTimer.Stop()
        }
    }
})

function Position-SimulatorOnStartup {
    if (-not $Fullscreen) {
        $script:windowSeenAt = $null
        $script:placementDeadline = [DateTime]::UtcNow.AddSeconds(45)
        $script:placementTimer.Start()
    }
}

function Save-Preference($Value) {
    Assert-RunPreference $Value
    Write-RunJson $script:preferencePath $Value
    $script:preference = $Value
    $script:dirty = $true
    Update-Panel
}

function Get-SimulatorProcesses {
    $paths = @((Join-Path $script:repoRoot 'FSDS.exe'), (Join-Path $script:repoRoot 'FSOnline\Binaries\Win64\Blocks.exe'))
    @(Get-Process FSDS, Blocks -ErrorAction SilentlyContinue | Where-Object { $_.Path -in $paths })
}

function Start-Simulator {
    $executable = Join-Path $script:repoRoot 'FSDS.exe'
    if (-not (Test-Path -LiteralPath $executable)) { throw 'FSDS.exe is missing from this checkout.' }
    Write-RunJson $script:runtimePath (New-SimulatorSettings $script:basePath $script:preference)
    $mode = if ($Fullscreen) { '-fullscreen' } else { '-windowed -ResX=1280 -ResY=720 -WinX=0 -WinY=0' }
    # Start-Process joins arguments on Windows, so quote the settings path explicitly.
    [void](Start-Process -FilePath $executable -WorkingDirectory $script:repoRoot -ArgumentList "$mode -settings `"$script:runtimePath`"")
    Position-SimulatorOnStartup
    $script:dirty = $false
}

function Show-Page([string]$Page) {
    $script:page = $Page
    foreach ($name in 'Home', 'Catalog', 'Sensor', 'Placement') {
        $script:ui[$name + 'Page'].Visibility = if ($Page -eq $name) { 'Visible' } else { 'Collapsed' }
    }
    $script:ui.BackButton.Visibility = if ($Page -eq 'Home') { 'Collapsed' } else { 'Visible' }
    $script:ui.Breadcrumb.Text = switch ($Page) {
        'Home' { 'RUN SETTINGS' }; 'Catalog' { 'RUN SETTINGS / LIDAR' }
        'Sensor' { 'LIDAR / PANDAR 40P' }; 'Placement' { 'PANDAR 40P / PLACEMENT' }
    }
    if ($Page -eq 'Placement') {
        foreach ($axis in 'X', 'Y', 'Z', 'Roll', 'Pitch', 'Yaw') {
            $script:ui[$axis + 'Input'].Text = ([double]$script:preference.Placement.$axis).ToString('0.###', [Globalization.CultureInfo]::InvariantCulture)
        }
        $script:ui.PlacementError.Text = ''
        Update-PlacementPreview
    }
}

function Update-Panel {
    $added = $script:preference.Added
    $script:ui.ToggleButton.Content = if ($added) { 'Remove' } else { 'Add' }
    $script:ui.ToggleButton.Background = if ($added) { '#EF646D' } else { '#64E6AE' }
    $script:ui.SensorStatus.Text = if ($added) { 'ADDED TO THIS RUN' } else { 'NOT ADDED' }
    $script:ui.SensorStatus.Foreground = if ($added) { '#64E6AE' } else { '#9BAAC0' }
    $script:ui.CatalogStatus.Text = if ($added) { 'Added' } else { 'Available' }
    $script:ui.HomeSummary.Text = if ($added) { '1 sensor added  /  Hesai Pandar 40P' } else { 'No lidar added to this run' }
    $p = $script:preference.Placement
    $script:ui.PlacementSummary.Text = 'X {0} m    Y {1} m    Z {2} m' -f $p.X, $p.Y, $p.Z
    $script:ui.SaveStatus.Text = if ($script:dirty) { 'Saved locally. Restart the simulator to apply.' } else { 'Your configuration is saved on this computer.' }
    $script:ui.RestartButton.Content = if ($script:dirty) { 'Apply & restart simulator' } else { 'Restart simulator' }
}

function Update-PlacementPreview {
    $values = @{}
    foreach ($axis in 'X', 'Y', 'Z') {
        $number = 0.0
        if (-not [double]::TryParse($script:ui[$axis + 'Input'].Text, [Globalization.NumberStyles]::Float,
            [Globalization.CultureInfo]::InvariantCulture, [ref]$number) -or [double]::IsNaN($number) -or [double]::IsInfinity($number)) { return }
        $values[$axis] = $number
    }
    [Windows.Controls.Canvas]::SetLeft($script:ui.LidarMarker, 158 + [math]::Max(-115, [math]::Min(115, $values.Y * -50)))
    [Windows.Controls.Canvas]::SetTop($script:ui.LidarMarker, 140 + [math]::Max(-100, [math]::Min(100, $values.X * -50)))
    $script:ui.PreviewHeight.Text = 'Height: {0:0.###} m' -f $values.Z
}

function Show-UiError($ErrorRecord) {
    [void][Windows.MessageBox]::Show($script:window, $ErrorRecord.Exception.Message, 'DV.Sim', 'OK', 'Error')
}

try {
    $hash = [Security.Cryptography.SHA256]::Create()
    try { $id = [BitConverter]::ToString($hash.ComputeHash([Text.Encoding]::UTF8.GetBytes($script:repoRoot.ToLowerInvariant()))).Replace('-', '') } finally { $hash.Dispose() }
    $created = $false
    $script:mutex = New-Object Threading.Mutex($true, "Local\DVSim.RunSettings.$id", [ref]$created)
    if (-not $created) { throw 'Run settings is already open for this checkout. Use its existing window.' }
    $script:preference = Read-RunPreference $script:preferencePath
    Write-RunJson $script:preferencePath $script:preference
    [xml]$markup = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'RunSettings.xaml') -Raw
    $reader = New-Object Xml.XmlNodeReader($markup)
    try { $script:window = [Windows.Markup.XamlReader]::Load($reader) } finally { $reader.Close() }
    $workArea = [Windows.SystemParameters]::WorkArea
    $script:window.Width = [math]::Min($script:window.Width, $workArea.Width)
    $script:window.Height = [math]::Min($script:window.Height, $workArea.Height)
    $script:window.MinWidth = [math]::Min($script:window.MinWidth, $workArea.Width)
    $script:window.MinHeight = [math]::Min($script:window.MinHeight, $workArea.Height)
    $script:window.Left = $workArea.Right - $script:window.Width
    $script:window.Top = $workArea.Top
    $script:ui = @{}
    foreach ($node in $markup.SelectNodes('//*[@Name]')) { $script:ui[$node.Name] = $script:window.FindName($node.Name) }
    $script:ui.LidarButton.Add_Click({ Show-Page 'Catalog' })
    $script:ui.PandarButton.Add_Click({ Show-Page 'Sensor' })
    $script:ui.PlacementButton.Add_Click({ Show-Page 'Placement' })
    $script:ui.BackButton.Add_Click({
        Show-Page $(switch ($script:page) { 'Placement' { 'Sensor' }; 'Sensor' { 'Catalog' }; default { 'Home' } })
    })
    $script:ui.ToggleButton.Add_Click({
        try {
            $next = $script:preference | ConvertTo-Json -Depth 10 | ConvertFrom-Json
            $next.Added = -not $next.Added
            Save-Preference $next
        } catch { Show-UiError $_ }
    })
    foreach ($axis in 'X', 'Y', 'Z') { $script:ui[$axis + 'Input'].Add_TextChanged({ Update-PlacementPreview }) }
    $script:ui.SavePlacementButton.Add_Click({
        try {
            $next = $script:preference | ConvertTo-Json -Depth 10 | ConvertFrom-Json
            foreach ($axis in 'X', 'Y', 'Z', 'Roll', 'Pitch', 'Yaw') {
                $number = 0.0
                if (-not [double]::TryParse($script:ui[$axis + 'Input'].Text, [Globalization.NumberStyles]::Float,
                    [Globalization.CultureInfo]::InvariantCulture, [ref]$number)) { throw "$axis must be a number (use a decimal point)." }
                $next.Placement.$axis = $number
            }
            Save-Preference $next
            Show-Page 'Sensor'
        } catch { $script:ui.PlacementError.Text = $_.Exception.Message }
    })
    $script:ui.RestartButton.Add_Click({
        try {
            $script:ui.RestartButton.IsEnabled = $false
            foreach ($process in (Get-SimulatorProcesses)) {
                [void]$process.CloseMainWindow()
                if (-not $process.WaitForExit(4000)) { $process.Kill(); $process.WaitForExit() }
            }
            Start-Simulator
            Update-Panel
        } catch { Show-UiError $_ } finally { $script:ui.RestartButton.IsEnabled = $true }
    })
    Show-Page 'Home'
    if (-not $SettingsOnly) {
        if ((Get-SimulatorProcesses).Count -gt 0) {
            $script:dirty = $true
            Position-SimulatorOnStartup
        } else { Start-Simulator }
    }
    Update-Panel
    [void]$script:window.ShowDialog()
} catch {
    [void][Windows.MessageBox]::Show($_.Exception.Message, 'DV.Sim could not open run settings', 'OK', 'Error')
    exit 1
} finally {
    $script:placementTimer.Stop()
    if ($script:mutex) { if ($created) { $script:mutex.ReleaseMutex() }; $script:mutex.Dispose() }
}
