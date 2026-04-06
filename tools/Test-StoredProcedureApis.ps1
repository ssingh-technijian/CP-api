param(
    [string]$ApiRoot = "D:\CP-Apis",
    [string]$RepoRoot = "D:\Technijian",
    [Parameter(Mandatory = $true)]
    [string]$ClientPortalConnectionString,
    [string]$DatabaseAlias = "client-portal",
    [string]$BaseUrl = "http://127.0.0.1:5195",
    [int]$SqlCommandTimeoutSeconds = 15,
    [int]$RequestTimeoutSeconds = 20,
    [string]$OutputDirectory = "",
    [string]$PathListPath = "",
    [bool]$RefreshManifest = $true
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $ApiRoot "artifacts\test-runs"
}

$null = New-Item -ItemType Directory -Path $OutputDirectory -Force

$timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
$jsonReportPath = Join-Path $OutputDirectory "stored-procedure-api-test-$timestamp.json"
$mdReportPath = Join-Path $OutputDirectory "stored-procedure-api-test-$timestamp.md"
$apiExePath = Join-Path $ApiRoot "src\CpApis.Api\bin\Debug\net8.0\CpApis.Api.exe"
$inventoryProjectPath = Join-Path $ApiRoot "src\CpApis.InventoryGenerator\CpApis.InventoryGenerator.csproj"
$healthUrl = "$BaseUrl/api/system/health"
$swaggerUrl = "$BaseUrl/swagger/v1/swagger.json"

if ((Test-Path $apiExePath) -eq $false) {
    throw "API executable not found at '$apiExePath'. Build the API first."
}

if ($RefreshManifest) {
    if ((Test-Path $inventoryProjectPath) -eq $false) {
        throw "Inventory generator project not found at '$inventoryProjectPath'."
    }

    Write-Host "Refreshing manifest from test database..."
    $inventoryOutputDirectory = Join-Path $ApiRoot "artifacts"
    $inventoryArgs = @(
        "run",
        "--no-build",
        "--project", $inventoryProjectPath,
        "--",
        "--repo-root", $RepoRoot,
        "--output-dir", $inventoryOutputDirectory,
        "--client-portal-connection-string", $ClientPortalConnectionString
    )

    & dotnet @inventoryArgs
    if ($LASTEXITCODE -ne 0) {
        throw "Inventory generator failed with exit code $LASTEXITCODE."
    }
}

function Convert-ExampleToJsonBody {
    param([object]$Example)

    if ($null -eq $Example) {
        return (@{ Parameters = @{} } | ConvertTo-Json -Depth 32 -Compress)
    }

    if ($Example -is [string]) {
        return $Example
    }

    return ($Example | ConvertTo-Json -Depth 32 -Compress)
}

function Get-ResponseText {
    param([object]$Response)

    if ($null -eq $Response) {
        return ""
    }

    if ($Response.PSObject.Properties.Name -contains "Content" -and $Response.Content -is [byte[]]) {
        return [System.Text.Encoding]::UTF8.GetString($Response.Content)
    }

    if ($Response.PSObject.Properties.Name -contains "Content") {
        return [string]$Response.Content
    }

    return [string]$Response
}

$process = $null
$results = New-Object System.Collections.Generic.List[object]

