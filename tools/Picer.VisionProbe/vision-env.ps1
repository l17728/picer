$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)

function Get-RepoPath {
    param([string]$Key, [string]$Default)
    $configured = $null
    $configPath = Join-Path $repoRoot 'picer.paths.json'
    if (Test-Path -LiteralPath $configPath) {
        $config = Get-Content -LiteralPath $configPath -Raw -Encoding UTF8 | ConvertFrom-Json
        if ($config.$Key) { $configured = $config.$Key }
    }
    if (-not $configured) { $configured = $Default }
    if ([IO.Path]::IsPathRooted($configured)) { return $configured }
    return Join-Path $repoRoot $configured
}

$probeRoot = Get-RepoPath -Key 'visionProbeRoot' -Default 'artifacts/vision-probe'
$nugetRoot = Get-RepoPath -Key 'nugetPackages' -Default 'artifacts/nuget'
$modelRoot = if ($env:PICER_MODEL_ROOT) { $env:PICER_MODEL_ROOT } else { Get-RepoPath -Key 'modelProbeRoot' -Default 'third_party/model-probe' }

$directories = @(
    $probeRoot,
    "$probeRoot\pip-cache",
    "$probeRoot\hf-cache",
    "$probeRoot\temp",
    $nugetRoot
)

foreach ($directory in $directories) {
    New-Item -ItemType Directory -Force -Path $directory | Out-Null
}

$env:PIP_CACHE_DIR = "$probeRoot\pip-cache"
$env:HF_HOME = "$probeRoot\hf-cache"
$env:HUGGINGFACE_HUB_CACHE = "$probeRoot\hf-cache\transformers"
$env:TEMP = "$probeRoot\temp"
$env:TMP = "$probeRoot\temp"
$env:NUGET_PACKAGES = $nugetRoot
$env:PICER_MODEL_ROOT = $modelRoot

Write-Host "Picer vision dependencies and caches are redirected to $repoRoot (see picer.paths.json)."
