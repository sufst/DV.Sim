$ErrorActionPreference = 'Stop'
. (Join-Path (Split-Path -Parent $PSScriptRoot) 'RunConfiguration.ps1')
$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$basePath = Join-Path $repoRoot 'settings.json'
$baseBefore = [IO.File]::ReadAllText($basePath)
$temporary = Join-Path $repoRoot ('FSOnline\Saved\RunSettings\test-' + [guid]::NewGuid().ToString('N'))
function Assert($Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
try {
    $preferencePath = Join-Path $temporary 'preferences.json'
    $preference = Read-RunPreference $preferencePath
    Assert (-not $preference.Added) 'First run should start with no lidar added.'
    $withoutLidar = New-SimulatorSettings $basePath $preference
    Assert ($null -eq $withoutLidar.Vehicles.FSCar.Sensors.Lidar) 'Absent lidar must not exist in runtime settings.'
    Assert $withoutLidar.Vehicles.FSCar.Sensors.Gps.Enabled 'GPS must be retained.'
    $preference.Added = $true
    $preference.Placement.X = 1.25
    $preference.Placement.Y = -0.2
    $preference.Placement.Yaw = 30
    Write-RunJson $preferencePath $preference
    $reopened = Read-RunPreference $preferencePath
    Assert ($reopened.Added -and $reopened.Placement.X -eq 1.25 -and $reopened.Placement.Yaw -eq 30) 'Selection and placement must survive reopening.'
    $settings = New-SimulatorSettings $basePath $reopened
    $lidar = $settings.Vehicles.FSCar.Sensors.Lidar
    Assert ($lidar.NumberOfLasers -eq 40 -and $lidar.PointsPerScan -eq 72000 -and $lidar.RotationsPerSecond -eq 10 -and $lidar.Range -eq 200) 'Pandar acquisition parameters are incorrect.'
    Assert ($lidar.VerticalFOVUpper -eq 15 -and $lidar.VerticalFOVLower -eq -25 -and ($lidar.HorizontalFOVEnd - $lidar.HorizontalFOVStart) -eq 360) 'Pandar field of view is incorrect.'
    Assert ($lidar.X -eq 1.25 -and $lidar.Y -eq -0.2 -and $lidar.Yaw -eq 30) 'Placement must reach runtime settings.'
    $reopened.Added = $false
    Write-RunJson $preferencePath $reopened
    $removed = Read-RunPreference $preferencePath
    Assert (-not $removed.Added -and $removed.Placement.X -eq 1.25) 'Removal must persist and retain placement.'
    $rejected = $false
    $removed.Placement.X = [double]::NaN
    try { Assert-RunPreference $removed } catch { $rejected = $true }
    Assert $rejected 'Non-finite placement must be rejected.'
    $removed.Placement.X = 11
    $rejected = $false
    try { Assert-RunPreference $removed } catch { $rejected = $true }
    Assert $rejected 'Out-of-bounds placement must be rejected.'
    [IO.File]::WriteAllText($preferencePath, '{invalid')
    $rejected = $false
    try { Read-RunPreference $preferencePath } catch { $rejected = $true }
    Assert $rejected 'Corrupt preferences must not be silently overwritten.'
    Assert ([IO.File]::ReadAllText($basePath) -eq $baseBefore) 'Shared settings must remain unchanged.'
    'PASS: defaults, Add/Remove, persistence, sensor parameters, placement, validation and shared configuration.'
} finally {
    # Only remove the unique test fixture within the ignored runtime directory.
    $expectedParent = [IO.Path]::GetFullPath((Join-Path $repoRoot 'FSOnline\Saved\RunSettings')) + '\'
    $resolvedTemporary = [IO.Path]::GetFullPath($temporary)
    if (-not $resolvedTemporary.StartsWith($expectedParent, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unexpected test cleanup target.' }
    if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Recurse -Force }
}
