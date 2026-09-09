param([string]$Destination = '')

$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($Destination)) {
    if ($env:PICER_MODEL_ROOT) {
        $Destination = $env:PICER_MODEL_ROOT
    } else {
        $repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
        $configured = $null
        $configPath = Join-Path $repoRoot 'picer.paths.json'
        if (Test-Path -LiteralPath $configPath) {
            $config = Get-Content -LiteralPath $configPath -Raw -Encoding UTF8 | ConvertFrom-Json
            if ($config.modelProbeRoot) { $configured = $config.modelProbeRoot }
        }
        if (-not $configured) { $configured = 'third_party/model-probe' }
        $Destination = if ([IO.Path]::IsPathRooted($configured)) { $configured } else { Join-Path $repoRoot $configured }
    }
}
New-Item -ItemType Directory -Force -Path $Destination | Out-Null
$models = @(
    @{ Name='face_detection_yunet_2023mar.onnx'; Url='https://raw.githubusercontent.com/opencv/opencv_zoo/main/models/face_detection_yunet/face_detection_yunet_2023mar.onnx' },
    @{ Name='face_recognition_sface_2021dec.onnx'; Url='https://raw.githubusercontent.com/opencv/opencv_zoo/main/models/face_recognition_sface/face_recognition_sface_2021dec.onnx' }
)
foreach ($model in $models) {
    $target = Join-Path $Destination $model.Name
    if (-not (Test-Path -LiteralPath $target) -or (Get-Item -LiteralPath $target).Length -eq 0) {
        Invoke-WebRequest -Uri $model.Url -OutFile $target -UseBasicParsing
    }
    $hash = Get-FileHash -LiteralPath $target -Algorithm SHA256
    [pscustomobject]@{ Name=$model.Name; Path=$target; Bytes=(Get-Item -LiteralPath $target).Length; SHA256=$hash.Hash }
}
