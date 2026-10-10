param(
    [Parameter(Mandatory)][string]$Destination,
    [string]$RedistDirectory
)
$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($RedistDirectory)) {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
    if (-not (Test-Path -LiteralPath $vswhere)) { throw 'Build machine needs the Visual C++ redistributable files from Visual Studio Build Tools.' }
    $installation = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($installation)) { throw 'Visual C++ Build Tools were not found.' }
    $versions = Get-ChildItem -LiteralPath (Join-Path $installation 'VC/Redist/MSVC') -Directory |
        Where-Object Name -Match '^\d+\.\d+\.\d+$' | Sort-Object { [version]$_.Name } -Descending
    $RedistDirectory = $versions | ForEach-Object { Join-Path $_.FullName 'x64/Microsoft.VC143.CRT' } |
        Where-Object { Test-Path -LiteralPath $_ -PathType Container } | Select-Object -First 1
}
if ([string]::IsNullOrWhiteSpace($RedistDirectory)) { throw 'Microsoft VC143 x64 redistributable directory is missing.' }
$source = (Resolve-Path -LiteralPath $RedistDirectory).Path
$required = @('msvcp140.dll', 'msvcp140_1.dll', 'vcruntime140.dll', 'vcruntime140_1.dll')
foreach ($name in $required) {
    if (-not (Test-Path -LiteralPath (Join-Path $source $name) -PathType Leaf)) { throw "Incomplete native redistributable: $name" }
}
New-Item -ItemType Directory -Path $Destination -Force | Out-Null
$files = foreach ($file in Get-ChildItem -LiteralPath $source -File -Filter '*.dll') {
    $signature = Get-AuthenticodeSignature -LiteralPath $file.FullName
    if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'Microsoft Corporation') {
        throw "Native prerequisite is not a verified Microsoft redistributable: $($file.Name)"
    }
    Copy-Item -LiteralPath $file.FullName -Destination $Destination -Force
    $sha = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    if ((Get-FileHash -LiteralPath (Join-Path $Destination $file.Name) -Algorithm SHA256).Hash.ToLowerInvariant() -ne $sha) { throw 'Native prerequisite copy mismatch.' }
    @{ name=$file.Name; version=$file.VersionInfo.FileVersion; sha256=$sha; bytes=$file.Length }
}
@{
    distribution='Microsoft Visual C++ VC143 x64 app-local runtime (from Visual Studio redistributable directory)'
    source='https://learn.microsoft.com/cpp/windows/redistributing-visual-cpp-files'
    servicing='These app-local runtime DLLs are updated by Picer program upgrades.'
    files=@($files)
} | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $Destination 'VC-REDIST-MANIFEST.json') -Encoding utf8
