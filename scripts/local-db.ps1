[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [ValidateSet('Up', 'Migrate', 'Verify', 'Reset', 'Down')]
    [string] $Action = 'Up',

    [string] $ConnectionString = 'Host=localhost;Port=54329;Database=nexora_dev;Username=nexora_dev;Password=nexora_dev_only',

    [switch] $SkipDocker,
    [switch] $ResetStorage
)

$ErrorActionPreference = 'Stop'
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$composeFile = Join-Path $repoRoot 'compose.dev.yml'
$storageRoot = [System.IO.Path]::GetFullPath((Join-Path $repoRoot '.nexora-local\storage'))

function Invoke-Checked {
    param([string] $FilePath, [string[]] $Arguments)
    & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$FilePath exited with code $LASTEXITCODE."
    }
}

function Assert-LocalConnection {
    $hostMatch = [regex]::Match($ConnectionString, '(?i)(?:^|;)\s*Host\s*=\s*([^;]+)')
    $databaseMatch = [regex]::Match($ConnectionString, '(?i)(?:^|;)\s*Database\s*=\s*([^;]+)')
    if (-not $hostMatch.Success -or $hostMatch.Groups[1].Value.Trim() -notin @('localhost', '127.0.0.1', '::1')) {
        throw 'Refusing local database operation: Host must be localhost, 127.0.0.1, or ::1.'
    }
    if (-not $databaseMatch.Success -or $databaseMatch.Groups[1].Value.Trim() -ne 'nexora_dev') {
        throw 'Refusing local database operation: Database must be exactly nexora_dev.'
    }
}

function Assert-Docker {
    if ($SkipDocker) { return }
    if (-not (Get-Command docker -ErrorAction SilentlyContinue)) {
        throw 'Docker is required for this action. Install Docker Desktop, or use -SkipDocker with Migrate/Verify against a manually installed local PostgreSQL instance.'
    }
    Invoke-Checked 'docker' @('compose', '-f', $composeFile, 'version')
}

function Wait-Postgres {
    if ($SkipDocker) { return }
    for ($attempt = 1; $attempt -le 60; $attempt++) {
        & docker compose -f $composeFile exec -T postgres pg_isready -U nexora_dev -d nexora_dev *> $null
        if ($LASTEXITCODE -eq 0) { return }
        Start-Sleep -Milliseconds 500
    }
    throw 'Local PostgreSQL did not become healthy within 30 seconds.'
}

function Invoke-Migrations {
    $previousConnection = $env:ConnectionStrings__Postgres
    try {
        $env:ConnectionStrings__Postgres = $ConnectionString
        Invoke-Checked 'dotnet' @('tool', 'restore')
        Invoke-Checked 'dotnet' @('ef', 'database', 'update', '--project', 'src/Nexora.Data', '--startup-project', 'src/Nexora.Api', '--no-build')
    }
    finally {
        $env:ConnectionStrings__Postgres = $previousConnection
    }
}

function Test-ModelDrift {
    $previousConnection = $env:ConnectionStrings__Postgres
    try {
        $env:ConnectionStrings__Postgres = $ConnectionString
        Invoke-Checked 'dotnet' @('ef', 'migrations', 'has-pending-model-changes', '--project', 'src/Nexora.Data', '--startup-project', 'src/Nexora.Api', '--no-build')
    }
    finally {
        $env:ConnectionStrings__Postgres = $previousConnection
    }
}

Push-Location $repoRoot
try {
    Assert-LocalConnection
    Assert-Docker

    switch ($Action) {
        'Up' {
            if ($SkipDocker) { throw 'Up requires Docker; start a manual local PostgreSQL service yourself.' }
            Invoke-Checked 'docker' @('compose', '-f', $composeFile, 'up', '-d')
            Wait-Postgres
            Write-Host 'Local PostgreSQL is healthy on 127.0.0.1:54329.'
        }
        'Migrate' {
            Wait-Postgres
            Invoke-Migrations
            Test-ModelDrift
            Write-Host 'Migrations are current and the EF model has no pending changes.'
        }
        'Verify' {
            Wait-Postgres
            Test-ModelDrift
            if (-not $SkipDocker) {
                Invoke-Checked 'docker' @('compose', '-f', $composeFile, 'exec', '-T', 'postgres', 'psql', '-U', 'nexora_dev', '-d', 'nexora_dev', '-v', 'ON_ERROR_STOP=1', '-c', 'SELECT COUNT(*) AS applied_migrations FROM "__EFMigrationsHistory";')
            }
            Write-Host 'Local PostgreSQL and migration model verification passed.'
        }
        'Reset' {
            if ($SkipDocker) { throw 'Reset is intentionally unavailable with -SkipDocker. Reset the manually installed local instance using its own administration tools.' }
            Invoke-Checked 'docker' @('compose', '-f', $composeFile, 'down', '--volumes', '--remove-orphans')
            if ($ResetStorage -and (Test-Path -LiteralPath $storageRoot)) {
                $allowedRoot = [System.IO.Path]::GetFullPath((Join-Path $repoRoot '.nexora-local')) + [System.IO.Path]::DirectorySeparatorChar
                if (-not $storageRoot.StartsWith($allowedRoot, [System.StringComparison]::OrdinalIgnoreCase) -or
                    [System.IO.Path]::GetFileName($storageRoot) -ne 'storage') {
                    throw "Refusing storage reset outside the known local root: $storageRoot"
                }
                Remove-Item -LiteralPath $storageRoot -Recurse -Force
            }
            Write-Host 'Known local PostgreSQL volume reset completed. Use Up then Migrate to recreate it.'
        }
        'Down' {
            if ($SkipDocker) { throw 'Down requires Docker; stop a manual local PostgreSQL service yourself.' }
            Invoke-Checked 'docker' @('compose', '-f', $composeFile, 'down')
            Write-Host 'Local PostgreSQL stopped; its named volume was preserved.'
        }
    }
}
finally {
    Pop-Location
}
