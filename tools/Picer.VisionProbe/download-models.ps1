param([string]$Destination = $(if ($env:PICER_MODEL_ROOT) { $env:PICER_MODEL_ROOT } else { 'D:\PicerData\model-probe' }))

$ErrorActionPreference = 'Stop'
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
