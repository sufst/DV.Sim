param([switch]$Fullscreen, [switch]$SettingsOnly)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationFramework, PresentationCore, WindowsBase, System.Xaml
Add-Type -Path @((Join-Path $PSScriptRoot 'LidarView.cs'), (Join-Path $PSScriptRoot 'CentrelineFollower.cs')) -ReferencedAssemblies @(
    'System.dll', 'System.Core.dll', [Windows.Point].Assembly.Location,
    [Windows.Media.Brush].Assembly.Location, [Windows.FrameworkElement].Assembly.Location,
    [System.Xaml.XamlReader].Assembly.Location
)
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
$script:lidarFeed = $null
$script:lidarTimer = New-Object Windows.Threading.DispatcherTimer
$script:lidarTimer.Interval = [TimeSpan]::FromMilliseconds(200)
$script:lidarTimer.Add_Tick({ Update-LidarView })
$script:slowAutoDriver = $null
$script:autoRunTimer = New-Object Windows.Threading.DispatcherTimer
$script:autoRunTimer.Interval = [TimeSpan]::FromMilliseconds(250)
$script:autoRunTimer.Add_Tick({ Update-SlowAutoRun })
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
    # This FSDS build crashes on map startup with the -settings file override.
    # Its supported LaunchDir fallback reads settings.json from the working directory.
    [void](Start-Process -FilePath $executable -WorkingDirectory $script:localDirectory -ArgumentList $mode)
    Position-SimulatorOnStartup
    $script:dirty = $false
}

function Get-ApiPort {
    $settings = Get-Content -LiteralPath $script:basePath -Raw | ConvertFrom-Json
    if ($settings.ApiServerPort) { return [int]$settings.ApiServerPort }
    return 41451
}

function Stop-LidarView {
    $script:lidarTimer.Stop()
    if ($script:lidarFeed) { $script:lidarFeed.Dispose(); $script:lidarFeed = $null }
}

function Start-LidarView {
    Stop-LidarView
    $script:lidarPlot.SetPoints($null)
    $script:ui.LidarOverlay.Visibility = 'Visible'
    $script:ui.LidarOverlayTitle.Text = 'Start the run'
    $script:ui.LidarOverlayBody.Text = 'Choose a map and click Run Simulation in FSDS.'
    $port = Get-ApiPort
    $script:lidarFeed = New-Object DVSim.LidarFeed($port)
    $script:lidarTimer.Start()
}

function Start-SlowAutoRun {
    Stop-SlowAutoRun
    $port = Get-ApiPort
    $script:slowAutoDriver = New-Object DVSim.SlowAutoDriver -ArgumentList @($port, [double]$script:ui.SlowAutoSpeedSlider.Value)
    $script:autoRunTimer.Start()
    Update-SlowAutoRun
}

function Stop-SlowAutoRun {
    if ($script:slowAutoDriver) {
        $script:slowAutoDriver.Dispose()
        $script:slowAutoDriver = $null
    }
    if (-not $script:slowAutoDriver) { $script:autoRunTimer.Stop() }
    Update-SlowAutoRun
}

function Update-SlowAutoRun {
    if (-not $script:ui -or -not $script:ui.SlowAutoButton) { return }
    $target = [double]$script:ui.SlowAutoSpeedSlider.Value
    $script:ui.SlowAutoSpeedLabel.Text = '{0:0.0} m/s' -f $target
    if ($script:slowAutoDriver) {
        $script:slowAutoDriver.TargetSpeed = $target
        $status = $script:slowAutoDriver.Status
        $script:ui.SlowAutoButton.Content = 'Stop slow run'
        $script:ui.SlowAutoButton.Background = '#EF646D'
        $script:ui.SlowAutoButton.Foreground = '#101827'
        $script:ui.SlowAutoStatus.Text = $status.ToUpperInvariant()
        $script:ui.SlowAutoStatus.Foreground = switch ($status) {
            'Running' { '#64E6AE' }
            'Connecting' { '#FFD166' }
            'Waiting' { '#FFD166' }
            default { '#9BAAC0' }
        }
        $script:ui.SlowAutoDetail.Text = $script:slowAutoDriver.Detail
    } else {
        $script:ui.SlowAutoButton.Content = 'Start slow run'
        $script:ui.SlowAutoButton.Background = '#64E6AE'
        $script:ui.SlowAutoButton.Foreground = '#101B18'
        $script:ui.SlowAutoStatus.Text = 'OFF'
        $script:ui.SlowAutoStatus.Foreground = '#9BAAC0'
        $script:ui.SlowAutoDetail.Text = 'Start after choosing a map.'
    }
}

