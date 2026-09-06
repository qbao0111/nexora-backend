[CmdletBinding()]
param(
    [string] $ServiceId,
    [switch] $Wait
)

$ErrorActionPreference = 'Stop'

# 1. Verify Render CLI
$renderCmd = Get-Command render -ErrorAction SilentlyContinue
if ($null -eq $renderCmd) {
    throw "Render CLI ('render') is not found in PATH. Please install or ensure render.exe is accessible."
}

Write-Host "Render CLI version:" -ForegroundColor Cyan
render --version

# 2. Display safe account/workspace metadata
Write-Host "`nCurrent user and workspace:" -ForegroundColor Cyan
render whoami
render workspace current

# 3. Locate service if not provided
if ([string]::IsNullOrWhiteSpace($ServiceId)) {
    Write-Host "`nSearching for 'nexora-staging' service..." -ForegroundColor Cyan
    $servicesJson = render services --output json | ConvertFrom-Json
    $matched = $servicesJson | Where-Object { $_.service.name -eq 'nexora-staging' } | Select-Object -First 1
    if ($null -ne $matched) {
        $ServiceId = $matched.service.id
        Write-Host "Found service: $($matched.service.name) (ID: $ServiceId)" -ForegroundColor Green
    } else {
        Write-Host "Service 'nexora-staging' was not found in the active workspace." -ForegroundColor Yellow
        Write-Host "Create it using: render services create --name nexora-staging --type web_service --runtime docker --plan free --region singapore --repo https://github.com/qbao0111/nexora-backend --branch deploy/render-free-staging"
        return
    }
}

# 4. Trigger deployment
Write-Host "`nTriggering deployment for service $ServiceId..." -ForegroundColor Cyan
if ($Wait) {
    render deploys create $ServiceId
} else {
    render deploys create $ServiceId
}
