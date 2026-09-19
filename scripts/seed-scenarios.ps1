# scripts/seed-scenarios.ps1
[CmdletBinding(DefaultParameterSetName = "Seed")]
param(
    [string]$ApiBaseUrl = "http://localhost:5088",
    [Parameter(Mandatory = $true, ParameterSetName = "Seed")]
    [string]$AccessToken,
    [Parameter(ParameterSetName = "Seed")]
    [switch]$UpdateExisting,
    [Parameter(Mandatory = $false, ParameterSetName = "ValidateOnly")]
    [switch]$ValidateOnly
)

$ErrorActionPreference = "Stop"
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$jsonPath = Join-Path $repoRoot "scripts/data/scenarios.vi.json"

Write-Host "========================================="
Write-Host " Nexora Scenario Library Seeder"
Write-Host "========================================="
Write-Host "Target API Base: $ApiBaseUrl"
if (-not $ValidateOnly) {
    Write-Host "Update Existing: $([bool]$UpdateExisting)"
} else {
    Write-Host "Mode: ValidateOnly"
}

# 1. Validate scenarios.vi.json exists
if (-not (Test-Path $jsonPath -PathType Leaf)) {
    throw "Dataset file not found at '$jsonPath'."
}

# 2. Parse JSON
try {
    $rawJson = [System.IO.File]::ReadAllText($jsonPath, [System.Text.Encoding]::UTF8)
    $dataset = $rawJson | ConvertFrom-Json
}
catch {
    throw "Failed to parse JSON dataset: $($_.Exception.Message)"
}

# 3. Keep the expected library shape in one place
$scenarios = $dataset.scenarios
$expectedLegacyCategoryCounts = [ordered]@{
    "banking" = 4
    "ecommerce" = 4
    "logistics" = 4
}
$expectedSoftwareCategoryCounts = [ordered]@{
    "backend" = 5
    "frontend" = 5
    "dotnet" = 5
    "java" = 5
    "database" = 5
    "devops" = 5
    "qa-testing" = 5
    "mobile" = 5
    "software-architecture" = 5
    "app-security" = 5
}
$expectedCategoryCounts = [ordered]@{}
foreach ($expectedCategory in @($expectedLegacyCategoryCounts.GetEnumerator()) + @($expectedSoftwareCategoryCounts.GetEnumerator())) {
    $expectedCategoryCounts[$expectedCategory.Key] = $expectedCategory.Value
}
$expectedLegacyDifficultyCounts = [ordered]@{
    "easy" = 3
    "medium" = 6
    "hard" = 3
}
$expectedNewDifficultyPerCategory = [ordered]@{
    "easy" = 1
    "medium" = 3
    "hard" = 1
}
$expectedDifficultyCounts = [ordered]@{}
foreach ($difficulty in $expectedLegacyDifficultyCounts.Keys) {
    $expectedDifficultyCounts[$difficulty] = $expectedLegacyDifficultyCounts[$difficulty] +
        ($expectedSoftwareCategoryCounts.Count * $expectedNewDifficultyPerCategory[$difficulty])
}
$expectedTotal = ($expectedCategoryCounts.Values | Measure-Object -Sum).Sum
$allowedCategories = @($expectedCategoryCounts.Keys)
$allowedDifficulties = @($expectedDifficultyCounts.Keys)

if ($null -eq $scenarios -or $scenarios.Count -ne $expectedTotal) {
    throw "Dataset must contain exactly $expectedTotal scenarios, found $(if ($scenarios) { $scenarios.Count } else { 0 })."
}

# 4. Validate schema and constraints
$allowedCompetencies = @(
    "customer_service",
    "problem_solving",
    "prioritization",
    "risk_management",
    "stakeholder_management",
    "decision_making",
    "communication",
    "operational_judgment"
)

$slugSet = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
$categoryCounts = @{}
foreach ($category in $allowedCategories) {
    $categoryCounts[$category] = 0
}
$difficultyCounts = @{}
foreach ($difficulty in $allowedDifficulties) {
    $difficultyCounts[$difficulty] = 0
}

