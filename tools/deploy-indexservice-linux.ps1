[CmdletBinding()]
param(
    [ValidatePattern('^[A-Za-z0-9.-]+$')]
    [string]$Server = '124.156.193.122',
    [ValidatePattern('^[A-Za-z0-9._-]+$')]
    [string]$User = 'root',
    [string]$IdentityFile,
    [string]$Version = '',
    [ValidatePattern('^[A-Za-z0-9_.@-]+\.service$')]
    [string[]]$AdditionalWriterServices = @(),
    [string]$OutputRoot = (Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts\indexservice-deploy'),
    [switch]$PackageOnly
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($Version -and $Version -notmatch '^v?\d+\.\d+\.\d+([-.][0-9A-Za-z.-]+)?$') { throw 'Invalid deployment version.' }

function Invoke-Native {
    param([string]$File, [string[]]$Arguments)
    & $File @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$([IO.Path]::GetFileName($File)) failed with exit code $LASTEXITCODE" }
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$ssh = (Get-Command ssh.exe -ErrorAction Stop).Source
$scp = (Get-Command scp.exe -ErrorAction Stop).Source
$dotnet = (Get-Command dotnet.exe -ErrorAction Stop).Source
$deploymentId = [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ', [Globalization.CultureInfo]::InvariantCulture)
$publishDir = Join-Path $OutputRoot "$deploymentId\publish"
$packagePath = Join-Path $OutputRoot "picer-index-$deploymentId.zip"
$remoteScript = Join-Path $PSScriptRoot 'deploy-indexservice-linux.sh'
$remoteArchive = "/tmp/picer-index-$deploymentId.zip"
$remoteParts = "/tmp/picer-index-$deploymentId.parts"
$remoteScriptPath = "/tmp/picer-index-$deploymentId.sh"

if (-not (Test-Path -LiteralPath $remoteScript -PathType Leaf)) { throw "Remote deployment script missing: $remoteScript" }
if ($IdentityFile -and -not (Test-Path -LiteralPath $IdentityFile -PathType Leaf)) { throw "SSH identity file not found: $IdentityFile" }
New-Item -ItemType Directory -Path $publishDir -Force | Out-Null

Write-Host "Publishing IndexService for linux-x64 ($deploymentId)..."
$publishArgs = @(
    'publish', (Join-Path $repoRoot 'server\Picer.IndexService\Picer.IndexService.csproj'),
    '-c', 'Release', '-r', 'linux-x64', '--self-contained', 'true',
    '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true',
    '-p:IncludeRuntimeAssets=false',
    '-o', $publishDir
)
if ($Version) { $publishArgs += '-p:Version=' + $Version.TrimStart('v') }
Invoke-Native $dotnet $publishArgs

$executable = Join-Path $publishDir 'Picer.IndexService'
if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) { throw 'Published Linux executable was not produced.' }
$executableHash = (Get-FileHash -LiteralPath $executable -Algorithm SHA256).Hash.ToLowerInvariant()
$commit = 'unknown'
try {
    $candidateCommit = & git -C $repoRoot rev-parse --short=12 HEAD 2>$null
    if ($LASTEXITCODE -eq 0 -and $candidateCommit) { $commit = ([string]$candidateCommit).Trim() }
} catch { }
$manifest = [ordered]@{
    deploymentId = $deploymentId
    binarySha256 = $executableHash
    sourceCommit = ([string]$commit).Trim()
    version = $Version
    builtUtc = [DateTime]::UtcNow.ToString('O', [Globalization.CultureInfo]::InvariantCulture)
}
$manifestPath = Join-Path $publishDir 'deployment.json'
$manifestJson = $manifest | ConvertTo-Json -Depth 4
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
[System.IO.File]::WriteAllText($manifestPath, $manifestJson, $utf8NoBom)

if (Test-Path -LiteralPath $packagePath) { Remove-Item -LiteralPath $packagePath -Force }
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [System.IO.Compression.ZipFile]::Open($packagePath, [System.IO.Compression.ZipArchiveMode]::Create)
try {
    [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $executable, 'Picer.IndexService', [System.IO.Compression.CompressionLevel]::Optimal)
    [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $manifestPath, 'deployment.json', [System.IO.Compression.CompressionLevel]::Optimal)
    [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, (Join-Path $PSScriptRoot 'indexservice-data-snapshot.py'), 'indexservice-data-snapshot.py', [System.IO.Compression.CompressionLevel]::Optimal)
    [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, (Join-Path $publishDir 'licenses\Magick.NET-Notice.txt'), 'licenses/Magick.NET-Notice.txt', [System.IO.Compression.CompressionLevel]::Optimal)
} finally { $zip.Dispose() }
$archiveHash = (Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash.ToLowerInvariant()
Write-Host "Package ready: $packagePath"
Write-Host "binarySha256=$executableHash archiveSha256=$archiveHash"
if ($PackageOnly) { return }

$sshArgs = @('-o', 'BatchMode=yes', '-o', 'StrictHostKeyChecking=yes')
if ($IdentityFile) { $sshArgs += @('-i', (Resolve-Path -LiteralPath $IdentityFile).Path) }
$target = "$User@$Server"
$writerUnits = $AdditionalWriterServices -join ' '
$remoteCommand = "PICER_DEPLOY_WRITER_UNITS='$writerUnits' bash $remoteScriptPath $remoteArchive $executableHash $deploymentId"

Write-Host "Uploading package and rollback-aware installer to $target..."
$scpArgs = @('-o', 'BatchMode=yes', '-o', 'StrictHostKeyChecking=yes')
if ($IdentityFile) { $scpArgs += @('-i', (Resolve-Path -LiteralPath $IdentityFile).Path) }
Invoke-Native $ssh ($sshArgs + @($target, "mkdir -m 700 '$remoteParts'"))
$chunkRoot = Join-Path $OutputRoot "$deploymentId\chunks"
New-Item -ItemType Directory -Path $chunkRoot -Force | Out-Null
$chunkSize = 4MB
$buffer = New-Object byte[] $chunkSize
$inputStream = [System.IO.File]::OpenRead($packagePath)
try {
    $chunkIndex = 0
    while (($read = $inputStream.Read($buffer, 0, $buffer.Length)) -gt 0) {
        $partPath = Join-Path $chunkRoot ('part-{0:D4}' -f $chunkIndex)
        $partStream = [System.IO.File]::Open($partPath, [System.IO.FileMode]::CreateNew, [System.IO.FileAccess]::Write, [System.IO.FileShare]::None)
        try { $partStream.Write($buffer, 0, $read) } finally { $partStream.Dispose() }
        Invoke-Native $scp ($scpArgs + @($partPath, "${target}:$remoteParts/$(Split-Path -Leaf $partPath)"))
        $chunkIndex++
        Write-Host "Uploaded package chunk $chunkIndex"
    }
} finally { $inputStream.Dispose() }
$assemble = "set -eu; cat '$remoteParts'/part-* > '$remoteArchive'; printf '%s  %s\n' '$archiveHash' '$remoteArchive' | sha256sum --check -; rm -f '$remoteParts'/part-*; rmdir '$remoteParts'"
Invoke-Native $ssh ($sshArgs + @($target, "bash -c '$assemble'"))
Invoke-Native $scp ($scpArgs + @($remoteScript, "${target}:$remoteScriptPath"))

Write-Host 'Running remote atomic upgrade and waiting for health/version verification...'
Invoke-Native $ssh ($sshArgs + @($target, $remoteCommand))
Write-Host "Upgrade verified. deploymentId=$deploymentId sha256=$executableHash"
Write-Host "Remote status endpoint: https://${Server}:8445/version"
