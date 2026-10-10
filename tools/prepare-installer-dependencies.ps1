param(
    [string]$CacheDirectory,
    [switch]$SkipCompiler
)
$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($CacheDirectory)) { $CacheDirectory = Join-Path $PSScriptRoot '../artifacts/installer-tools' }
$cache = [IO.Path]::GetFullPath($CacheDirectory)
New-Item -ItemType Directory -Path $cache -Force | Out-Null
$manifest = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'installer/dependencies.json') -Raw | ConvertFrom-Json
function Get-PinnedFile($dependency) {
    $path = Join-Path $cache $dependency.fileName
    if ((Test-Path -LiteralPath $path) -and (Get-Item -LiteralPath $path).Length -eq $dependency.bytes -and
        (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -eq $dependency.sha256) { return $path }
    $partial = $path + '.partial-' + [Guid]::NewGuid().ToString('N')
    try {
        $downloaded = $false
        for ($attempt = 0; $attempt -lt 3; $attempt++) {
            try {
                Invoke-WebRequest -Uri $dependency.url -OutFile $partial -UseBasicParsing
                $downloaded = $true
                break
            } catch { if ($attempt -eq 2) { throw }; Start-Sleep -Seconds 2 }
        }
        if (-not $downloaded -or (Get-Item -LiteralPath $partial).Length -ne $dependency.bytes -or
            (Get-FileHash -LiteralPath $partial -Algorithm SHA256).Hash -ne $dependency.sha256) {
            throw "Pinned build dependency failed integrity verification: $($dependency.fileName)"
        }
        Move-Item -LiteralPath $partial -Destination $path -Force
    } finally { if (Test-Path -LiteralPath $partial) { Remove-Item -LiteralPath $partial -Force } }
    return $path
}
$archive = Get-PinnedFile $manifest.ffmpeg
$mediaTools = Join-Path $cache ('ffmpeg-' + $manifest.ffmpeg.version)
New-Item -ItemType Directory -Path $mediaTools -Force | Out-Null
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [IO.Compression.ZipFile]::OpenRead($archive)
try {
    foreach ($mapping in @(
        @{Source='bin/ffmpeg.exe'; Target='ffmpeg.exe'},
        @{Source='bin/ffprobe.exe'; Target='ffprobe.exe'},
        @{Source='LICENSE'; Target='LICENSE.txt'},
        @{Source='README.txt'; Target='UPSTREAM-README.txt'}
    )) {
        $name = $manifest.ffmpeg.archiveRoot + '/' + $mapping.Source
        $entry = $zip.GetEntry($name)
        if ($null -eq $entry -or $entry.Length -le 0) { throw "Missing dependency archive member: $name" }
        [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, (Join-Path $mediaTools $mapping.Target), $true)
    }
} finally { $zip.Dispose() }
$manifest.ffmpeg | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $mediaTools 'DISTRIBUTION-SOURCE.json') -Encoding utf8
$compiler = $null
if (-not $SkipCompiler) {
    $innoInstaller = Get-PinnedFile $manifest.inno
    $innoRoot = Join-Path $cache 'inno'
    $compiler = Join-Path $innoRoot 'ISCC.exe'
    if (-not (Test-Path -LiteralPath $compiler) -or
        (Get-FileHash -LiteralPath $compiler -Algorithm SHA256).Hash -ne $manifest.inno.compilerSha256) {
        $arguments = @('/SP-', '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/NOICONS', '/CURRENTUSER', ('/DIR="' + $innoRoot + '"'))
        $process = Start-Process -FilePath $innoInstaller -ArgumentList $arguments -WindowStyle Hidden -Wait -PassThru
        if ($process.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $compiler) -or
            (Get-FileHash -LiteralPath $compiler -Algorithm SHA256).Hash -ne $manifest.inno.compilerSha256) { throw 'Pinned installer compiler setup failed.' }
    }
}
[pscustomobject]@{ Compiler=$compiler; MediaTools=$mediaTools; Manifest=(Join-Path $PSScriptRoot 'installer/dependencies.json') }