foreach ($s in $scenarios) {
    if ([string]::IsNullOrWhiteSpace($s.slug)) { throw "Scenario slug cannot be empty." }
    if (-not $slugSet.Add($s.slug)) { throw "Duplicate slug detected: '$($s.slug)'." }

    if ($allowedCategories -notcontains $s.category) {
        throw "Invalid category '$($s.category)' for scenario '$($s.slug)'."
    }
    $categoryCounts[$s.category]++

    if ($allowedDifficulties -notcontains $s.difficulty) {
        throw "Invalid difficulty '$($s.difficulty)' for scenario '$($s.slug)'."
    }
    $difficultyCounts[$s.difficulty]++

    if ($allowedCompetencies -notcontains $s.competency) {
        throw "Invalid competency '$($s.competency)' for scenario '$($s.slug)'."
    }

    if ([string]::IsNullOrWhiteSpace($s.title) -or $s.title.Length -gt 200) {
        throw "Invalid title for scenario '$($s.slug)'."
    }

    if ([string]::IsNullOrWhiteSpace($s.summary) -or $s.summary.Length -gt 500) {
        throw "Invalid summary for scenario '$($s.slug)'."
    }

    if ($s.summary -eq $s.title) {
        throw "Summary cannot be identical to title for scenario '$($s.slug)'."
    }

    if ($s.estimatedMinutes -le 0 -or $s.estimatedMinutes -gt 600) {
        throw "Estimated minutes for '$($s.slug)' must be between 1 and 600."
    }

    if ($s.difficulty -eq "easy" -and ($s.estimatedMinutes -lt 8 -or $s.estimatedMinutes -gt 10)) {
        throw "Easy scenario '$($s.slug)' estimatedMinutes ($($s.estimatedMinutes)) out of expected range 8-10."
    }
    if ($s.difficulty -eq "medium" -and ($s.estimatedMinutes -lt 12 -or $s.estimatedMinutes -gt 15)) {
        throw "Medium scenario '$($s.slug)' estimatedMinutes ($($s.estimatedMinutes)) out of expected range 12-15."
    }
    if ($s.difficulty -eq "hard" -and ($s.estimatedMinutes -lt 15 -or $s.estimatedMinutes -gt 20)) {
        throw "Hard scenario '$($s.slug)' estimatedMinutes ($($s.estimatedMinutes)) out of expected range 15-20."
    }

    if ([string]::IsNullOrWhiteSpace($s.content) -or $s.content.Length -gt 20000) {
        throw "Invalid content for scenario '$($s.slug)'."
    }

    if (-not $s.content.Contains("## Bối cảnh") -or
        -not $s.content.Contains("## Dữ kiện") -or
        -not $s.content.Contains("## Nhiệm vụ của bạn")) {
        throw "Scenario '$($s.slug)' missing required content markdown sections."
    }

    if ($s.content.Contains("## Đáp án") -or
        $s.content.Contains("## Gợi ý trả lời") -or
        $s.content.Contains("## Câu trả lời mẫu")) {
        throw "Scenario '$($s.slug)' contains forbidden answer/key sections."
    }
}

foreach ($expectedCategory in $expectedCategoryCounts.GetEnumerator()) {
    if ($categoryCounts[$expectedCategory.Key] -ne $expectedCategory.Value) {
        throw "Dataset must have exactly $($expectedCategory.Value) '$($expectedCategory.Key)' scenarios, found $($categoryCounts[$expectedCategory.Key])."
    }
}
foreach ($expectedSoftwareCategory in $expectedSoftwareCategoryCounts.GetEnumerator()) {
    $categoryScenarios = @($scenarios | Where-Object { $_.category -eq $expectedSoftwareCategory.Key })
    foreach ($expectedDifficulty in $expectedNewDifficultyPerCategory.GetEnumerator()) {
        $actualDifficultyCount = @($categoryScenarios | Where-Object { $_.difficulty -eq $expectedDifficulty.Key }).Count
        if ($actualDifficultyCount -ne $expectedDifficulty.Value) {
            throw "Software category '$($expectedSoftwareCategory.Key)' must have exactly $($expectedDifficulty.Value) '$($expectedDifficulty.Key)' scenarios, found $actualDifficultyCount."
        }
    }
}
foreach ($expectedDifficulty in $expectedDifficultyCounts.GetEnumerator()) {
    if ($difficultyCounts[$expectedDifficulty.Key] -ne $expectedDifficulty.Value) {
        throw "Dataset must have exactly $($expectedDifficulty.Value) '$($expectedDifficulty.Key)' scenarios, found $($difficultyCounts[$expectedDifficulty.Key])."
    }
}

