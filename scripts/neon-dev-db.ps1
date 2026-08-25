[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [ValidateSet('Migrate', 'Verify')]
    [string] $Action = 'Verify',

    [string] $ConnectionString = $env:NEXORA_DEV_POSTGRES
)

$ErrorActionPreference = 'Stop'
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))

function Invoke-Checked {
    param([string] $FilePath, [string[]] $Arguments)
    & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$FilePath exited with code $LASTEXITCODE." }
}

if ([string]::IsNullOrWhiteSpace($ConnectionString)) {
    throw 'Set NEXORA_DEV_POSTGRES or pass -ConnectionString with a dedicated Neon development-branch connection string.'
}
$hostMatch = [regex]::Match($ConnectionString, '(?i)(?:^|;)\s*Host\s*=\s*([^;]+)')
if (-not $hostMatch.Success -or -not $hostMatch.Groups[1].Value.Trim().EndsWith('.neon.tech', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Only a key/value Npgsql connection string whose Host ends in .neon.tech is accepted.'
}
if ($ConnectionString -notmatch '(?i)(?:^|;)\s*SSL Mode\s*=\s*(Require|VerifyFull)\s*(?:;|$)') {
    throw 'Neon development connections must set SSL Mode=Require or SSL Mode=VerifyFull.'
}

$previousConnection = $env:ConnectionStrings__Postgres
Push-Location $repoRoot
try {
    $env:ConnectionStrings__Postgres = $ConnectionString
    Invoke-Checked 'dotnet' @('tool', 'restore')
    if ($Action -eq 'Migrate') {
        Invoke-Checked 'dotnet' @('ef', 'database', 'update', '--project', 'src/Nexora.Data', '--startup-project', 'src/Nexora.Api', '--no-build')
    }
    Invoke-Checked 'dotnet' @('ef', 'migrations', 'has-pending-model-changes', '--project', 'src/Nexora.Data', '--startup-project', 'src/Nexora.Api', '--no-build')
    Write-Host "Neon development database $Action completed; no model drift detected."
}
finally {
    $env:ConnectionStrings__Postgres = $previousConnection
    Pop-Location
}
