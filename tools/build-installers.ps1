param(
    [Parameter(Mandatory)][ValidatePattern('^v\d+\.\d+\.\d+$')][string]$Version,
    [Parameter(Mandatory)][string]$GuiDirectory,
    [Parameter(Mandatory)][string]$AgentDirectory,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [string]$CompilerPath
)
$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($CompilerPath)) {
    $dependencies = & (Join-Path $PSScriptRoot 'prepare-installer-dependencies.ps1')
    $CompilerPath = $dependencies.Compiler
}
if (-not (Test-Path -LiteralPath $CompilerPath -PathType Leaf)) { throw 'Installer compiler is missing.' }
$dependencyManifest = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'installer/dependencies.json') -Raw | ConvertFrom-Json
if ((Get-FileHash -LiteralPath $CompilerPath -Algorithm SHA256).Hash -ne $dependencyManifest.inno.compilerSha256) { throw 'Installer compiler must match the pinned Inno Setup 6.7.3 SHA-256.' }
$output = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $output -Force | Out-Null
$script = Join-Path $PSScriptRoot 'installer/picer.iss'
foreach ($product in @(
    @{Kind='gui'; Directory=$GuiDirectory; Executable='Picer.App.exe'; AgentSubdirectory='media-agent'},
    @{Kind='agent'; Directory=$AgentDirectory; Executable='Picer.MediaAgent.exe'; AgentSubdirectory=''}
)) {
    $payload = (Resolve-Path -LiteralPath $product.Directory).Path
    if ($output.Equals($payload, [StringComparison]::OrdinalIgnoreCase) -or
        $output.StartsWith($payload.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Installer output cannot be inside its payload.' }
    foreach ($file in @($product.Executable,'coreclr.dll','hostfxr.dll','msvcp140.dll','msvcp140_1.dll','vcruntime140.dll','vcruntime140_1.dll')) {
        if (-not (Test-Path -LiteralPath (Join-Path $payload $file) -PathType Leaf)) { throw "Installer payload is not self-contained: $file" }
    }
    $agentRoot = if ($product.AgentSubdirectory) { Join-Path $payload $product.AgentSubdirectory } else { $payload }
    foreach ($file in @('Picer.MediaAgent.exe','coreclr.dll','msvcp140.dll','vcruntime140.dll','tools/ffmpeg/ffmpeg.exe','tools/ffmpeg/ffprobe.exe','tools/ffmpeg/LICENSE.txt')) {
        if (-not (Test-Path -LiteralPath (Join-Path $agentRoot $file) -PathType Leaf)) { throw "Agent installation dependency is missing: $file" }
    }
    & $CompilerPath "/DProductKind=$($product.Kind)" "/DProductVersion=$($Version.Substring(1))" "/DPayload=$payload" "/DInstallerOutput=$output" $script
    if ($LASTEXITCODE -ne 0) { throw "Installer compilation failed: $($product.Kind)" }
}
Write-Output "One-click user installers ready: $output"
