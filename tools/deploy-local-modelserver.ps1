[CmdletBinding()]
param([string]$ModelRoot = 'D:\VL-models', [string]$SourceDirectory = '', [string]$TrainingArchiveRoot = '')
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$ModelRoot = [IO.Path]::GetFullPath($ModelRoot).TrimEnd('\')
$deploymentId = [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfff')
$source = if ($SourceDirectory) { [IO.Path]::GetFullPath($SourceDirectory) } else { Join-Path $repo ('artifacts\modelserver-publish\' + $deploymentId) }
$destination = Join-Path $ModelRoot 'services\Picer.ModelServer'
$launcher = Join-Path $ModelRoot 'start-picer-model-service.ps1'
$dll = Join-Path $destination 'Picer.ModelServer.dll'
$clipSource = Join-Path $repo 'third_party\runtime\models\chinese-clip'
$clipModelSource = Join-Path $clipSource 'model_quantized.onnx'
$clipVocabularySource = Join-Path $clipSource 'vocab.txt'
$backup = Join-Path $ModelRoot ('service-backups\modelserver-' + $deploymentId)
$launcherContent = [IO.File]::ReadAllText($launcher)
if (-not $TrainingArchiveRoot -and $launcherContent.Contains('PICER_TRAINING_ARCHIVE_ROOT')) { throw 'Pass TrainingArchiveRoot explicitly when the launcher configures an external training archive.' }
if (-not $TrainingArchiveRoot) { $TrainingArchiveRoot = if ($env:PICER_TRAINING_ARCHIVE_ROOT) { $env:PICER_TRAINING_ARCHIVE_ROOT } else { Join-Path $destination 'training-archive' } }
$TrainingArchiveRoot = [IO.Path]::GetFullPath($TrainingArchiveRoot).TrimEnd('\')
if ($TrainingArchiveRoot -in @($ModelRoot, $destination, [IO.Path]::GetPathRoot($TrainingArchiveRoot).TrimEnd('\'))) { throw 'TrainingArchiveRoot must be a dedicated state directory.' }
$archiveInsideApplication = $TrainingArchiveRoot.StartsWith($destination + '\', [StringComparison]::OrdinalIgnoreCase)
$archiveExisted = Test-Path -LiteralPath $TrainingArchiveRoot -PathType Container
if ($source.Equals($destination, [StringComparison]::OrdinalIgnoreCase) -or $source.StartsWith($destination + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'SourceDirectory must be staging, not the live installation.' }
if (-not $SourceDirectory) {
    & dotnet publish (Join-Path $repo 'server\Picer.ModelServer\Picer.ModelServer.csproj') -c Release -r win-x64 --self-contained false -o $source
    if ($LASTEXITCODE -ne 0) { throw 'ModelServer publish failed.' }
}
$files = @('Picer.ModelServer.dll', 'Picer.ModelServer.deps.json', 'Picer.ModelServer.runtimeconfig.json')
foreach ($file in $files) { if (-not (Test-Path -LiteralPath (Join-Path $source $file))) { throw "Missing build file: $file" } }
foreach ($asset in @(
    @{ Path=$clipModelSource; Hash='A2C2849329EAFB24100ACEC5A9421A39F8CB445296444C5C98855B69E5E7A751' },
    @{ Path=$clipVocabularySource; Hash='45BBAC6B341C319ADC98A532532882E91A9CEFC0329AA57BAC9AE761C27B291C' })) {
    if (-not (Test-Path -LiteralPath $asset.Path -PathType Leaf)) { throw "Missing pinned Chinese-CLIP asset: $($asset.Path)" }
    if ((Get-FileHash -LiteralPath $asset.Path -Algorithm SHA256).Hash -ne $asset.Hash) { throw "Chinese-CLIP asset hash mismatch: $($asset.Path)" }
}
New-Item -ItemType Directory -Path $backup | Out-Null
$backupReady = $false
$stopped = $false
$changed = $false
function Stop-VerifiedModelServer {
    $managerListener = @(Get-NetTCPConnection -LocalPort 8090 -State Listen -ErrorAction SilentlyContinue | Select-Object -ExpandProperty OwningProcess -Unique)
    if ($managerListener.Count -gt 0) {
        foreach ($managerId in $managerListener) {
            $managerProcess = Get-CimInstance Win32_Process -Filter "ProcessId=$managerId"
            $managerEntry = Join-Path $ModelRoot 'manager\server.js'
            if (-not $managerProcess.CommandLine -or $managerProcess.CommandLine.IndexOf($managerEntry, [StringComparison]::OrdinalIgnoreCase) -lt 0) {
                throw 'Port 8090 belongs to an unverified supervisor; refusing to stop the inference process.'
            }
        }
        $stopResult = Invoke-RestMethod -Method Post -Uri 'http://127.0.0.1:8090/api/services/picer-model/stop' -TimeoutSec 15
        if (-not $stopResult.ok) { throw 'ModelServer supervisor did not confirm an orderly stop.' }
        $released = $false
        for ($attempt = 0; $attempt -lt 40; $attempt++) {
            if (-not (Get-NetTCPConnection -LocalPort 8091 -State Listen -ErrorAction SilentlyContinue)) { $released = $true; break }
            Start-Sleep -Milliseconds 250
        }
        if (-not $released) { throw 'ModelServer supervisor stopped the request but port 8091 remains occupied.' }
    }
    $listener = Get-NetTCPConnection -LocalPort 8091 -State Listen -ErrorAction SilentlyContinue
    $owned = @(Get-CimInstance Win32_Process | Where-Object { $_.Name -in @('dotnet.exe', 'Picer.ModelServer.exe') -and $_.CommandLine -and $_.CommandLine.IndexOf($dll, [StringComparison]::OrdinalIgnoreCase) -ge 0 })
    foreach ($serviceId in @(@($listener.OwningProcess) + @($owned.ProcessId) | Select-Object -Unique)) {
        if (-not $serviceId) { continue }
        $service = Get-CimInstance Win32_Process -Filter "ProcessId=$serviceId"
        if (-not $service.CommandLine -or $service.CommandLine.IndexOf($dll, [StringComparison]::OrdinalIgnoreCase) -lt 0) {
            throw 'Port 8091 belongs to an unexpected process; refusing to stop it.'
        }
        function Stop-Children([uint32]$ParentId) {
            foreach ($child in @(Get-CimInstance Win32_Process -Filter "ParentProcessId=$ParentId")) {
                Stop-Children $child.ProcessId
                $childProcess = Get-Process -Id $child.ProcessId -ErrorAction SilentlyContinue
                Stop-Process -Id $child.ProcessId -Force -ErrorAction SilentlyContinue
                if ($childProcess -and -not $childProcess.WaitForExit(15000)) { throw 'ModelServer worker did not exit.' }
            }
        }
        Stop-Children $serviceId
        $running = Get-Process -Id $serviceId
        Stop-Process -Id $serviceId -Force
        if (-not $running.WaitForExit(15000)) { throw 'ModelServer did not exit' }
        Start-Sleep -Milliseconds 500
    }
}
try {
    Stop-VerifiedModelServer
    $stopped = $true
    Copy-Item -LiteralPath $destination -Destination (Join-Path $backup 'application') -Recurse
    Copy-Item -LiteralPath $launcher -Destination $backup
    if ($archiveExisted -and -not $archiveInsideApplication) { Copy-Item -LiteralPath $TrainingArchiveRoot -Destination (Join-Path $backup 'training-archive') -Recurse }
    $backupReady = $true
    $changed = $true
    Get-ChildItem -LiteralPath $source -Force | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $destination -Recurse -Force }
    $workerDestination = Join-Path $destination 'worker'
    New-Item -ItemType Directory -Path $workerDestination -Force | Out-Null
    Get-ChildItem -LiteralPath (Join-Path $repo 'server\Picer.ModelServer\worker') -File | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $workerDestination -Force }
    $clipDestination = Join-Path $destination 'models\chinese-clip'
    New-Item -ItemType Directory -Path $clipDestination -Force | Out-Null
    Copy-Item -LiteralPath $clipModelSource -Destination $clipDestination -Force
    Copy-Item -LiteralPath $clipVocabularySource -Destination $clipDestination -Force
    $workerSetting = '$env:PICER_GPU_WORKER_SCRIPT = ' + "'" + (Join-Path $workerDestination 'face_worker.py').Replace("'", "''") + "'"
    $content = [regex]::Replace($launcherContent, '(?m)^\s*\$env:PICER_GPU_WORKER_SCRIPT\s*=.*$', '')
    $content = $workerSetting + [Environment]::NewLine + $content
    $content = [regex]::Replace($content, '(?m)^\s*\$env:PICER_CLIP_MODEL_ROOT\s*=.*$', '')
    $clipSetting = '$env:PICER_CLIP_MODEL_ROOT = ' + "'" + $destination.Replace("'", "''") + "'"
    $content = $clipSetting + [Environment]::NewLine + $content
    $setting = '$env:PICER_GPU_WORKER_USE_CUDA = ''1'''
    if ($content -notmatch 'PICER_GPU_WORKER_USE_CUDA') { $content = $setting + [Environment]::NewLine + $content }
    [IO.File]::WriteAllText($launcher, $content, [Text.UTF8Encoding]::new($true))
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $launcher
    if ($LASTEXITCODE -ne 0) { throw 'ModelServer launcher failed' }
    $healthy = $false
    for ($attempt = 0; $attempt -lt 30; $attempt++) {
        try { $health = Invoke-RestMethod 'http://127.0.0.1:8091/health' -TimeoutSec 2; if ($health.status -eq 'ok') { $healthy = $true; break } } catch { }
        Start-Sleep -Seconds 1
    }
    if (-not $healthy) { throw 'ModelServer health timeout' }
    if ((Get-FileHash -LiteralPath $dll -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath (Join-Path $source 'Picer.ModelServer.dll') -Algorithm SHA256).Hash) { throw 'Installed ModelServer hash mismatch.' }
    Write-Output "ModelServer updated; backup: $backup"
} catch {
    if ($changed -and $backupReady) {
        Stop-VerifiedModelServer
        $failed = [IO.Path]::GetFullPath($destination + '.failed-' + $deploymentId)
        if (-not $failed.StartsWith($ModelRoot + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Unexpected application rollback target.' }
        Move-Item -LiteralPath $destination -Destination $failed
        Copy-Item -LiteralPath (Join-Path $backup 'application') -Destination $destination -Recurse
        if (-not $archiveInsideApplication) {
            $failedArchive = [IO.Path]::GetFullPath($TrainingArchiveRoot + '.failed-' + $deploymentId)
            if ([IO.Path]::GetDirectoryName($failedArchive) -ne [IO.Path]::GetDirectoryName($TrainingArchiveRoot)) { throw 'Unexpected archive rollback target.' }
            if (Test-Path -LiteralPath $TrainingArchiveRoot) { Move-Item -LiteralPath $TrainingArchiveRoot -Destination $failedArchive }
            if ($archiveExisted) { Copy-Item -LiteralPath (Join-Path $backup 'training-archive') -Destination $TrainingArchiveRoot -Recurse }
        }
        Copy-Item -LiteralPath (Join-Path $backup 'start-picer-model-service.ps1') -Destination $launcher -Force
    }
    if ($stopped) { & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $launcher }
    throw
}
