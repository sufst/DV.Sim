param([switch]$Fullscreen, [switch]$NoLaunch, [int]$TimeoutSeconds = 180, [string]$PythonExecutable)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot

try {
    $pythonCandidates = @()
    if ($PythonExecutable) { $pythonCandidates = @($PythonExecutable) }
    else {
        $pythonCandidates += Join-Path $repoRoot '.venv\Scripts\python.exe'
        $pythonCandidates += @(Get-Command python, python3 -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source)
        foreach ($registry in @('HKCU:\Software\Python\PythonCore', 'HKLM:\Software\Python\PythonCore', 'HKLM:\Software\WOW6432Node\Python\PythonCore')) {
            foreach ($version in @(Get-ChildItem $registry -ErrorAction SilentlyContinue)) {
                $install = Get-ItemProperty (Join-Path $version.PSPath 'InstallPath') -ErrorAction SilentlyContinue
                if ($install.ExecutablePath) { $pythonCandidates += $install.ExecutablePath }
                elseif ($install.'(default)') { $pythonCandidates += Join-Path $install.'(default)' 'python.exe' }
            }
        }
    }
    $pythonExe = $null
    foreach ($candidate in ($pythonCandidates | Select-Object -Unique)) {
        if (-not (Test-Path -LiteralPath $candidate) -or $candidate -like '*\Microsoft\WindowsApps\*') { continue }
        & $candidate -c 'import sys; sys.exit(0 if sys.version_info >= (3, 9) else 1)' 2>$null
        if ($LASTEXITCODE -eq 0) { $pythonExe = $candidate; break }
    }
    if (-not $pythonExe) { throw 'Python 3.9+ was not found. Install Python or supply -PythonExecutable with its full path.' }
    & $pythonExe -c 'import msgpack' 2>$null
    if ($LASTEXITCODE -ne 0) { throw "Install the controller dependency with: & '$pythonExe' -m pip install msgpack" }
    Add-Type -AssemblyName PresentationFramework, PresentationCore, WindowsBase, System.Xaml
    Add-Type -Path @((Join-Path $repoRoot 'Launcher\LidarView.cs'), (Join-Path $repoRoot 'Launcher\CentrelineFollower.cs')) -ReferencedAssemblies @(
        'System.dll', 'System.Core.dll', [Windows.Point].Assembly.Location,
        [Windows.Media.Brush].Assembly.Location, [Windows.FrameworkElement].Assembly.Location,
        [System.Xaml.XamlReader].Assembly.Location
    )
    if (-not $NoLaunch) {
        # Match the run-settings mutex so an existing settings window is reused.
        $hash = [Security.Cryptography.SHA256]::Create()
        try { $id = [BitConverter]::ToString($hash.ComputeHash([Text.Encoding]::UTF8.GetBytes($repoRoot.ToLowerInvariant()))).Replace('-', '') } finally { $hash.Dispose() }
        $existing = $null
        if ([Threading.Mutex]::TryOpenExisting("Local\DVSim.RunSettings.$id", [ref]$existing)) {
            $existing.Dispose()
            Write-Host 'Using the existing Run settings window. If FSDS is closed, click Restart simulator.'
        } else {
            $arguments = '-NoProfile -STA -ExecutionPolicy Bypass -File "{0}"' -f (Join-Path $repoRoot 'Launcher\RunSettings.ps1')
            if ($Fullscreen) { $arguments += ' -Fullscreen' }
            Start-Process powershell.exe -WindowStyle Hidden -ArgumentList $arguments -WorkingDirectory $repoRoot
        }
    }
    $settings = Get-Content -LiteralPath (Join-Path $repoRoot 'settings.json') -Raw | ConvertFrom-Json
    $port = if ($settings.ApiServerPort) { [int]$settings.ApiServerPort } else { 41451 }
    Write-Host 'Choose a map and click Run Simulation. Waiting for cone positions...'
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    $lastError = 'No running simulation.'
    $follower = $null
    while ([DateTime]::UtcNow -lt $deadline) {
        $rpc = $null
        try {
            $rpc = New-Object DVSim.LidarRpc($port)
            $track = $rpc.Call('getRefereeState')
            $candidate = New-Object DVSim.CentrelineFollower($track)
            if ($candidate.WaypointCount -ge 2) { $follower = $candidate; break }
            $lastError = 'This map has no usable blue/yellow cone centreline.'
        } catch { $lastError = $_.Exception.Message }
        finally { if ($rpc) { $rpc.Dispose() } }
        Start-Sleep -Milliseconds 500
    }
    if (-not $follower) { throw "No centreline received within $TimeoutSeconds seconds. $lastError" }
    $index = 0
    $rows = @($follower.GetWaypoints() | ForEach-Object {
        [pscustomobject]@{ index = $index++; x_m = $_.X; y_m = $_.Y; track_width_m = $_.Width }
    })
    $outputDirectory = Join-Path $repoRoot 'FSOnline\Saved\Control_Tests'
    [void](New-Item -ItemType Directory -Path $outputDirectory -Force)
    $stem = 'centreline-' + [DateTime]::Now.ToString('yyyyMMdd-HHmmss-fff')
    $csvPath = Join-Path $outputDirectory "$stem.csv"
    $jsonPath = Join-Path $outputDirectory "$stem.json"
    $rows | Export-Csv -LiteralPath $csvPath -NoTypeInformation -Encoding UTF8
    [pscustomobject]@{
        coordinate_frame = 'Metres relative to spawn; fixed map X/Y axes, Unreal Y inverted'
        closed = $follower.IsClosed
        waypoints = $rows
    } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $jsonPath -Encoding UTF8
    Write-Host ("`n{0} ordered centreline positions. Closed loop: {1}" -f $rows.Count, $follower.IsClosed)
    Write-Host "CSV: $csvPath"
    Write-Host "JSON: $jsonPath"
    Write-Host "`nPassing centreline to Control_Tests\control.py using $pythonExe"
    & $pythonExe (Join-Path $PSScriptRoot 'control.py') --centreline $jsonPath --port $port
    if ($LASTEXITCODE -ne 0) { throw "control.py exited with code $LASTEXITCODE" }
} catch {
    Write-Host "Centreline test failed: $($_.Exception.Message)" -ForegroundColor Red
    exit 1
}
