[CmdletBinding()]
param(
    [string]$ModelServerBaseUrl = 'http://127.0.0.1:8091',
    [string]$IndexServiceBaseUrl = 'https://127.0.0.1:8445',
    [string]$IndexServiceToken = $env:PICER_INDEX_SERVICE_TOKEN,
    [string]$OutputPath = 'artifacts/platform-service-check.json',
    [switch]$AllowIndexUnavailable
)

$ErrorActionPreference = 'Stop'
[void][System.Reflection.Assembly]::LoadWithPartialName('System.Net.Http')
$results = [ordered]@{ generatedUtc = [DateTime]::UtcNow; checks = @() }

function Add-Check([string]$Name, [string]$Url, [bool]$Passed, [int]$StatusCode, [string]$Detail) {
    $script:results.checks += [ordered]@{
        name = $Name; url = $Url; passed = $Passed; statusCode = $StatusCode; detail = $Detail
    }
}

function Test-Endpoint([string]$Name, [string]$Url, [hashtable]$Headers = @{}, [bool]$Optional = $false) {
    try {
        $handler = [System.Net.Http.HttpClientHandler]::new()
        $handler.ServerCertificateCustomValidationCallback = { $true }
        $client = [System.Net.Http.HttpClient]::new($handler)
        $client.Timeout = [TimeSpan]::FromSeconds(15)
        foreach ($key in $Headers.Keys) { $client.DefaultRequestHeaders.TryAddWithoutValidation($key, [string]$Headers[$key]) | Out-Null }
        $response = $client.GetAsync($Url).GetAwaiter().GetResult()
        $body = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        $ok = $response.IsSuccessStatusCode
        Add-Check $Name $Url $ok ([int]$response.StatusCode) $body.Substring(0, [Math]::Min(200, $body.Length))
        $client.Dispose(); $handler.Dispose()
        if (-not $ok -and -not ($Optional -and $AllowIndexUnavailable)) { throw "${Name} returned HTTP $($response.StatusCode)" }
    } catch {
        Add-Check $Name $Url ($Optional -and $AllowIndexUnavailable) 0 $_.Exception.Message
        if (-not ($Optional -and $AllowIndexUnavailable)) { throw }
    }
}

Test-Endpoint 'model-server-health' ($ModelServerBaseUrl.TrimEnd('/') + '/health')
$headers = @{}
if (-not [string]::IsNullOrWhiteSpace($IndexServiceToken)) { $headers.Authorization = "Bearer $IndexServiceToken" }
Test-Endpoint 'index-service-health' ($IndexServiceBaseUrl.TrimEnd('/') + '/health') $headers $true

$directory = Split-Path -Parent $OutputPath
if (-not [string]::IsNullOrWhiteSpace($directory)) { New-Item -ItemType Directory -Force -Path $directory | Out-Null }
$results | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $OutputPath -Encoding UTF8
$failed = @($results.checks | Where-Object { -not $_.passed })
if ($failed.Count -gt 0) { Write-Warning "平台服务检查存在 $($failed.Count) 个失败/未配置项；报告：$OutputPath"; exit 2 }
Write-Host "平台服务检查通过；报告：$OutputPath"