$categorySummary = ($expectedCategoryCounts.GetEnumerator() | ForEach-Object { "$($_.Key)=$($_.Value)" }) -join ", "
$difficultySummary = ($expectedDifficultyCounts.GetEnumerator() | ForEach-Object { "$($_.Key)=$($_.Value)" }) -join ", "
Write-Host "Dataset validation passed ($expectedTotal scenarios; categories: $categorySummary; difficulties: $difficultySummary)."

if ($ValidateOnly) {
    Write-Host "`nAll validation checks PASSED. Exiting due to -ValidateOnly switch." -ForegroundColor Green
    return
}

# API Helper
function Invoke-NexoraApi {
    param(
        [Parameter(Mandatory = $true)][string]$Method,
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $false)]$BodyObject
    )

    $url = "$($ApiBaseUrl.TrimEnd('/'))$Path"
    $headers = @{
        "Authorization" = "Bearer $AccessToken"
        "Accept" = "application/json"
    }

    $requestParams = @{
        Uri = $url
        Method = $Method
        Headers = $headers
        TimeoutSec = 30
    }

    if ($null -ne $BodyObject) {
        $json = $BodyObject | ConvertTo-Json -Depth 10 -Compress
        $requestParams["Body"] = [System.Text.Encoding]::UTF8.GetBytes($json)
        $requestParams["ContentType"] = "application/json; charset=utf-8"
    }

    try {
        $resp = Invoke-RestMethod @requestParams
        return @{
            Success = $true
            StatusCode = 200
            Data = $resp.data
            Error = $null
        }
    }
    catch {
        $statusCode = $null
        $errorBody = ""
        if ($_.Exception.Response) {
            $resp = $_.Exception.Response
            if ($resp.StatusCode) {
                $statusCode = [int]$resp.StatusCode
            }
            try {
                if ($resp.GetResponseStream) {
                    $stream = $resp.GetResponseStream()
                    $reader = [System.IO.StreamReader]::new($stream)
                    $errorBody = $reader.ReadToEnd()
                } elseif ($_.ErrorDetails -and $_.ErrorDetails.Message) {
                    $errorBody = $_.ErrorDetails.Message
                }
            } catch {
                $errorBody = $_.Exception.Message
            }
        } else {
            $errorBody = $_.Exception.Message
        }

        return @{
            Success = $false
            StatusCode = $statusCode
            Data = $null
            Error = $errorBody
        }
    }
}

# 5. GET admin categories
Write-Host "Fetching admin scenario categories..."
$catRes = Invoke-NexoraApi -Method "Get" -Path "/api/v1/admin/scenario-categories"
if (-not $catRes.Success) {
    Write-Host "Failed to fetch categories. HTTP $($catRes.StatusCode): $($catRes.Error)" -ForegroundColor Red
    throw "Cannot proceed without admin categories."
}

# 6. Build slug -> categoryId map
$categoryMap = @{}
foreach ($cat in $catRes.Data) {
    $categoryMap[$cat.slug] = $cat.id
}

# 7. Fail clearly if categories missing
foreach ($reqCat in $allowedCategories) {
    if (-not $categoryMap.ContainsKey($reqCat)) {
        throw "Required category '$reqCat' is missing from the database. Please ensure category is seeded or created."
    }
}

# 8. GET existing admin scenarios
Write-Host "Fetching existing admin scenarios..."
$scenariosRes = Invoke-NexoraApi -Method "Get" -Path "/api/v1/admin/scenarios"
if (-not $scenariosRes.Success) {
    Write-Host "Failed to fetch admin scenarios. HTTP $($scenariosRes.StatusCode): $($scenariosRes.Error)" -ForegroundColor Red
    throw "Cannot proceed without existing admin scenarios list."
}

$existingScenarios = @{}
foreach ($item in $scenariosRes.Data) {
    $existingScenarios[$item.slug] = $item
}

$createdCount = 0
$publishedCount = 0
$updatedCount = 0
$skippedCount = 0
$failedCount = 0

