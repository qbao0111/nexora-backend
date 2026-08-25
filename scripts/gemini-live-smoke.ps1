[CmdletBinding()]
param(
    [string] $ApiUrl = 'http://127.0.0.1:5088',
    [string] $ConnectionString = $env:NEXORA_DEV_POSTGRES,
    [switch] $KeepServicesRunning
)

$ErrorActionPreference = 'Stop'
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))

if ([string]::IsNullOrWhiteSpace($ConnectionString)) {
    $secretLines = & dotnet user-secrets list --project (Join-Path $repoRoot 'src/Nexora.Api')
    if ($LASTEXITCODE -ne 0) { throw 'Unable to read local .NET user-secrets.' }
    $connectionLine = $secretLines | Where-Object { $_ -like 'ConnectionStrings:Postgres = *' } | Select-Object -First 1
    if ($null -ne $connectionLine) {
        $ConnectionString = $connectionLine.Substring($connectionLine.IndexOf(' = ') + 3)
    }
}

Write-Warning 'This opt-in smoke sends synthetic CV/JD/answer text to the configured Gemini development model. Do not use real candidate data.'
& (Join-Path $PSScriptRoot 'local-smoke.ps1') -ApiUrl $ApiUrl -ConnectionString $ConnectionString `
    -Neon -AiProvider Gemini -KeepServicesRunning:$KeepServicesRunning
