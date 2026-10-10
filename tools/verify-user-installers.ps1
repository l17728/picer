param(
    [Parameter(Mandatory)][ValidatePattern('^v\d+\.\d+\.\d+$')][string]$Version,
    [Parameter(Mandatory)][string]$AssetDirectory,
    [string]$TestRoot,
    [switch]$VerifyLiveService
)
$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($TestRoot)) { $TestRoot = Join-Path $PSScriptRoot '../artifacts/installer-validation' }
$root = Join-Path ([IO.Path]::GetFullPath($TestRoot)) ([Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root -Force | Out-Null
$results = @()
function Run-Setup([string]$Executable, [string[]]$Arguments) {
    $process = Start-Process -FilePath $Executable -ArgumentList $Arguments -WindowStyle Hidden -Wait -PassThru
    if ($process.ExitCode -ne 0) { throw "Installer returned $($process.ExitCode): $Executable" }
}
foreach ($product in @(
    @{ Name='Picer'; Id='Picer.Desktop'; Exe='Picer.App.exe'; Agent='media-agent' },
    @{ Name='Picer.MediaAgent'; Id='Picer.MediaAgent'; Exe='Picer.MediaAgent.exe'; Agent='' }
)) {
    $registryPath = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\' + $product.Id + '_is1'
    if (Test-Path -LiteralPath $registryPath) { throw 'Installer validation refuses to change an existing user installation.' }
    $installer = (Resolve-Path -LiteralPath (Join-Path $AssetDirectory "$($product.Name)-$Version-win-x64-setup.exe")).Path
    $destination = Join-Path $root $product.Name
    $externalData = Join-Path $root ($product.Name + '-user-data')
    New-Item -ItemType Directory -Path $externalData -Force | Out-Null
    $mediaSentinel = Join-Path $externalData 'original-photo-sentinel.txt'
    [IO.File]::WriteAllText($mediaSentinel, 'Original media must not change during installation or removal.')
    $originalHash = (Get-FileHash -LiteralPath $mediaSentinel -Algorithm SHA256).Hash
    $log = Join-Path $root ($product.Name + '-install.log')
    $arguments = @('/SP-', '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/TASKS=', '/NOICONS',
        ('/DIR="' + $destination + '"'), ('/GROUP="Picer installer validation ' + [Guid]::NewGuid().ToString('N') + '"'), ('/LOG="' + $log + '"'))
    $installed = $false
    try {
        Run-Setup $installer $arguments
        $installed = $true
        foreach ($file in @($product.Exe, 'coreclr.dll', 'hostfxr.dll', 'msvcp140.dll', 'msvcp140_1.dll', 'vcruntime140.dll', 'vcruntime140_1.dll', 'VC-REDIST-MANIFEST.json')) {
            if (-not (Test-Path -LiteralPath (Join-Path $destination $file) -PathType Leaf)) { throw "Installed prerequisite missing: $file" }
        }
        $agentRoot = if ($product.Agent) { Join-Path $destination $product.Agent } else { $destination }
        $ffmpeg = Join-Path $agentRoot 'tools/ffmpeg/ffmpeg.exe'
        $ffprobe = Join-Path $agentRoot 'tools/ffmpeg/ffprobe.exe'
        if (-not (Test-Path -LiteralPath $ffmpeg) -or -not (Test-Path -LiteralPath $ffprobe)) { throw 'Bundled video tools missing.' }
        $synthetic = Join-Path $root ($product.Name + '-synthetic.mp4')
        & $ffmpeg -hide_banner -loglevel error -f lavfi -i 'color=c=black:s=64x48:d=0.2' -c:v mpeg4 -y $synthetic
        if ($LASTEXITCODE -ne 0) { throw 'Bundled FFmpeg failed.' }
        $probe = & $ffprobe -v error -select_streams v:0 -show_entries stream=width,height -of json $synthetic | ConvertFrom-Json
        if ($LASTEXITCODE -ne 0 -or $probe.streams[0].width -ne 64 -or $probe.streams[0].height -ne 48) { throw 'Bundled FFprobe failed.' }
        if ($product.Name -eq 'Picer') {
            & (Join-Path $PSScriptRoot 'verify-p1-package.ps1') -PackageRoot $destination -TestRoot $root
        }
        if ($VerifyLiveService) {
            $probeOutput = & dotnet run --project (Join-Path $PSScriptRoot 'Picer.ServiceProbe/Picer.ServiceProbe.csproj') -c Release -- $destination
            if ($LASTEXITCODE -ne 0) { throw 'Installed payload could not verify the real service.' }
            $probeOutput | Set-Content -LiteralPath (Join-Path $root ($product.Name + '-live-service.json')) -Encoding utf8
        }
        $userFile = Join-Path $destination 'unowned-user-sentinel.txt'
        [IO.File]::WriteAllText($userFile, 'Installer must preserve unowned files.')
        Run-Setup $installer $arguments
        if (-not (Test-Path -LiteralPath $userFile)) { throw 'Upgrade removed an unowned file.' }
        $uninstaller = (Resolve-Path -LiteralPath (Join-Path $destination 'unins000.exe')).Path
        if (-not $uninstaller.StartsWith($root + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Uninstaller path escaped isolated validation root.' }
        Run-Setup $uninstaller @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART')
        $installed = $false
        if (Test-Path -LiteralPath (Join-Path $destination $product.Exe)) { throw 'Uninstall did not remove the program.' }
        if (-not (Test-Path -LiteralPath $userFile) -or
            (Get-FileHash -LiteralPath $mediaSentinel -Algorithm SHA256).Hash -ne $originalHash) { throw 'Uninstall changed user data.' }
        if (Test-Path -LiteralPath $registryPath) { throw 'Uninstall left its application registration.' }
        $results += @{ product=$product.Name; install=$true; repeatUpgrade=$true; uninstall=$true; dataPreserved=$true; ffmpeg=$true; ffprobe=$true }
    } finally {
        if ($installed -and (Test-Path -LiteralPath (Join-Path $destination 'unins000.exe'))) {
            $cleanup = (Resolve-Path -LiteralPath (Join-Path $destination 'unins000.exe')).Path
            if ($cleanup.StartsWith($root + '\', [StringComparison]::OrdinalIgnoreCase)) {
                Run-Setup $cleanup @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART')
            }
        }
    }
}
$results | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $root 'result.json') -Encoding utf8
$results | ConvertTo-Json -Depth 4
