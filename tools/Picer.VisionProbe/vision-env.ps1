$probeRoot = 'D:\PicerVisionProbe'
$dependencyRoot = 'D:\PicerDependencies'

$directories = @(
    $probeRoot,
    "$probeRoot\pip-cache",
    "$probeRoot\hf-cache",
    "$probeRoot\temp",
    "$dependencyRoot\nuget"
)

foreach ($directory in $directories) {
    New-Item -ItemType Directory -Force -Path $directory | Out-Null
}

$env:PIP_CACHE_DIR = "$probeRoot\pip-cache"
$env:HF_HOME = "$probeRoot\hf-cache"
$env:HUGGINGFACE_HUB_CACHE = "$probeRoot\hf-cache\transformers"
$env:TEMP = "$probeRoot\temp"
$env:TMP = "$probeRoot\temp"
$env:NUGET_PACKAGES = "$dependencyRoot\nuget"
$env:PICER_MODEL_ROOT = 'D:\PicerData\model-probe'

Write-Host 'Picer vision dependencies and caches are redirected to D:.'
