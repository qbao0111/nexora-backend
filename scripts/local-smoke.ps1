[CmdletBinding()]
param(
    [string] $ApiUrl = 'http://127.0.0.1:5088',
    [string] $ConnectionString = $env:NEXORA_DEV_POSTGRES,
    [ValidateSet('Fake', 'Gemini')] [string] $AiProvider = 'Fake',
    [switch] $Neon,
    [switch] $SkipDocker,
    [switch] $KeepServicesRunning
)

$ErrorActionPreference = 'Stop'
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$runtimeRoot = [System.IO.Path]::GetFullPath((Join-Path $repoRoot '.nexora-local'))
$storageRoot = Join-Path $runtimeRoot 'storage'
$logRoot = Join-Path $runtimeRoot 'logs'
$localConnectionString = 'Host=localhost;Port=54329;Database=nexora_dev;Username=nexora_dev;Password=nexora_dev_only'
$jwtKey = 'nexora-local-development-only-signing-key-change-me'
$webhookSecret = 'nexora-local-development-only-webhook-secret'
$apiProcess = $null
$workerProcess = $null

function Invoke-Checked {
    param([string] $FilePath, [string[]] $Arguments)
    & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$FilePath exited with code $LASTEXITCODE." }
}

function Start-NexoraProcess {
    param([ValidateSet('Api', 'Worker')] [string] $Project)
    $projectPath = "src/Nexora.$Project"
    $stdout = Join-Path $logRoot "$($Project.ToLowerInvariant()).stdout.log"
    $stderr = Join-Path $logRoot "$($Project.ToLowerInvariant()).stderr.log"
    Remove-Item -LiteralPath $stdout, $stderr -Force -ErrorAction SilentlyContinue
    Start-Process -FilePath 'dotnet' -ArgumentList @('run', '--project', $projectPath, '--no-build', '--no-launch-profile') `
        -WorkingDirectory $repoRoot -PassThru -WindowStyle Hidden `
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

function Invoke-Json {
    param(
        [ValidateSet('Get', 'Post')] [string] $Method,
        [string] $Path,
        [object] $Body,
        [hashtable] $Headers = @{}
    )
    $parameters = @{
        Uri = "$ApiUrl$Path"
        Method = $Method
        Headers = $Headers
        ContentType = 'application/json'
        TimeoutSec = 15
    }
    if ($null -ne $Body) { $parameters.Body = $Body | ConvertTo-Json -Depth 10 -Compress }
    Invoke-RestMethod @parameters
}

function Wait-ForData {
    param([scriptblock] $Operation, [scriptblock] $Completed, [string] $Description)
    for ($attempt = 1; $attempt -le 60; $attempt++) {
        try {
            $result = & $Operation
            if (& $Completed $result) { return $result }
        }
        catch { }
        Start-Sleep -Milliseconds 500
    }
    throw "Timed out waiting for $Description."
}

function Stop-NexoraProcess {
    param([System.Diagnostics.Process] $Process)
    if ($null -ne $Process -and -not $Process.HasExited) {
        Stop-Process -Id $Process.Id -Force
        $null = $Process.WaitForExit(10000)
    }
}

function Get-DatabaseScalar {
    param([string] $Sql)
    if ($SkipDocker) {
        if (-not (Get-Command psql -ErrorAction SilentlyContinue)) {
            throw 'psql must be available on PATH when local smoke uses -SkipDocker.'
        }
        $previousPassword = $env:PGPASSWORD
        try {
            $env:PGPASSWORD = 'nexora_dev_only'
            $output = & psql -h localhost -p 54329 -U nexora_dev -d nexora_dev -v ON_ERROR_STOP=1 -tA -c $Sql
        }
        finally { $env:PGPASSWORD = $previousPassword }
    }
    else {
        $output = & docker compose -f (Join-Path $repoRoot 'compose.dev.yml') exec -T postgres `
            psql -U nexora_dev -d nexora_dev -v ON_ERROR_STOP=1 -tA -c $Sql
    }
    if ($LASTEXITCODE -ne 0) { throw 'PostgreSQL verification query failed.' }
    ($output | Select-Object -Last 1).Trim()
}

function Assert-NeonDevelopmentConnection {
    if ([string]::IsNullOrWhiteSpace($ConnectionString)) {
        throw 'Set NEXORA_DEV_POSTGRES or pass -ConnectionString with a Neon development-branch connection string.'
    }
    $hostMatch = [regex]::Match($ConnectionString, '(?i)(?:^|;)\s*Host\s*=\s*([^;]+)')
    if (-not $hostMatch.Success -or -not $hostMatch.Groups[1].Value.Trim().EndsWith('.neon.tech', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Neon smoke accepts only a key/value Npgsql connection string whose Host ends in .neon.tech.'
    }
    if ($ConnectionString -notmatch '(?i)(?:^|;)\s*SSL Mode\s*=\s*(Require|VerifyFull)\s*(?:;|$)') {
        throw 'Neon development connections must set SSL Mode=Require or SSL Mode=VerifyFull.'
    }
}

function Invoke-RemoteMigrations {
    $previousConnection = $env:ConnectionStrings__Postgres
    try {
        $env:ConnectionStrings__Postgres = $ConnectionString
        Invoke-Checked 'dotnet' @('tool', 'restore')
        Invoke-Checked 'dotnet' @('ef', 'database', 'update', '--project', 'src/Nexora.Data', '--startup-project', 'src/Nexora.Api', '--no-build')
        Invoke-Checked 'dotnet' @('ef', 'migrations', 'has-pending-model-changes', '--project', 'src/Nexora.Data', '--startup-project', 'src/Nexora.Api', '--no-build')
    }
    finally { $env:ConnectionStrings__Postgres = $previousConnection }
}

$savedEnvironment = @{
    DOTNET_ENVIRONMENT = $env:DOTNET_ENVIRONMENT
    ASPNETCORE_ENVIRONMENT = $env:ASPNETCORE_ENVIRONMENT
    ASPNETCORE_URLS = $env:ASPNETCORE_URLS
    Connection = $env:ConnectionStrings__Postgres
    Jwt = $env:Authentication__Jwt__SigningKey
    Storage = $env:Storage__Local__RootPath
    Ai = $env:Ai__Provider
    Webhook = $env:Billing__FakePayment__WebhookSecret
}

Push-Location $repoRoot
try {
    New-Item -ItemType Directory -Path $storageRoot, $logRoot -Force | Out-Null
    Invoke-Checked 'dotnet' @('restore')
    Invoke-Checked 'dotnet' @('build', '--no-restore')
    if ($Neon) {
        Assert-NeonDevelopmentConnection
        Invoke-RemoteMigrations
    }
    else {
        if ([string]::IsNullOrWhiteSpace($ConnectionString)) { $ConnectionString = $localConnectionString }
        if (-not $SkipDocker) {
            & (Join-Path $PSScriptRoot 'local-db.ps1') Up -ConnectionString $ConnectionString
        }
        & (Join-Path $PSScriptRoot 'local-db.ps1') Migrate -ConnectionString $ConnectionString -SkipDocker:$SkipDocker
    }

    $env:DOTNET_ENVIRONMENT = 'Development'
    $env:ASPNETCORE_ENVIRONMENT = 'Development'
    $env:ASPNETCORE_URLS = $ApiUrl
    $env:ConnectionStrings__Postgres = $ConnectionString
    $env:Authentication__Jwt__SigningKey = $jwtKey
    $env:Storage__Local__RootPath = $storageRoot
    $env:Ai__Provider = $AiProvider
    $env:Billing__FakePayment__WebhookSecret = $webhookSecret

    $workerProcess = Start-NexoraProcess Worker
    $apiProcess = Start-NexoraProcess Api
    Wait-Api

    $suffix = [Guid]::NewGuid().ToString('N')
    $email = "local-smoke-$suffix@example.test"
    $password = 'Strong!Pass123'
    $register = Invoke-Json Post '/api/v1/auth/register' @{ email = $email; password = $password; displayName = 'Local smoke candidate' }
    if (-not $register.data.user.id) { throw 'Registration response was incomplete.' }
    $login = Invoke-Json Post '/api/v1/auth/login' @{ email = $email; password = $password }
    $token = $login.data.accessToken
    $auth = @{ Authorization = "Bearer $token" }
    $me = Invoke-Json Get '/api/v1/me' $null $auth
    if ($me.data.email -ne $email) { throw 'Authenticated /me response did not match the registered user.' }

    $plans = Invoke-Json Get '/api/v1/plans' $null
    $basic = $plans.data | Where-Object code -eq 'basic' | Select-Object -First 1
    if ($null -eq $basic) { throw 'Seeded basic plan was not found.' }
    $checkoutHeaders = $auth.Clone()
    $checkoutHeaders['Idempotency-Key'] = "local-checkout-$suffix"
    $checkout = Invoke-Json Post '/api/v1/checkout-sessions' @{ planPriceId = $basic.prices[0].id } $checkoutHeaders
    $orderId = [Guid]$checkout.data.orderId

    $eventId = "local-event-$suffix"
    $timestamp = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds().ToString([Globalization.CultureInfo]::InvariantCulture)
    $webhookBody = @{
        eventId = $eventId
        orderId = $orderId
        transactionId = "fake_$($orderId.ToString('N'))"
        status = 'paid'
        occurredAt = [DateTimeOffset]::UtcNow
    } | ConvertTo-Json -Compress
    $hmac = [Security.Cryptography.HMACSHA256]::new([Text.Encoding]::UTF8.GetBytes($webhookSecret))
    try {
        $signature = [Convert]::ToHexString($hmac.ComputeHash([Text.Encoding]::UTF8.GetBytes("$timestamp.$webhookBody"))).ToLowerInvariant()
    }
    finally { $hmac.Dispose() }
    $webhookHeaders = @{ 'X-Payment-Timestamp' = $timestamp; 'X-Payment-Signature' = $signature }
    $null = Invoke-RestMethod -Method Post -Uri "$ApiUrl/api/v1/webhooks/payments/fake" -Headers $webhookHeaders `
        -ContentType 'application/json' -Body $webhookBody -TimeoutSec 15
    $null = Invoke-RestMethod -Method Post -Uri "$ApiUrl/api/v1/webhooks/payments/fake" -Headers $webhookHeaders `
        -ContentType 'application/json' -Body $webhookBody -TimeoutSec 15
    $me = Wait-ForData { Invoke-Json Get '/api/v1/me' $null $auth } { param($value) $null -ne $value.data.billing.entitlement } 'entitlement fulfillment'
    if (($me.data.billing.orders | Where-Object id -eq $orderId).Count -ne 1) {
        throw 'Duplicate webhook flow did not retain exactly one order.'
    }

    $resumeBytes = [Text.Encoding]::ASCII.GetBytes("%PDF-1.7`n% Nexora deterministic local smoke resume")
    $intent = Invoke-Json Post '/api/v1/uploads/presign' @{
        fileName = 'resume.pdf'; contentType = 'application/pdf'; size = $resumeBytes.Length
    } $auth
    Invoke-WebRequest -Method Put -Uri "$ApiUrl$($intent.data.uploadUrl)" -Body $resumeBytes -ContentType 'application/pdf' -TimeoutSec 15 | Out-Null
    $resume = Invoke-Json Post '/api/v1/resumes' @{ uploadToken = $intent.data.token } $auth
    $jd = Invoke-Json Post '/api/v1/job-descriptions' @{
        title = 'Backend Developer'; content = 'Build reliable APIs with .NET, PostgreSQL, tests, and clear communication.'
    } $auth

    $analysisHeaders = $auth.Clone()
    $analysisHeaders['Idempotency-Key'] = "local-analysis-$suffix"
    $analysis = Wait-ForData {
        Invoke-Json Post '/api/v1/resume-analyses' @{ resumeId = $resume.data.id; jobDescriptionId = $jd.data.id } $analysisHeaders
    } { param($value) $null -ne $value.data.id } 'resume extraction'
    $analysis = Wait-ForData {
        Invoke-Json Get "/api/v1/resume-analyses/$($analysis.data.id)" $null $auth
    } { param($value) $value.data.status -eq 'completed' } 'resume analysis'

    $interviewHeaders = $auth.Clone()
    $interviewHeaders['Idempotency-Key'] = "local-interview-$suffix"
    $interview = Invoke-Json Post '/api/v1/interviews' @{
        role = 'Backend Developer'; seniority = 'junior'; interviewType = 'behavioral'; difficulty = 'medium'
        resumeId = $resume.data.id; jobDescriptionId = $jd.data.id
    } $interviewHeaders
    $interviewId = $interview.data.id
    $interview = Wait-ForData {
        Invoke-Json Get "/api/v1/interviews/$interviewId" $null $auth
    } { param($value) $value.data.status -eq 'active' -and $value.data.questions.Count -eq 1 } 'active interview'

    $answerHeaders = $auth.Clone()
    $answerHeaders['Idempotency-Key'] = "local-answer-one-$suffix"
    $firstAnswer = Invoke-Json Post "/api/v1/interviews/$interviewId/answers" @{
        questionId = $interview.data.questions[0].id
        content = 'Tôi xác định nguyên nhân, phối hợp với đội và giảm lỗi 30 phần trăm.'
        durationSeconds = 45
    } $answerHeaders
    $answerHeaders['Idempotency-Key'] = "local-answer-two-$suffix"
    $secondAnswer = Invoke-Json Post "/api/v1/interviews/$interviewId/answers" @{
        questionId = $firstAnswer.data.nextQuestion.id
        content = 'Tôi sẽ đo baseline sớm hơn và kiểm tra tiến độ mỗi tuần.'
        durationSeconds = 40
    } $answerHeaders
    if (-not $secondAnswer.data.isComplete) { throw 'Interview did not accept all official answers.' }

    $completeHeaders = $auth.Clone()
    $completeHeaders['Idempotency-Key'] = "local-complete-$suffix"
    $null = Invoke-Json Post "/api/v1/interviews/$interviewId/complete" $null $completeHeaders
    $report = Wait-ForData {
        Invoke-Json Get "/api/v1/interviews/$interviewId/report" $null $auth
    } { param($value) $null -ne $value.data.overallScore } 'completed report'
    $dashboard = Invoke-Json Get '/api/v1/dashboard' $null $auth
    if ($dashboard.data.interviews.id -notcontains $interviewId -or $dashboard.data.reports.interviewId -notcontains $interviewId) {
        throw 'Dashboard did not contain the completed interview and report.'
    }
    if (($dashboard.data.reports | Where-Object interviewId -eq $interviewId).Count -ne 1) {
        throw 'Dashboard exposed duplicate reports for one interview.'
    }

    Stop-NexoraProcess $apiProcess
    $apiProcess = Start-NexoraProcess Api
    Wait-Api
    $persistedDashboard = Invoke-Json Get '/api/v1/dashboard' $null $auth
    $persistedReport = Invoke-Json Get "/api/v1/interviews/$interviewId/report" $null $auth
    if ($persistedDashboard.data.interviews.id -notcontains $interviewId -or $persistedReport.data.id -ne $report.data.id) {
        throw 'PostgreSQL persistence did not survive the API restart.'
    }

    if (-not $Neon) {
        $paymentCountSql = 'SELECT COUNT(*) FROM payment_events WHERE "ProviderEventId" = ''{0}'';' -f $eventId
        if ((Get-DatabaseScalar $paymentCountSql) -ne '1') {
            throw 'Duplicate fake webhook produced duplicate payment events.'
        }
        $reportCountSql = 'SELECT COUNT(*) FROM interview_reports WHERE "InterviewSessionId" = ''{0}'';' -f $interviewId
        if ((Get-DatabaseScalar $reportCountSql) -ne '1') {
            throw 'Interview report was not idempotent.'
        }
        if ((Get-DatabaseScalar 'SELECT COUNT(*) FROM outbox_events WHERE "Status" = ''processing'';') -ne '0') {
            throw 'An outbox job remained stuck in processing state.'
        }
    }

    $providerLabel = $AiProvider.ToUpperInvariant()
    Write-Host ($Neon ? "INTERNAL DEVELOPMENT ENVIRONMENT READY (NEON, $providerLabel AI)" : "LOCAL INTERNAL ENVIRONMENT READY ($providerLabel AI)")
    Write-Host "Evidence logs: $logRoot"
}
finally {
    if (-not $KeepServicesRunning) {
        Stop-NexoraProcess $apiProcess
        Stop-NexoraProcess $workerProcess
    }
    $env:DOTNET_ENVIRONMENT = $savedEnvironment.DOTNET_ENVIRONMENT
    $env:ASPNETCORE_ENVIRONMENT = $savedEnvironment.ASPNETCORE_ENVIRONMENT
    $env:ASPNETCORE_URLS = $savedEnvironment.ASPNETCORE_URLS
    $env:ConnectionStrings__Postgres = $savedEnvironment.Connection
    $env:Authentication__Jwt__SigningKey = $savedEnvironment.Jwt
    $env:Storage__Local__RootPath = $savedEnvironment.Storage
    $env:Ai__Provider = $savedEnvironment.Ai
    $env:Billing__FakePayment__WebhookSecret = $savedEnvironment.Webhook
    Pop-Location
}