function Update-LidarView {
    if (-not $script:lidarFeed) { return }
    $scan = $script:lidarFeed.Latest
    $live = $scan.Status -eq 'Live'
    $script:ui.LidarOverlay.Visibility = if ($live) { 'Collapsed' } else { 'Visible' }
    $script:ui.LiveState.Text = if ($live) { 'LIVE' } else { 'WAITING' }
    $script:ui.LiveState.Foreground = if ($live) { '#64E6AE' } else { '#9BAAC0' }
    if ($live) {
        $script:lidarPlot.SetPoints($scan.Points)
        $age = [math]::Max(0, [int]([DateTime]::UtcNow - $scan.ReceivedAt).TotalMilliseconds)
        $script:ui.ScanSummary.Text = '{0:N0} returns / {1} ms ago' -f ($scan.Points.Length / 3), $age
    } else {
        $script:lidarPlot.SetPoints($null)
        $script:ui.ScanSummary.Text = 'Waiting for a scan'
        switch ($scan.Status) {
            'NoLidar' {
                $script:ui.LidarOverlayTitle.Text = 'Add a lidar first'
                $script:ui.LidarOverlayBody.Text = 'Add the Pandar 40P in Run settings, apply and restart, then start the run.'
            }
            'Paused' {
                $script:ui.LidarOverlayTitle.Text = 'Resume the run'
                $script:ui.LidarOverlayBody.Text = 'The lidar scan has stopped updating. Resume or start a run in FSDS.'
            }
            'Waiting' {
                $script:ui.LidarOverlayTitle.Text = 'Waiting for lidar'
                $script:ui.LidarOverlayBody.Text = 'Start the run in FSDS to receive the first scan.'
            }
            default {
                $script:ui.LidarOverlayTitle.Text = 'Start the run'
                $script:ui.LidarOverlayBody.Text = 'Choose a map and click Run Simulation in FSDS.'
            }
        }
    }
}

