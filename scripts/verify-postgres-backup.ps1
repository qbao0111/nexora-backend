[CmdletBinding()]
param(
    [switch]$ConfirmIsolatedTarget
)

$ErrorActionPreference = 'Stop'

if (-not $ConfirmIsolatedTarget) {
    throw 'Pass -ConfirmIsolatedTarget only after verifying the restore database is isolated and disposable.'
}

$source = $env:NEXORA_BACKUP_SOURCE
$target = $env:NEXORA_RESTORE_TARGET
$apiReadUrl = $env:NEXORA_RESTORE_API_READ_URL
$apiToken = $env:NEXORA_RESTORE_API_TOKEN
if ([string]::IsNullOrWhiteSpace($source) -or [string]::IsNullOrWhiteSpace($target)) {
    throw 'NEXORA_BACKUP_SOURCE and NEXORA_RESTORE_TARGET are required.'
}
if ($source.Trim() -eq $target.Trim()) {
    throw 'Source and restore target must be different databases.'
}
if ([string]::IsNullOrWhiteSpace($apiReadUrl) -or [string]::IsNullOrWhiteSpace($apiToken)) {
    throw 'NEXORA_RESTORE_API_READ_URL and NEXORA_RESTORE_API_TOKEN are required to prove an authenticated API read after restore.'
}

function Get-DatabaseName([string]$connection) {
    if ($connection -match '(?i)(?:Database|Initial Catalog)\s*=\s*([^;]+)') { return $Matches[1].Trim() }
    if ($connection -match '^postgres(?:ql)?://') { return ([Uri]$connection).AbsolutePath.Trim('/') }
    return ''
}

$targetDatabase = Get-DatabaseName $target
if ($targetDatabase -notmatch '(?i)(isolated|restore|drill|test)') {
    throw "Restore target database name '$targetDatabase' must clearly identify an isolated restore/test database."
}

$dumpPath = Join-Path ([IO.Path]::GetTempPath()) "nexora-restore-drill-$([Guid]::NewGuid().ToString('N')).dump"
try {
    & pg_dump "--dbname=$source" --format=custom "--file=$dumpPath"
    if ($LASTEXITCODE -ne 0) { throw 'pg_dump failed.' }

    & pg_restore "--dbname=$target" --clean --if-exists --no-owner --no-privileges $dumpPath
    if ($LASTEXITCODE -ne 0) { throw 'pg_restore failed.' }

    $headers = @{ Authorization = "Bearer $apiToken" }
    $response = Invoke-RestMethod -Uri $apiReadUrl -Headers $headers -Method Get
    if ($null -eq $response.data) { throw 'Restored API response did not contain the canonical data envelope.' }
    Write-Host 'T-10 restore drill passed: backup restored to an isolated target and authenticated API data was readable.'
}
finally {
    if (Test-Path -LiteralPath $dumpPath) { Remove-Item -LiteralPath $dumpPath -Force }
}