try {
    $startInfo = New-Object System.Diagnostics.ProcessStartInfo
    $startInfo.FileName = $apiExePath
    $startInfo.WorkingDirectory = Split-Path $apiExePath -Parent
    $startInfo.UseShellExecute = $false
    $startInfo.Environment["ASPNETCORE_URLS"] = $BaseUrl
    $startInfo.Environment["ConnectionStrings__client-portal"] = $ClientPortalConnectionString
    $startInfo.Environment["RuntimeCatalog__CommandTimeoutSeconds"] = [string]$SqlCommandTimeoutSeconds

    $process = [System.Diagnostics.Process]::Start($startInfo)
    if ($null -eq $process) {
        throw "Failed to start API process."
    }

    $deadline = (Get-Date).AddSeconds(45)
    $health = $null
    do {
        Start-Sleep -Milliseconds 500
        try {
            $health = Invoke-RestMethod -Uri $healthUrl -TimeoutSec 5 -ErrorAction Stop
        }
        catch {
        }
    } while ($null -eq $health -and (Get-Date) -lt $deadline)

    if ($null -eq $health) {
        throw "API did not become healthy at '$healthUrl'."
    }

    $swagger = Invoke-WebRequest -Uri $swaggerUrl -TimeoutSec 20 | Select-Object -ExpandProperty Content | ConvertFrom-Json -AsHashtable -Depth 100
    $paths = $swagger.paths.Keys | Where-Object { $_ -like "/api/modules/*/stored-procedures/$DatabaseAlias/*/execute" } | Sort-Object
    if ([string]::IsNullOrWhiteSpace($PathListPath) -eq $false) {
        $allowedPaths = Get-Content -Path $PathListPath | Where-Object { [string]::IsNullOrWhiteSpace($_) -eq $false } | ForEach-Object { $_.Trim() }
        $paths = $paths | Where-Object { $_ -in $allowedPaths }
    }

    if (@($paths).Count -eq 0) {
        throw "No stored procedure execute endpoints found for database alias '$DatabaseAlias'."
    }

    $index = 0
    foreach ($path in $paths) {
        $index++
        $operation = $swagger.paths[$path].post
        $example = $null
        if ($operation.requestBody -and $operation.requestBody.content -and $operation.requestBody.content."application/json") {
            $example = $operation.requestBody.content."application/json".example
        }

        $body = Convert-ExampleToJsonBody -Example $example
        $uri = "$BaseUrl$path"

        try {
            $response = Invoke-WebRequest -Uri $uri -Method Post -ContentType "application/json" -Body $body -TimeoutSec $RequestTimeoutSeconds -SkipHttpErrorCheck
            $responseText = Get-ResponseText -Response $response
            $isSuccess = [int]$response.StatusCode -ge 200 -and [int]$response.StatusCode -lt 300

            $results.Add([pscustomobject]@{
                    Index = $index
                    Path = $path
                    StatusCode = [int]$response.StatusCode
                    Success = $isSuccess
                    RequestBody = $body
                    ResponsePreview = if ($responseText.Length -gt 1000) { $responseText.Substring(0, 1000) } else { $responseText }
                })
        }
        catch {
            $results.Add([pscustomobject]@{
                    Index = $index
                    Path = $path
                    StatusCode = 0
                    Success = $false
                    RequestBody = $body
                    ResponsePreview = $_.Exception.Message
                })
        }

        Write-Host ("[{0}/{1}] {2} -> {3}" -f $index, @($paths).Count, $path, $results[$results.Count - 1].StatusCode)
    }

    $summary = [pscustomobject]@{
        GeneratedAt = (Get-Date).ToString("o")
        BaseUrl = $BaseUrl
        DatabaseAlias = $DatabaseAlias
        SqlCommandTimeoutSeconds = $SqlCommandTimeoutSeconds
        RequestTimeoutSeconds = $RequestTimeoutSeconds
        Total = $results.Count
        Passed = @($results | Where-Object Success).Count
        Failed = @($results | Where-Object { -not $_.Success }).Count
        Results = $results
    }

    $summary | ConvertTo-Json -Depth 100 | Set-Content -Path $jsonReportPath -Encoding UTF8

    $markdown = @(
        "# Stored Procedure API Test Report"
        ""
        "- GeneratedAt: $($summary.GeneratedAt)"
        "- BaseUrl: $($summary.BaseUrl)"
        "- DatabaseAlias: $($summary.DatabaseAlias)"
        "- Total: $($summary.Total)"
        "- Passed: $($summary.Passed)"
        "- Failed: $($summary.Failed)"
        ""
        "## Failures"
        ""
    )

    $failures = @($results | Where-Object { -not $_.Success })
    if ($failures.Count -eq 0) {
        $markdown += "- None"
    }
    else {
        foreach ($failure in $failures) {
            $markdown += "- [$($failure.StatusCode)] $($failure.Path)"
            $markdown += "  Request: $($failure.RequestBody)"
            $markdown += "  Response: $($failure.ResponsePreview -replace "`r?`n", ' ')"
        }
    }

    Set-Content -Path $mdReportPath -Value $markdown -Encoding UTF8

    [pscustomobject]@{
        JsonReportPath = $jsonReportPath
        MarkdownReportPath = $mdReportPath
        Total = $summary.Total
        Passed = $summary.Passed
        Failed = $summary.Failed
    } | ConvertTo-Json -Depth 10
}
finally {
    if ($null -ne $process -and $process.HasExited -eq $false) {
        Stop-Process -Id $process.Id -Force
    }
}
