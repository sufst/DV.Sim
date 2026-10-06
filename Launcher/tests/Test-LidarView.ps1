$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationFramework, PresentationCore, WindowsBase, System.Xaml
$references = @('System.dll', 'System.Core.dll', [Windows.Point].Assembly.Location,
    [Windows.Media.Brush].Assembly.Location, [Windows.FrameworkElement].Assembly.Location,
    [System.Xaml.XamlReader].Assembly.Location)
Add-Type -Path @((Join-Path (Split-Path -Parent $PSScriptRoot) 'LidarView.cs'),
    (Join-Path (Split-Path -Parent $PSScriptRoot) 'CentrelineFollower.cs'),
    (Join-Path $PSScriptRoot 'Test-CentrelineFollower.cs'),
    (Join-Path $PSScriptRoot 'Test-LidarView.cs')) -ReferencedAssemblies $references
[CentrelineFollowerChecks]::Run()
[LidarViewChecks]::Run()
$plot = New-Object DVSim.LidarPlot
$plot.SetPoints([double[]]@(10, 2, 1))
$plot.Measure((New-Object Windows.Size(600, 400)))
$plot.Arrange((New-Object Windows.Rect(0, 0, 600, 400)))
$plot.UpdateLayout()
$bitmap = New-Object Windows.Media.Imaging.RenderTargetBitmap(600, 400, 96, 96, [Windows.Media.PixelFormats]::Pbgra32)
$bitmap.Render($plot)
$pixels = New-Object byte[] (600 * 400 * 4)
$bitmap.CopyPixels($pixels, 600 * 4, 0)
$offset = (170 * 600 + 240) * 4
if ($pixels[$offset + 1] -lt 200 -or $pixels[$offset + 2] -lt 40) {
    throw 'The WPF renderer did not draw the projected lidar return.'
}
$plot.Range = 5
$plot.UpdateLayout()
$clipped = New-Object Windows.Media.Imaging.RenderTargetBitmap(600, 400, 96, 96, [Windows.Media.PixelFormats]::Pbgra32)
$clipped.Render($plot)
$clipped.CopyPixels($pixels, 600 * 4, 0)
if ($pixels[$offset + 1] -ge 200) { throw 'The WPF renderer did not clip the distant return.' }
'PASS: rendered lidar dots and view-range clipping.'

# Exceed the former 18,000-point limit; the nearby cluster starts at an index
# the old global stride skipped. Other returns lie behind the sensor.
$plot.Range = 200
$denseScan = New-Object double[] (18003 * 3)
for ($index = 0; $index -lt 18003; $index++) { $denseScan[$index * 3] = -10 }
for ($index = 1; $index -le 3; $index++) {
    $denseScan[$index * 3] = 10
    $denseScan[$index * 3 + 1] = 2 + ($index - 1) * 0.04
    $denseScan[$index * 3 + 2] = 1
}
$plot.SetPoints($denseScan)
$plot.ZoomAt(16, (New-Object Windows.Point(240,170)))
$plot.UpdateLayout()
$detailed = New-Object Windows.Media.Imaging.RenderTargetBitmap(600,400,96,96,[Windows.Media.PixelFormats]::Pbgra32)
$detailed.Render($plot)
$detailed.CopyPixels($pixels,600*4,0)
if ($plot.RenderedPointCount -ne 3) { throw 'Zoomed scan must include every visible return without global point skipping.' }
foreach ($x in @(240,221,202)) {
    $offset = (170*600 + $x)*4
    if ($pixels[$offset + 1] -lt 200) { throw 'Zoom must separate and draw all three close cluster returns.' }
}
$plot.PanBy(60,-20)
$plot.UpdateLayout()
$panned = New-Object Windows.Media.Imaging.RenderTargetBitmap(600,400,96,96,[Windows.Media.PixelFormats]::Pbgra32)
$panned.Render($plot)
$panned.CopyPixels($pixels,600*4,0)
$offset = (150*600 + 300)*4
if ($pixels[$offset + 1] -lt 200) { throw 'Dragging must translate the zoomed scan.' }
$plot.ResetView()
if ($plot.Zoom -ne 1 -or $plot.PanX -ne 0 -or $plot.PanY -ne 0 -or $plot.Range -ne 200) {
    throw 'Reset must restore navigation while retaining sensor view range.'
}
$plot.ZoomAt(1000, (New-Object Windows.Point(300,200)))
if ($plot.Zoom -ne 32) { throw 'Zoom-in limit failed.' }
$plot.ZoomAt(0.000001, (New-Object Windows.Point(300,200)))
if ($plot.Zoom -ne 0.25) { throw 'Zoom-out limit failed.' }
$plot.ResetView()
'PASS: full scan visibility, cursor-anchored zoom, separated cluster dots, panning, reset and zoom bounds.'

$wheel = New-Object Windows.Input.MouseWheelEventArgs([Windows.Input.Mouse]::PrimaryDevice,0,120)
$wheel.RoutedEvent = [Windows.Input.Mouse]::MouseWheelEvent
$plot.RaiseEvent($wheel)
if (-not $wheel.Handled -or [math]::Abs($plot.Zoom-1.2) -gt 0.00001) { throw 'Plot must handle the wheel rather than scrolling the settings page.' }
$plot.ResetView()
$plot.TopDown = $true
$fullScan = New-Object double[] (72000 * 3)
for ($index=0; $index -lt 72000; $index++) {
    $fullScan[$index*3] = 10 + ($index % 300)*0.1
    $fullScan[$index*3+1] = -12 + [math]::Floor($index/300)*0.1
}
$plot.SetPoints($fullScan)
$plot.UpdateLayout()
$fullFrame = New-Object Windows.Media.Imaging.RenderTargetBitmap(600,400,96,96,[Windows.Media.PixelFormats]::Pbgra32)
$renderTime = [Diagnostics.Stopwatch]::StartNew()
$fullFrame.Render($plot)
$renderTime.Stop()
if ($plot.RenderedPointCount -ne 72000) { throw 'Full Pandar scan must be rendered without point skipping.' }
'PASS: wheel routing and all 72,000 points rendered ({0} ms).' -f $renderTime.ElapsedMilliseconds
