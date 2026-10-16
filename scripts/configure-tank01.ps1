param()
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$secretDirectory = Join-Path $projectRoot '.tools'
New-Item -ItemType Directory -Path $secretDirectory -Force | Out-Null
Write-Host 'Paste your RapidAPI key at the private prompt. It will not be displayed.'
$secureKey = Read-Host 'Tank01 / RapidAPI key' -AsSecureString
if ($secureKey.Length -lt 20 -or $secureKey.Length -gt 256) { throw 'The API key length looks incorrect. No key was saved.' }
$encryptedKey = ConvertFrom-SecureString -SecureString $secureKey
[IO.File]::WriteAllText((Join-Path $secretDirectory 'tank01-api-key.txt'), $encryptedKey)
$secureKey.Dispose()
Write-Host 'Key saved with Windows encryption for this Windows account. Restart Sunday Rivalry to load it.'
Write-Host 'Keep the Tank01 NFL subscription on the free Basic plan. Setup does not make API calls.'
