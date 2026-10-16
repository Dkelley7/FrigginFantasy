$keyFile = Join-Path $projectRoot '.tools/tank01-api-key.txt'
if (-not $env:Tank01__ApiKey -and (Test-Path -LiteralPath $keyFile)) {
    $protectedKey = ConvertTo-SecureString ([IO.File]::ReadAllText($keyFile))
    try { $env:Tank01__ApiKey = ([Net.NetworkCredential]::new('', $protectedKey)).Password }
    finally { $protectedKey.Dispose() }
}
