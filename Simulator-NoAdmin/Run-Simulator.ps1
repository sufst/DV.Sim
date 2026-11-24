param([switch]$Fullscreen)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$launcher = Join-Path $root 'Launcher\RunSettings.ps1'
if (-not (Test-Path -LiteralPath $launcher -PathType Leaf)) {
    throw 'Keep Simulator-NoAdmin in the DV.Sim checkout.'
}

$runtime = Join-Path $PSScriptRoot 'DirectX'
$libraries = 'D3DCompiler_43.dll', 'XINPUT1_3.dll', 'XAPOFX1_5.dll', 'XAudio2_7.dll', 'X3DAudio1_7.dll'
$missing = $libraries | Where-Object { -not (Test-Path -LiteralPath (Join-Path $runtime $_) -PathType Leaf) }
if ($missing) {
    $sevenZip = Join-Path $env:ProgramFiles '7-Zip\7z.exe'
    if (-not (Test-Path -LiteralPath $sevenZip -PathType Leaf)) {
        throw 'First-time setup needs 7-Zip to extract the Microsoft package.'
    }

    $cabinets = 'Jun2010_D3DCompiler_43_x64.cab', 'APR2007_xinput_x64.cab',
        'Jun2010_XAudio_x64.cab', 'Feb2010_X3DAudio_x64.cab'
    $stage = Join-Path $PSScriptRoot ('.setup-' + [guid]::NewGuid().ToString('N'))
    [void][IO.Directory]::CreateDirectory($stage)
    try {
        $package = Join-Path $stage 'directx_Jun2010_redist.exe'
        $url = 'https://download.microsoft.com/download/8/4/a/84a35bf1-dafe-4ae8-82af-ad2ae20b6b14/directx_Jun2010_redist.exe'
        Write-Host 'Downloading the Microsoft DirectX redistributable (96 MB)...'
        $ProgressPreference = 'SilentlyContinue'
        Invoke-WebRequest -UseBasicParsing -Uri $url -OutFile $package
        $expectedHash = '053F76DCBB28802E23341B6A787E3B0791C0FA5C8D4D011B1044172DBF89C73B'
        if ((Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash -ne $expectedHash) {
            throw 'The Microsoft DirectX download did not match the verified package.'
        }

        & $sevenZip e $package "-o$stage" $cabinets -y -bso0
        if ($LASTEXITCODE -ne 0) { throw 'Could not extract the DirectX cabinets.' }
        foreach ($cabinet in $cabinets) {
            & "$env:SystemRoot\System32\expand.exe" '-F:*.dll' (Join-Path $stage $cabinet) $stage | Out-Null
            if ($LASTEXITCODE -ne 0) { throw "Could not extract $cabinet." }
        }

        $files = foreach ($library in $libraries) {
            Get-Item -LiteralPath (Join-Path $stage $library)
        }
        [void][IO.Directory]::CreateDirectory($runtime)
        Copy-Item -LiteralPath $files.FullName -Destination $runtime -Force
    } finally {
        Get-ChildItem -LiteralPath $stage -File | Remove-Item -Force
        Remove-Item -LiteralPath $stage
    }
}

# The run-settings process and the simulator it starts inherit this search path.
$env:Path = "$runtime;$env:Path"
$arguments = "-NoProfile -STA -ExecutionPolicy Bypass -File `"$launcher`""
if ($Fullscreen) { $arguments += ' -Fullscreen' }
Start-Process -FilePath "$PSHOME\powershell.exe" -ArgumentList $arguments -WindowStyle Hidden
