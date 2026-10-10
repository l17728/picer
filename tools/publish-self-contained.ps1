param(
    [string]$OutputDirectory = '',
    [string]$ServiceOutputDirectory = '',
    [switch]$SkipArchive
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($OutputDirectory) -or [string]::IsNullOrWhiteSpace($ServiceOutputDirectory)) {
    $config = $null
    $configPath = Join-Path $repoRoot 'picer.paths.json'
    if (Test-Path -LiteralPath $configPath) {
        $config = Get-Content -LiteralPath $configPath -Raw -Encoding UTF8 | ConvertFrom-Json
    }
    if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
        $configured = if ($config -and $config.releaseOutput) { $config.releaseOutput } else { 'artifacts/release/Picer-win-x64' }
        $OutputDirectory = if ([IO.Path]::IsPathRooted($configured)) { $configured } else { Join-Path $repoRoot $configured }
    }
    if ([string]::IsNullOrWhiteSpace($ServiceOutputDirectory)) {
        $configured = if ($config -and $config.serviceReleaseOutput) { $config.serviceReleaseOutput } else { 'artifacts/release/Picer.IndexService-win-x64' }
        $ServiceOutputDirectory = if ([IO.Path]::IsPathRooted($configured)) { $configured } else { Join-Path $repoRoot $configured }
    }
}
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory).TrimEnd('\')
$ServiceOutputDirectory = [IO.Path]::GetFullPath($ServiceOutputDirectory).TrimEnd('\')
if ($OutputDirectory.Equals($ServiceOutputDirectory, [StringComparison]::OrdinalIgnoreCase) -or
    $OutputDirectory.StartsWith($ServiceOutputDirectory + '\', [StringComparison]::OrdinalIgnoreCase) -or
    $ServiceOutputDirectory.StartsWith($OutputDirectory + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Application and service staging directories must be separate.' }
foreach ($stagingDirectory in @($OutputDirectory, $ServiceOutputDirectory)) {
    if ((Test-Path -LiteralPath $stagingDirectory) -and @(Get-ChildItem -LiteralPath $stagingDirectory -Force).Count -ne 0) {
        throw 'Publish requires empty staging directories. Use fresh OutputDirectory and ServiceOutputDirectory; do not publish over a live installation.'
    }
}
$runtimeSource = Join-Path $repoRoot 'third_party\runtime'
$required = @(
    @{ Path='models\face\face_detection_yunet_2023mar.onnx'; Bytes=232589; Hash='8F2383E4DD3CFBB4553EA8718107FC0423210DC964F9F4280604804ED2552FA4' },
    @{ Path='models\face\face_recognition_sface_2021dec.onnx'; Bytes=38696353; Hash='0BA9FBFA01B5270C96627C4EF784DA859931E02F04419C829E83484087C34E79' },
    @{ Path='ffmpeg\ffmpeg.exe'; Bytes=102856192; Hash='72A489ECCD008C2EC2C0A5856C5C75BC3D8BBFA90166C4566865C246445E6AA3' },
    @{ Path='whisper\whisper-cli.exe'; Bytes=479232; Hash='800A0FD754AFA75E109C7248286AD735670FB6B23D92CA5D12604647EF638A65' },
    @{ Path='whisper\whisper.dll'; Bytes=1368064; Hash='0A29E5824C7495185B833AD07DF7AB9CADF130A9BE848F967C6B88AECA971566' },
    @{ Path='webrtc\picer_aec3.dll'; Bytes=1196544; Hash='AC405193CB90F963177B883FDC1533660128B3C15CCA00986FBAFF0A215BC6DD' },
    @{ Path='models\whisper\ggml-base-q5_1.bin'; Bytes=59707625; Hash='422F1AE452ADE6F30A004D7E5C6A43195E4433BC370BF23FAC9CC591F01A8898' },
    @{ Path='models\chinese-clip\model_quantized.onnx'; Bytes=190842404; Hash='A2C2849329EAFB24100ACEC5A9421A39F8CB445296444C5C98855B69E5E7A751' },
    @{ Path='models\chinese-clip\vocab.txt'; Bytes=109540; Hash='45BBAC6B341C319ADC98A532532882E91A9CEFC0329AA57BAC9AE761C27B291C' },
    @{ Path='models\florence-2-base-ft\vision_encoder_quantized.onnx'; Bytes=93746540; Hash='3B79D54F23F666F731549DB23CB070C35A979CE19CBD9720E90E67A78DC9768C' },
    @{ Path='models\florence-2-base-ft\encoder_model_quantized.onnx'; Bytes=43651493; Hash='F4AD7A68F1FB875D3BCF735EA14A7021B7BA7E83BAF7CF10289881B4ED6D9B85' },
    @{ Path='models\florence-2-base-ft\embed_tokens_quantized.onnx'; Bytes=39390433; Hash='6B2258DB1C8EE9B160576CCDE3CD3814D83A2EDAED0DD1C6CA9FF3C38FA62214' },
    @{ Path='models\florence-2-base-ft\decoder_model_merged_quantized.onnx'; Bytes=98177697; Hash='F22F52F980C33DF0EFA15932C2F3DB6D9D3595CE6387ECA938B8CFE23DC4C641' },
    @{ Path='models\florence-2-base-ft\decoder_model_quantized.onnx'; Bytes=97760937; Hash='C529B26BAFCE2EE76F886F3A0E374BB646B07A6D8B7640FD8A50D7A48843DD67' },
    @{ Path='models\florence-2-base-ft\config.json'; Bytes=5432; Hash='D90C22ED72EB55291F183FCD9B98EBD3BD3D92BFCFFB6C7F6E1606085E793525' },
    @{ Path='models\florence-2-base-ft\preprocessor_config.json'; Bytes=2673; Hash='C892857E34A7082284983A7717717D39C9BF7E574F1F41D80D4C918C97502EFA' },
    @{ Path='models\florence-2-base-ft\tokenizer.json'; Bytes=2297961; Hash='D69DCDB2323E124AC4F800CB9863DDCCEA0D7BB11E16125E8DF3BD60F2F8AEAC' },
    @{ Path='models\florence-2-base-ft\vocab.json'; Bytes=1099884; Hash='394FDC63C71AABE0A9B97117F5D62FB5FCC4D59B2B3EA929A3929E6A53217B3C' },
    @{ Path='models\florence-2-base-ft\merges.txt'; Bytes=456318; Hash='1CE1664773C50F3E0CC8842619A93EDC4624525B728B188A9E0BE33B7726ADC5' },
    @{ Path='models\florence-2-base-ft\generation_config.json'; Bytes=292; Hash='7B8EB17BBD6CF8A07F619AD83AE03881EFF05B6B9237BAB89005B40E77783C29' },
    @{ Path='models\florence-2-base-ft\tokenizer_config.json'; Bytes=197658; Hash='D8E64607233CB53B619FB46664F6CAD08176C26E0E8735B2D30D888364F19600' },
    @{ Path='models\florence-2-base-ft\special_tokens_map.json'; Bytes=146627; Hash='72FF172DC769BC1551B1B4211628CE3271643BC60379E4DA45D85A9BE9332C39' },
    @{ Path='models\florence-2-base-ft\added_tokens.json'; Bytes=22410; Hash='BA67457C577895525996E7A42D7DA60A05856506EA4FD25FF9A023A64662F74B' }
)

function Assert-Runtime([string]$root) {
    foreach ($item in $required) {
        $path = Join-Path $root $item.Path
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Required offline component is missing: $path" }
        $file = Get-Item -LiteralPath $path
        if ($file.Length -ne $item.Bytes) { throw "Offline component size mismatch: $path" }
        $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
        if ($hash -ne $item.Hash) { throw "Offline component SHA-256 mismatch: $path" }
    }
    $licenses = @('YuNet-MIT.txt','SFace-Apache-2.0.txt','whisper.cpp-MIT.txt','FFmpeg-GPLv3.txt','Chinese-CLIP-MIT.txt','Florence-2-MIT.txt','DirectML-MIT.txt','DirectML-ThirdPartyNotices.txt','ONNXRuntime-MIT.txt','ONNXRuntime-ThirdPartyNotices.txt','WebRTC-BSD.txt','THIRD-PARTY-NOTICES.txt')
    foreach ($license in $licenses) {
        $path = Join-Path $root "licenses\$license"
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Required license is missing: $path" }
    }
}

Assert-Runtime $runtimeSource
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
dotnet publish (Join-Path $repoRoot 'src\Picer.App\Picer.App.csproj') -c Release -r win-x64 --self-contained true -o $OutputDirectory
if ($LASTEXITCODE -ne 0) { throw 'Self-contained publish failed.' }
& (Join-Path $PSScriptRoot 'copy-native-prerequisites.ps1') -Destination $OutputDirectory
$agentOutput = Join-Path $OutputDirectory 'media-agent'
New-Item -ItemType Directory -Force -Path $agentOutput | Out-Null
dotnet publish (Join-Path $repoRoot 'src\Picer.MediaAgent\Picer.MediaAgent.csproj') -c Release -r win-x64 --self-contained true -o $agentOutput
if ($LASTEXITCODE -ne 0) { throw 'Self-contained media agent publish failed.' }
& (Join-Path $PSScriptRoot 'copy-native-prerequisites.ps1') -Destination $agentOutput
$agentDependencies = & (Join-Path $PSScriptRoot 'prepare-installer-dependencies.ps1') -SkipCompiler
$agentToolDirectory = Join-Path $agentOutput 'tools/ffmpeg'
New-Item -ItemType Directory -Path $agentToolDirectory -Force | Out-Null
Get-ChildItem -LiteralPath $agentDependencies.MediaTools -File | Copy-Item -Destination $agentToolDirectory -Force
foreach ($file in @('Picer.MediaAgent.exe','Picer.MediaAgent.dll','Picer.MediaAgent.runtimeconfig.json','Picer.MediaAgent.deps.json','hostfxr.dll','coreclr.dll')) {
    if (-not (Test-Path -LiteralPath (Join-Path $agentOutput $file) -PathType Leaf)) { throw "Published media agent component is missing: $file" }
}
Assert-Runtime (Join-Path $OutputDirectory 'runtime')
if (-not (Test-Path -LiteralPath (Join-Path $OutputDirectory 'Picer.App.exe'))) { throw 'Published application executable is missing.' }
$native = @(
    @{ Path='Magick.Native-Q8-x64.dll'; Bytes=24742576; Hash='7A85C85083D25418A659EDE27ED6C7E104CAE4FEDE6611C2BE4B91294B6207F2' },
    @{ Path='licenses\Magick.NET-Notice.txt'; Bytes=428101; Hash='38037A04466C03F8698907DC1B79E6E6D7813C9CF021439B4547CF99469DF751' },
    @{ Path='DirectML.dll'; Bytes=18527776; Hash='9C9E6D822561C6C41B90E6994B3E8857CF1D66DBFB1E0C4C799C7C89B4E92DA1' },
    @{ Path='onnxruntime.dll'; Bytes=16468024; Hash='F722DD91A311B9FA4B7F2341D22C379A72FB7BC5B639DF18B64821F23CDDE14B' }
)
foreach ($item in $native) {
    $path = Join-Path $OutputDirectory $item.Path
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Required native runtime is missing: $path" }
    if ((Get-Item -LiteralPath $path).Length -ne $item.Bytes) { throw "Native runtime size mismatch: $path" }
    if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $item.Hash) { throw "Native runtime SHA-256 mismatch: $path" }
}

New-Item -ItemType Directory -Force -Path $ServiceOutputDirectory | Out-Null
dotnet publish (Join-Path $repoRoot 'server\Picer.IndexService\Picer.IndexService.csproj') -c Release -r win-x64 --self-contained true -o $ServiceOutputDirectory
if ($LASTEXITCODE -ne 0) { throw 'Index service publish failed.' }
& (Join-Path $PSScriptRoot 'copy-native-prerequisites.ps1') -Destination $ServiceOutputDirectory
Assert-Runtime (Join-Path $ServiceOutputDirectory 'runtime')
if (-not (Test-Path -LiteralPath (Join-Path $ServiceOutputDirectory 'Picer.IndexService.exe'))) { throw 'Published index service executable is missing.' }

if (-not $SkipArchive) {
    $archive = "$OutputDirectory.zip"
    if (Test-Path -LiteralPath $archive) { Remove-Item -LiteralPath $archive -Force }
    Compress-Archive -Path (Join-Path $OutputDirectory '*') -DestinationPath $archive -CompressionLevel Optimal
    Write-Output "Self-contained release: $archive"
} else {
    Write-Output "Self-contained release: $OutputDirectory"
}
$serviceArchive = "$ServiceOutputDirectory.zip"
if (-not $SkipArchive) {
    if (Test-Path -LiteralPath $serviceArchive) { Remove-Item -LiteralPath $serviceArchive -Force }
    Compress-Archive -Path (Join-Path $ServiceOutputDirectory '*') -DestinationPath $serviceArchive -CompressionLevel Optimal
    Write-Output "Index service release: $serviceArchive"
} else {
    Write-Output "Index service release: $ServiceOutputDirectory"
}
