param([switch]$Lan, [ValidateRange(1, 65535)][int]$Port = 5180, [switch]$NoBrowser)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
Set-Location -LiteralPath $projectRoot
$env:DOTNET_CLI_HOME = Join-Path $projectRoot '.tools/cli'
$env:NUGET_PACKAGES = Join-Path $projectRoot '.tools/packages'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$localUrl = "http://localhost:$Port"
$logDirectory = Join-Path $projectRoot 'src/SundayRivalry/App_Data/logs'
New-Item -ItemType Directory -Path $logDirectory -Force | Out-Null
$logPath = Join-Path $logDirectory ('launcher-{0}-{1}.log' -f (Get-Date -Format 'yyyyMMdd-HHmmss-fff'), $PID)
$launchLock = $null

function Write-LaunchMessage([string]$Message) {
    Write-Host $Message
    Add-Content -LiteralPath $logPath -Value ('[{0}] {1}' -f (Get-Date -Format 'o'), $Message)
}

function Test-SundayRivalry {
    try {
        # This public account page also works on the version already running
        # before the launcher fix. Don't confuse another web service with ours.
        $page = Invoke-WebRequest -Uri "$localUrl/login" -UseBasicParsing -TimeoutSec 2
        return $page.StatusCode -eq 200 -and
            $page.Content.Contains('SUNDAY RIVALRY / YOUR ACCOUNT') -and
            $page.Content.Contains('/account.css')
    }
    catch { return $false }
}

function Test-LocalPort {
    $client = New-Object System.Net.Sockets.TcpClient
    try {
        $connection = $client.BeginConnect('localhost', $Port, $null, $null)
        if (-not $connection.AsyncWaitHandle.WaitOne(750)) { return $false }
        $client.EndConnect($connection)
        return $true
    }
    catch { return $false }
    finally { $client.Dispose() }
}

function Open-App {
    if (-not $NoBrowser) {
        try { Start-Process $localUrl | Out-Null }
        catch { Write-LaunchMessage "Open $localUrl in your browser." }
    }
}

function Use-RunningApp {
    Write-LaunchMessage "Sunday Rivalry is already running at $localUrl. Reusing the existing app."
    Write-LaunchMessage 'No second server was started. Closing this window will leave the existing server running.'
    if ($Lan) { Write-LaunchMessage 'The existing network binding was kept. Stop that server before restarting to change LAN access.' }
    Open-App
}

try {
    Write-LaunchMessage "Starting Sunday Rivalry. Diagnostics: $logPath"
    if (Test-SundayRivalry) { Use-RunningApp; exit 0 }

    # An exclusive file handle serializes launchers, including two fast clicks
    # while the first instance is still restoring/building. It is released on exit.
    $lockPath = Join-Path $projectRoot ".tools/launcher-$Port.lock"
    New-Item -ItemType Directory -Path (Split-Path -Parent $lockPath) -Force | Out-Null
    $deadline = (Get-Date).AddSeconds(90)
    $announced = $false
    while ($null -eq $launchLock) {
        try { $launchLock = [IO.File]::Open($lockPath, [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None) }
        catch [IO.IOException] {
            if (Test-SundayRivalry) { Use-RunningApp; exit 0 }
            if ((Get-Date) -ge $deadline) { throw 'Another launcher is still starting the app. Check its window or retry after it finishes.' }
            if (-not $announced) { Write-LaunchMessage 'Another launcher is starting the app. Waiting for it to become ready...'; $announced = $true }
            Start-Sleep -Milliseconds 500
        }
    }

    # A server might have become ready between the first check and lock acquisition.
    if (Test-SundayRivalry) { Use-RunningApp; exit 0 }
    if (Test-LocalPort) {
        throw "Port $Port is occupied, but the service doesn't identify itself as Sunday Rivalry. No process was stopped. Close the conflicting service or select another port with -Port."
    }

    . (Join-Path $PSScriptRoot 'resolve-sdk.ps1')
    . (Join-Path $PSScriptRoot 'load-tank01-key.ps1')
    Write-LaunchMessage "Using .NET at $sdkPath"
    # Windows PowerShell 5 turns redirected native stderr into ErrorRecords.
    # Let the process finish and use its exit code, so a warning cannot abort it.
    $ErrorActionPreference = 'Continue'
    & $sdkPath restore 'src/SundayRivalry' --configfile 'NuGet.Config' 2>&1 | Tee-Object -FilePath $logPath -Append
    $restoreExit = $LASTEXITCODE
    $ErrorActionPreference = 'Stop'
    if ($restoreExit -ne 0) { throw "Package restore failed (exit $restoreExit). Check your connection and the diagnostic log." }

    $binding = $localUrl
    if ($Lan) { $binding = "http://0.0.0.0:$Port" }
    $serverArgs = @('--project', 'src/SundayRivalry', '--no-launch-profile', '--no-restore', '--', '--urls', $binding)
    if ($Lan) { $serverArgs += @('--AllowLan', 'true') }
    Write-LaunchMessage "Sunday Rivalry is starting at $localUrl"
    Write-LaunchMessage 'Keep this window open while playing. Press Ctrl+C to stop.'
    # The .NET process owns the terminal. Open the browser only after its ready
    # message arrives, and record both output streams for startup/runtime diagnosis.
    $browserOpened = $false
    $ErrorActionPreference = 'Continue'
    & $sdkPath run @serverArgs 2>&1 | ForEach-Object {
        $line = $_.ToString()
        Write-Output $line
        Add-Content -LiteralPath $logPath -Value $line
        if (-not $browserOpened -and $line -match 'Now listening on:') {
            $browserOpened = $true
            Open-App
        }
    }
    $serverExit = $LASTEXITCODE
    $ErrorActionPreference = 'Stop'
    if ($serverExit -ne 0) { throw "Sunday Rivalry stopped (exit $serverExit). See the diagnostic log above." }
    exit 0
}
catch {
    $ErrorActionPreference = 'Continue'
    Write-LaunchMessage ('Launch failed: ' + $_.Exception.Message)
    Write-Host "Full diagnostics: $logPath" -ForegroundColor Yellow
    exit 1
}
finally {
    if ($null -ne $launchLock) { $launchLock.Dispose() }
}
