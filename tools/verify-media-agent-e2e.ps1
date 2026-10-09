[CmdletBinding()]
param(
    [string]$IndexService = "https://127.0.0.1:8445",
    [string]$TokenPath = "D:\PicerData\secrets\model-server-api-key.txt",
    [string]$FaceSample = "D:\PicerData\app-data\face-thumbs\1.v1.jpg"
)

$ErrorActionPreference = "Stop"
[System.Net.ServicePointManager]::ServerCertificateValidationCallback = { $true }
[System.Net.ServicePointManager]::SecurityProtocol = [System.Net.SecurityProtocolType]::Tls12
$token = (Get-Content -LiteralPath $TokenPath -Raw).Trim()
$headers = @{ Authorization = "Bearer $token" }
$jsonHeaders = @{ Authorization = "Bearer $token"; "Content-Type" = "application/json" }
$temp = Join-Path ([IO.Path]::GetTempPath()) ("picer-media-agent-e2e-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $temp | Out-Null

try {
    $capabilities = (curl.exe -k -sS --fail-with-body "$IndexService/api/service/v1/capabilities" -H "Authorization: Bearer $token" | ConvertFrom-Json)
    $expected = @("ffmpeg.probe", "ffmpeg.keyframes", "ffmpeg.audio_extract", "asr.transcribe", "vl.analyze", "face.detect", "face.embed", "tts.synthesize")
    foreach ($id in $expected) {
        $capability = $capabilities.capabilities | Where-Object id -eq $id
        if ($null -eq $capability -or $capability.state -ne "ready") { throw "Capability not ready: $id" }
    }

    $faceBody = @{ path = $FaceSample; modelVersion = "sface" } | ConvertTo-Json
    $faceJson = Join-Path $temp "face.json"
    [IO.File]::WriteAllText($faceJson, $faceBody, [Text.UTF8Encoding]::new($false))
    $face = (curl.exe -k -sS --fail-with-body --max-time 90 -X POST "$IndexService/api/service/v1/face/detect" -H "Authorization: Bearer $token" -H "Content-Type: application/json" --data-binary "@$faceJson" | ConvertFrom-Json)
    if ($face.faces.Count -lt 1) { throw "Face detect returned no faces" }

    $ttsBody = @{ text = "MediaAgent 流式验收" } | ConvertTo-Json
    $ttsJson = Join-Path $temp "tts.json"
    $ttsWav = Join-Path $temp "tts.wav"
    [IO.File]::WriteAllText($ttsJson, $ttsBody, [Text.UTF8Encoding]::new($false))
    & curl.exe -k -sS --fail-with-body --max-time 90 -X POST "$IndexService/api/service/v1/tts/stream" -H "Authorization: Bearer $token" -H "Content-Type: application/json" --data-binary "@$ttsJson" -o $ttsWav
    $wav = [IO.File]::ReadAllBytes($ttsWav)
    if ($wav.Length -lt 44 -or [Text.Encoding]::ASCII.GetString($wav, 0, 4) -ne "RIFF") { throw "TTS stream is not a WAV response" }

    $vlBody = @{ model = "Qwen/Qwen3-VL-8B-Instruct-GGUF:Q4_K_M"; messages = @(@{ role = "user"; content = "Reply exactly: stream ready" }); max_tokens = 16 } | ConvertTo-Json -Depth 8
    $vlJson = Join-Path $temp "vl.json"
    $vlSse = Join-Path $temp "vl.sse"
    [IO.File]::WriteAllText($vlJson, $vlBody, [Text.UTF8Encoding]::new($false))
    & curl.exe -k -sS --fail-with-body --max-time 120 -X POST "$IndexService/api/service/v1/vl/stream" -H "Authorization: Bearer $token" -H "Content-Type: application/json" --data-binary "@$vlJson" -o $vlSse
    $sse = Get-Content -LiteralPath $vlSse -Raw
    if ($sse -notmatch "data:" -or $sse -notmatch "stream" -or $sse -notmatch "ready" -or $sse -notmatch "\[DONE\]") { throw "VL stream did not return SSE chunks" }

    [pscustomobject]@{
        Capabilities = $expected.Count
        FaceCount = $face.faces.Count
        TtsBytes = $wav.Length
        VlSseBytes = (Get-Item $vlSse).Length
        Status = "passed"
    } | ConvertTo-Json
}
finally {
    Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue
}