# 9 & 10. Process each dataset item
Write-Host "`nProcessing scenarios..."
foreach ($s in $scenarios) {
    $slug = $s.slug
    $targetCatId = $categoryMap[$s.category]

    if ($existingScenarios.ContainsKey($slug)) {
        $existing = $existingScenarios[$slug]
        if ($UpdateExisting) {
            Write-Host " - Updating existing: $slug"
            $updatePayload = @{
                title = $s.title
                summary = $s.summary
                categoryId = $targetCatId
                difficulty = $s.difficulty
                competency = $s.competency
                estimatedMinutes = [int]$s.estimatedMinutes
                content = $s.content
            }
            $updateRes = Invoke-NexoraApi -Method "Patch" -Path "/api/v1/admin/scenarios/$($existing.id)" -BodyObject $updatePayload
            if (-not $updateRes.Success) {
                Write-Host "   [FAILED] Failed to update '$slug'. HTTP $($updateRes.StatusCode): $($updateRes.Error)" -ForegroundColor Red
                $failedCount++
                continue
            }
            $updatedCount++

            # Ensure published
            if ($existing.status -ne "published") {
                $pubRes = Invoke-NexoraApi -Method "Post" -Path "/api/v1/admin/scenarios/$($existing.id)/publish"
                if ($pubRes.Success) {
                    Write-Host "   [PUBLISHED] Scenario '$slug' published." -ForegroundColor Green
                    $publishedCount++
                } else {
                    Write-Host "   [FAILED] Failed to publish '$slug'. HTTP $($pubRes.StatusCode): $($pubRes.Error)" -ForegroundColor Red
                    $failedCount++
                }
            }
        } else {
            Write-Host " - Skipping existing: $slug"
            $skippedCount++
        }
    } else {
        Write-Host " - Creating new: $slug"
        $createPayload = @{
            slug = $s.slug
            title = $s.title
            summary = $s.summary
            categoryId = $targetCatId
            difficulty = $s.difficulty
            competency = $s.competency
            estimatedMinutes = [int]$s.estimatedMinutes
            content = $s.content
        }
        $createRes = Invoke-NexoraApi -Method "Post" -Path "/api/v1/admin/scenarios" -BodyObject $createPayload
        if (-not $createRes.Success) {
            Write-Host "   [FAILED] Failed to create '$slug'. HTTP $($createRes.StatusCode): $($createRes.Error)" -ForegroundColor Red
            $failedCount++
            continue
        }
        $createdCount++
        $newId = $createRes.Data.id

        # Publish
        $pubRes = Invoke-NexoraApi -Method "Post" -Path "/api/v1/admin/scenarios/$newId/publish"
        if ($pubRes.Success) {
            Write-Host "   [PUBLISHED] Scenario '$slug' published." -ForegroundColor Green
            $publishedCount++
        } else {
            Write-Host "   [FAILED] Failed to publish '$slug'. HTTP $($pubRes.StatusCode): $($pubRes.Error)" -ForegroundColor Red
            $failedCount++
        }
    }
}

# Print summary
Write-Host "`n========================================="
Write-Host " Execution Summary"
Write-Host "========================================="
Write-Host "Created:   $createdCount"
Write-Host "Published: $publishedCount"
Write-Host "Updated:   $updatedCount"
Write-Host "Skipped:   $skippedCount"
Write-Host "Failed:    $failedCount"

# 11 & 12. Verification via public endpoint
Write-Host "`nVerifying public scenario library (GET /api/v1/scenarios, paged)..."
$publicSlugs = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
$publicTotal = 0
$publicPage = 1
do {
    $pubListRes = Invoke-NexoraApi -Method "Get" -Path "/api/v1/scenarios?page=$publicPage&pageSize=50"
    if (-not $pubListRes.Success) {
        Write-Host "Verification call failed on page $publicPage. HTTP $($pubListRes.StatusCode): $($pubListRes.Error)" -ForegroundColor Red
        Write-Host "`nRESULT: FAIL" -ForegroundColor Red
        exit 1
    }

    $publicTotal = [int]$pubListRes.Data.total
    if ($pubListRes.Data -and $pubListRes.Data.items) {
        foreach ($item in $pubListRes.Data.items) {
            [void]$publicSlugs.Add($item.slug)
        }
    }
    $publicPage++
} while ($publicSlugs.Count -lt $publicTotal -and $publicPage -le [math]::Ceiling($publicTotal / 50.0))

$missing = @()
foreach ($s in $scenarios) {
    if (-not $publicSlugs.Contains($s.slug)) {
        $missing += $s.slug
    }
}

if ($missing.Count -eq 0) {
    Write-Host "All $expectedTotal expected scenario slugs are present and published in public library." -ForegroundColor Green
    Write-Host "Total published in library: $publicTotal"
    Write-Host "`nRESULT: PASS" -ForegroundColor Green
} else {
    Write-Host "Missing $($missing.Count) expected slugs from public library:" -ForegroundColor Red
    foreach ($m in $missing) {
        Write-Host " - $m" -ForegroundColor Red
    }
    Write-Host "`nRESULT: FAIL" -ForegroundColor Red
    exit 1
}
