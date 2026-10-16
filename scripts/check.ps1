param([int]$Seed = 20261016, [int]$Iterations = 6, [ValidateRange(1,1000)][int]$Repeat = 1, [switch]$SkipBrowser)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
Set-Location -LiteralPath $projectRoot
$env:DOTNET_CLI_HOME = Join-Path $projectRoot '.tools/cli'
$env:NUGET_PACKAGES = Join-Path $projectRoot '.tools/packages'
. (Join-Path $PSScriptRoot 'resolve-sdk.ps1')
$testLock = $null
try {
$lockPath = Join-Path $projectRoot '.tools/verification.lock'
New-Item -ItemType Directory -Path (Split-Path -Parent $lockPath) -Force | Out-Null
try { $testLock = [System.IO.File]::Open($lockPath, 'OpenOrCreate', 'ReadWrite', 'None') }
catch { throw 'Another verification run is active. Wait for it to finish before starting another.' }
$buildRoot = Join-Path $projectRoot 'artifacts/check-build'
& $sdkPath restore 'tests/SundayRivalry.Checks' --artifacts-path $buildRoot --configfile NuGet.Config -p:NuGetAudit=false --ignore-failed-sources
if ($LASTEXITCODE -ne 0) { throw 'Restore failed.' }
& $sdkPath build 'tests/SundayRivalry.Checks' --artifacts-path $buildRoot --no-restore
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
$env:SUNDAY_RIVALRY_TEST_APP_DLL = Join-Path $buildRoot 'bin/SundayRivalry/debug/SundayRivalry.dll'
for ($round = 0; $round -lt $Repeat; $round++) {
    $suiteArguments = @('--suite', '--seed', ($Seed + $round), '--iterations', $Iterations)
    if ($SkipBrowser) { $suiteArguments += '--skip-browser' }
    & $sdkPath (Join-Path $buildRoot 'bin/SundayRivalry.Checks/debug/SundayRivalry.Checks.dll') @suiteArguments
    if ($LASTEXITCODE -ne 0) { throw 'Verification failed. See artifacts/verification for logs and the replay seed.' }
}
}
finally { if ($testLock) { $testLock.Dispose() } }