function Show-Page([string]$Page) {
    if ($script:page -eq 'Visuals' -and $Page -ne 'Visuals') { Stop-LidarView }
    $script:page = $Page
    foreach ($name in 'Home', 'Catalog', 'Sensor', 'Placement', 'Visuals', 'AutomaticMovement') {
        $script:ui[$name + 'Page'].Visibility = if ($Page -eq $name) { 'Visible' } else { 'Collapsed' }
    }
    $script:ui.BackButton.Visibility = if ($Page -eq 'Home') { 'Collapsed' } else { 'Visible' }
    $script:ui.Breadcrumb.Text = switch ($Page) {
        'Home' { 'RUN SETTINGS' }; 'Catalog' { 'RUN SETTINGS / LIDAR' }
        'Sensor' { 'LIDAR / PANDAR 40P' }; 'Placement' { 'PANDAR 40P / PLACEMENT' }
        'Visuals' { 'IN-RUN VISUALS / LIDAR MAP' }
        'AutomaticMovement' { 'AUTOMATIC MOVEMENT' }
    }
    $script:ui.RunSettingsNav.Background = if ($Page -in 'Visuals', 'AutomaticMovement') { '#101827' } else { '#23304B' }
    $script:ui.LiveLidarButton.Background = if ($Page -eq 'Visuals') { '#23304B' } else { '#101827' }
    $script:ui.AutomaticMovementButton.Background = if ($Page -eq 'AutomaticMovement') { '#23304B' } else { '#101827' }
    if ($Page -eq 'Visuals') { Start-LidarView }
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
    $script:lidarPlot = New-Object DVSim.LidarPlot
    $script:lidarPlot.Angled = $true
    [void]$script:ui.LidarPlotHost.Children.Add($script:lidarPlot)
    $script:lidarPlot.Add_ViewChanged({ $script:ui.ZoomLabel.Text = '{0:0.0}x' -f $script:lidarPlot.Zoom })
    $script:ui.ResetViewButton.Add_Click({ $script:lidarPlot.ResetView() })
    $script:ui.RunSettingsNav.Add_Click({ Show-Page 'Home' })
    $script:ui.LiveLidarButton.Add_Click({ Show-Page 'Visuals' })
    $script:ui.AutomaticMovementButton.Add_Click({ Show-Page 'AutomaticMovement' })
    $script:ui.PovButton.Add_Click({
        $script:lidarPlot.ResetView()
        $script:ui.RaisedViewControls.Visibility = 'Collapsed'
        $script:lidarPlot.Angled = $false
        $script:lidarPlot.TopDown = $false
        $script:ui.PovButton.Background = '#34415D'; $script:ui.TopDownButton.Background = '#202C40'
        $script:ui.AngledButton.Background = '#202C40'
    })
    $script:ui.TopDownButton.Add_Click({
        $script:lidarPlot.ResetView()
        $script:ui.RaisedViewControls.Visibility = 'Collapsed'
        $script:lidarPlot.Angled = $false
        $script:lidarPlot.TopDown = $true
        $script:ui.PovButton.Background = '#202C40'; $script:ui.TopDownButton.Background = '#34415D'
        $script:ui.AngledButton.Background = '#202C40'
    })
    $script:ui.AngledButton.Add_Click({
        $script:lidarPlot.ResetView()
        $script:ui.RaisedViewControls.Visibility = 'Visible'
        $script:lidarPlot.Angled = $true
        $script:ui.PovButton.Background = '#202C40'; $script:ui.TopDownButton.Background = '#202C40'
        $script:ui.AngledButton.Background = '#34415D'
    })
    $script:ui.CameraHeightSlider.Add_ValueChanged({
        $script:lidarPlot.CameraHeight = $script:ui.CameraHeightSlider.Value
        $script:ui.CameraHeightLabel.Text = '{0:0.0} m' -f $script:lidarPlot.CameraHeight
    })
    $script:ui.CameraTiltSlider.Add_ValueChanged({
        $script:lidarPlot.CameraTilt = $script:ui.CameraTiltSlider.Value
        $script:ui.CameraTiltLabel.Text = ('{0:0}' -f $script:lidarPlot.CameraTilt) + [char]0x00B0
    })
    $script:ui.ViewRangeSlider.Add_ValueChanged({
        $script:lidarPlot.Range = $script:ui.ViewRangeSlider.Value
        $script:ui.ViewRangeLabel.Text = '{0:0} m' -f $script:lidarPlot.Range
    })
    $script:ui.SlowAutoSpeedSlider.Add_ValueChanged({ Update-SlowAutoRun })
    $script:ui.SlowAutoButton.Add_Click({
        try {
            if ($script:slowAutoDriver) { Stop-SlowAutoRun } else { Start-SlowAutoRun }
        } catch { Show-UiError $_ }
    })
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
            Stop-SlowAutoRun
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
    Stop-LidarView
    Stop-SlowAutoRun
    $script:autoRunTimer.Stop()
    $script:placementTimer.Stop()
    if ($script:mutex) { if ($created) { $script:mutex.ReleaseMutex() }; $script:mutex.Dispose() }
}
