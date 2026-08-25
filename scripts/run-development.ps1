[CmdletBinding()]
param(
    [string] $ApiUrl = 'http://localhost:5088',
    [string] $ConnectionString = $env:NEXORA_DEV_POSTGRES,
    [switch] $SkipBuild,
    [switch] $SkipMigrations
)

$ErrorActionPreference = 'Stop'
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$runtimeRoot = Join-Path $repoRoot '.nexora-local'
$logRoot = Join-Path $runtimeRoot 'logs'
$apiProcess = $null
$workerProcess = $null

function Invoke-Checked {
    param([string] $FilePath, [string[]] $Arguments)
    & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$FilePath exited with code $LASTEXITCODE." }
}

function Get-DevelopmentConnection {
    if (-not [string]::IsNullOrWhiteSpace($ConnectionString)) { return $ConnectionString }
    $secretLines = & dotnet user-secrets list --project (Join-Path $repoRoot 'src/Nexora.Api')
    if ($LASTEXITCODE -ne 0) { throw 'Unable to read local .NET user-secrets.' }
    $line = $secretLines | Where-Object { $_ -like 'ConnectionStrings:Postgres = *' } | Select-Object -First 1
    if ($null -eq $line) {
        throw 'Configure ConnectionStrings:Postgres in shared .NET user-secrets or set NEXORA_DEV_POSTGRES.'
    }
    return $line.Substring($line.IndexOf(' = ') + 3)
}

function Start-NexoraProcess {
    param([ValidateSet('Api', 'Worker')] [string] $Project)
    $stdout = Join-Path $logRoot "$($Project.ToLowerInvariant()).stdout.log"
    $stderr = Join-Path $logRoot "$($Project.ToLowerInvariant()).stderr.log"
    Remove-Item -LiteralPath $stdout, $stderr -Force -ErrorAction SilentlyContinue
    Start-Process -FilePath 'dotnet' -ArgumentList @(
        'run', '--project', "src/Nexora.$Project", '--no-build', '--no-launch-profile'
    ) -WorkingDirectory $repoRoot -PassThru -WindowStyle Hidden `
        -RedirectStandardOutput $stdout -RedirectStandardError $stderr
}

function Wait-Api {
    for ($attempt = 1; $attempt -le 80; $attempt++) {
        try {
            $response = Invoke-WebRequest -Uri "$ApiUrl/api/v1/health" -TimeoutSec 2
            if ($response.StatusCode -eq 200) { return }
        }
        catch { }
        Start-Sleep -Milliseconds 500
    }
    throw "API did not become ready. Inspect $logRoot."
}

function Stop-NexoraProcess {
    param([System.Diagnostics.Process] $Process)
    if ($null -ne $Process -and -not $Process.HasExited) {
        Stop-Process -Id $Process.Id -Force
        $null = $Process.WaitForExit(10000)
    }
}

$savedEnvironment = @{
    Dotnet = $env:DOTNET_ENVIRONMENT
    Aspnet = $env:ASPNETCORE_ENVIRONMENT
    Urls = $env:ASPNETCORE_URLS
    Connection = $env:ConnectionStrings__Postgres
}

Push-Location $repoRoot
try {
    $ConnectionString = Get-DevelopmentConnection
    New-Item -ItemType Directory -Path $logRoot -Force | Out-Null
    if (-not $SkipBuild) {
        Invoke-Checked 'dotnet' @('restore')
        Invoke-Checked 'dotnet' @('build', '--no-restore')
    }
    if (-not $SkipMigrations) {
        & (Join-Path $PSScriptRoot 'neon-dev-db.ps1') Migrate -ConnectionString $ConnectionString
        if ($LASTEXITCODE -ne 0) { throw 'Neon development migration failed.' }
    }

    $env:DOTNET_ENVIRONMENT = 'Development'
    $env:ASPNETCORE_ENVIRONMENT = 'Development'
    $env:ASPNETCORE_URLS = $ApiUrl
    $env:ConnectionStrings__Postgres = $ConnectionString

    $workerProcess = Start-NexoraProcess Worker
    $apiProcess = Start-NexoraProcess Api
    Wait-Api

    Write-Host 'Nexora API + Worker are ready.'
    Write-Host "API:      $ApiUrl/api/v1"
    Write-Host "OpenAPI:  $ApiUrl/openapi/v1.json"
    Write-Host 'Frontend: http://localhost:5173'
    Write-Host "Logs:     $logRoot"
    Write-Host 'Keep this terminal open. Press Ctrl+C to stop both processes.'

    while (-not $apiProcess.HasExited -and -not $workerProcess.HasExited) {
        Start-Sleep -Seconds 1
    }
    $failed = $apiProcess.HasExited ? 'API' : 'Worker'
    throw "$failed exited unexpectedly. Inspect $logRoot."
}
finally {
    Stop-NexoraProcess $apiProcess
    Stop-NexoraProcess $workerProcess
    $env:DOTNET_ENVIRONMENT = $savedEnvironment.Dotnet
    $env:ASPNETCORE_ENVIRONMENT = $savedEnvironment.Aspnet
    $env:ASPNETCORE_URLS = $savedEnvironment.Urls
    $env:ConnectionStrings__Postgres = $savedEnvironment.Connection
    Pop-Location
}
