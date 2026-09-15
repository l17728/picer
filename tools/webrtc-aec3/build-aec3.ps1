param(
    [string]$WebRtcRoot = "D:\webrtc\src",
    [string]$Configuration = "Release",
    [string]$RuntimeDirectory = ""
)

$ErrorActionPreference = "Stop"
$sourceRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Split-Path -Parent (Split-Path -Parent $sourceRoot)
$RuntimeDirectory = if ([string]::IsNullOrWhiteSpace($RuntimeDirectory)) {
    Join-Path $repoRoot "third_party\runtime"
} else {
    [IO.Path]::GetFullPath($RuntimeDirectory)
}
$targetRoot = Join-Path $WebRtcRoot "picer_aec3"
$outputRoot = Join-Path $WebRtcRoot "out\picer_aec3"
$rootBuild = Join-Path $WebRtcRoot "BUILD.gn"

New-Item -ItemType Directory -Force -Path $targetRoot | Out-Null
Copy-Item -LiteralPath (Join-Path $sourceRoot "picer_aec3.h") -Destination $targetRoot -Force
Copy-Item -LiteralPath (Join-Path $sourceRoot "picer_aec3.cc") -Destination $targetRoot -Force
Copy-Item -LiteralPath (Join-Path $sourceRoot "BUILD.gn") -Destination $targetRoot -Force

$env:DEPOT_TOOLS_WIN_TOOLCHAIN = "0"
$env:vs2022_install = "C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools"
$env:PATH = "D:\webrtc\depot_tools;$WebRtcRoot\third_party\ninja;$env:PATH"
$isDebug = if ($Configuration -eq "Debug") { "true" } else { "false" }
$gnArgs = "is_debug=$isDebug " + 'target_cpu=\"x64\" rtc_include_tests=false rtc_build_examples=false rtc_enable_protobuf=false is_component_build=false use_remoteexec=false treat_warnings_as_errors=false'
$rootBuildContent = [IO.File]::ReadAllText($rootBuild)
$defaultDependency = 'deps = [ ":webrtc" ]'
if (!$rootBuildContent.Contains($defaultDependency)) {
    throw "Unexpected WebRTC root BUILD.gn; default dependency was not found"
}
$patchedRootBuild = $rootBuildContent.Replace($defaultDependency, 'deps = [ ":webrtc", "//picer_aec3" ]')

Push-Location $WebRtcRoot
try {
    [IO.File]::WriteAllText($rootBuild, $patchedRootBuild, [Text.UTF8Encoding]::new($false))
    & (Join-Path $WebRtcRoot "buildtools\win\gn.exe") gen $outputRoot "--args=$gnArgs"
    if ($LASTEXITCODE -ne 0) { throw "GN generation failed: $LASTEXITCODE" }
    & (Join-Path $WebRtcRoot "third_party\ninja\ninja.exe") -C $outputRoot picer_aec3.dll
    if ($LASTEXITCODE -ne 0) { throw "AEC3 build failed: $LASTEXITCODE" }
}
finally {
    [IO.File]::WriteAllText($rootBuild, $rootBuildContent, [Text.UTF8Encoding]::new($false))
    Pop-Location
}

$dll = Join-Path $outputRoot "picer_aec3.dll"
if (!(Test-Path -LiteralPath $dll)) { throw "AEC3 DLL was not produced: $dll" }
$runtimeAec = Join-Path $RuntimeDirectory "webrtc"
$runtimeLicenses = Join-Path $RuntimeDirectory "licenses"
New-Item -ItemType Directory -Force -Path $runtimeAec, $runtimeLicenses | Out-Null
Copy-Item -LiteralPath $dll -Destination (Join-Path $runtimeAec "picer_aec3.dll") -Force
Copy-Item -LiteralPath (Join-Path $WebRtcRoot "LICENSE") -Destination (Join-Path $runtimeLicenses "WebRTC-BSD.txt") -Force
$hash = (Get-FileHash -LiteralPath $dll -Algorithm SHA256).Hash
Write-Output "$dll SHA256=$hash"
