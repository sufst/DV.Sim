$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationFramework, PresentationCore, WindowsBase, System.Xaml
$references = @('System.dll', 'System.Core.dll', [Windows.Point].Assembly.Location,
    [Windows.Media.Brush].Assembly.Location, [Windows.FrameworkElement].Assembly.Location,
    [System.Xaml.XamlReader].Assembly.Location)
Add-Type -Path @((Join-Path (Split-Path -Parent $PSScriptRoot) 'LidarView.cs'),
    (Join-Path $PSScriptRoot 'Test-LidarView.cs')) -ReferencedAssemblies $references
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
if ($pixels[$offset + 1] -lt 200 -or $pixels[$offset + 2] -lt 80) {
    throw 'The WPF renderer did not draw the projected lidar return.'
}
$plot.Range = 5
$plot.UpdateLayout()
$clipped = New-Object Windows.Media.Imaging.RenderTargetBitmap(600, 400, 96, 96, [Windows.Media.PixelFormats]::Pbgra32)
$clipped.Render($plot)
$clipped.CopyPixels($pixels, 600 * 4, 0)
if ($pixels[$offset + 1] -ge 200) { throw 'The WPF renderer did not clip the distant return.' }
'PASS: rendered lidar dots and view-range clipping.'
