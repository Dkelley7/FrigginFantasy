$systemDotnet = Get-Command dotnet.exe -ErrorAction SilentlyContinue
$sdkPath = $null
if ($systemDotnet) {
    $installedSdks = & $systemDotnet.Source --list-sdks
    if ($installedSdks -match '^10\.') { $sdkPath = $systemDotnet.Source }
}
if (-not $sdkPath) {
    $sdkPath = Join-Path $projectRoot '.tools/dotnet/dotnet.exe'
    if (-not (Test-Path -LiteralPath $sdkPath)) {
        New-Item -ItemType Directory -Path (Join-Path $projectRoot '.tools') -Force | Out-Null
        Write-Host 'Downloading the .NET 10 SDK from Microsoft into this project...'
        Invoke-WebRequest -Uri 'https://dot.net/v1/dotnet-install.ps1' -OutFile (Join-Path $projectRoot '.tools/dotnet-install.ps1')
        & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $projectRoot '.tools/dotnet-install.ps1') -Channel 10.0 -InstallDir (Join-Path $projectRoot '.tools/dotnet') -NoPath
        if ($LASTEXITCODE -ne 0) { throw 'SDK installation failed.' }
    }
}
$env:DOTNET_ROOT = Split-Path -Parent $sdkPath
