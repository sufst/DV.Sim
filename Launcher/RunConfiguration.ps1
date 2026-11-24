# Local run preferences and generated FSDS settings. No GUI dependencies.
function New-RunPreference {
    [pscustomobject]@{
        Version = 1
        Added = $false
        Placement = [pscustomobject]@{ X = 0.0; Y = 0.0; Z = 0.8; Roll = 0.0; Pitch = 0.0; Yaw = 0.0 }
    }
}

function Assert-RunPreference($Preference) {
    if ($Preference.Version -ne 1 -or $Preference.Added -isnot [bool]) {
        throw 'The saved run configuration has an unsupported format.'
    }
    foreach ($name in 'ImuEnabled', 'GpsEnabled') {
        $property = $Preference.PSObject.Properties[$name]
        if ($null -ne $property -and $property.Value -isnot [bool]) {
            throw "$name must be true or false."
        }
    }
    foreach ($axis in 'X', 'Y', 'Z', 'Roll', 'Pitch', 'Yaw') {
        $number = 0.0
        $value = $Preference.Placement.$axis
        if ($null -eq $value -or -not [double]::TryParse([string]$value,
            [Globalization.NumberStyles]::Float, [Globalization.CultureInfo]::InvariantCulture, [ref]$number) -or
            [double]::IsNaN($number) -or [double]::IsInfinity($number)) {
            throw "$axis must be a finite number."
        }
        $limit = if ($axis -in 'X', 'Y', 'Z') { 10 } else { 180 }
        if ([math]::Abs($number) -gt $limit) { throw "$axis must be between -$limit and $limit." }
    }
}

function Read-RunPreference([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return New-RunPreference }
    $preference = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    Assert-RunPreference $preference
    return $preference
}

function Write-RunJson([string]$Path, $Value) {
    $directory = Split-Path -Parent $Path
    [void][IO.Directory]::CreateDirectory($directory)
    $temporary = $Path + '.' + [guid]::NewGuid().ToString('N') + '.tmp'
    try {
        $json = $Value | ConvertTo-Json -Depth 40
        [IO.File]::WriteAllText($temporary, $json, (New-Object Text.UTF8Encoding($false)))
        if ([IO.File]::Exists($Path)) {
            [IO.File]::Replace($temporary, $Path, [NullString]::Value)
        } else {
            [IO.File]::Move($temporary, $Path)
        }
    } finally {
        if ([IO.File]::Exists($temporary)) { [IO.File]::Delete($temporary) }
    }
}

function New-SimulatorSettings([string]$BasePath, $Preference) {
    Assert-RunPreference $Preference
    $settings = Get-Content -LiteralPath $BasePath -Raw | ConvertFrom-Json
    $car = $settings.Vehicles.FSCar
    if ($null -eq $car) { throw 'The shared settings must contain Vehicles.FSCar.' }
    if ($null -eq $car.Sensors) { $car | Add-Member -NotePropertyName Sensors -NotePropertyValue ([pscustomobject]@{}) -Force }
    foreach ($name in 'Imu', 'Gps') {
        $override = $Preference.PSObject.Properties[$name + 'Enabled']
        if ($null -eq $override) { continue }
        $sensor = $car.Sensors.$name
        if ($null -eq $sensor) {
            $sensorType = if ($name -eq 'Imu') { 2 } else { 3 }
            $sensor = [pscustomobject]@{ SensorType = $sensorType }
            $car.Sensors | Add-Member -NotePropertyName $name -NotePropertyValue $sensor -Force
        }
        $sensor | Add-Member -NotePropertyName Enabled -NotePropertyValue $override.Value -Force
    }
    # Replace the managed lidar while preserving the other sensors.
    $lidars = @($car.Sensors.PSObject.Properties | Where-Object { $_.Value.SensorType -eq 6 })
    foreach ($lidar in $lidars) { $car.Sensors.PSObject.Properties.Remove($lidar.Name) }
    if ($Preference.Added) {
        $lidar = [ordered]@{
            SensorType = 6; Enabled = $true
            NumberOfLasers = 40; Range = 200; PointsPerScan = 72000; RotationsPerSecond = 10
            VerticalFOVUpper = 15; VerticalFOVLower = -25
            HorizontalFOVStart = -180; HorizontalFOVEnd = 180; DrawDebugPoints = $false
        }
        foreach ($axis in 'X', 'Y', 'Z', 'Roll', 'Pitch', 'Yaw') { $lidar[$axis] = [double]$Preference.Placement.$axis }
        # Keep the API name used by the existing controllers.
        $car.Sensors | Add-Member -NotePropertyName Lidar -NotePropertyValue ([pscustomobject]$lidar) -Force
    }
    return $settings
}
