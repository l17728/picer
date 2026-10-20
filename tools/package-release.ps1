param(
    [Parameter(Mandatory)][ValidatePattern('^v\d+\.\d+\.\d+([-.][0-9A-Za-z.-]+)?$')][string]$Version,
    [Parameter(Mandatory)][string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$output = [IO.Path]::GetFullPath($OutputDirectory)
if ((Test-Path -LiteralPath $output) -and @(Get-ChildItem -LiteralPath $output -Force).Count) {
    throw 'Release output must be empty; never package over a live installation.'
}
New-Item -ItemType Directory -Path $output -Force | Out-Null
$stage = Join-Path $output 'staging'
New-Item -ItemType Directory -Path $stage | Out-Null
$installerDependencies = & (Join-Path $PSScriptRoot 'prepare-installer-dependencies.ps1')
$packages = @(
    @{ Name='Picer'; Project='src/Picer.App/Picer.App.csproj'; Rid='win-x64'; Executable='Picer.App.exe' },
    @{ Name='Picer.MediaAgent'; Project='src/Picer.MediaAgent/Picer.MediaAgent.csproj'; Rid='win-x64'; Executable='Picer.MediaAgent.exe' },
    @{ Name='Picer.IndexService'; Project='server/Picer.IndexService/Picer.IndexService.csproj'; Rid='win-x64'; Executable='Picer.IndexService.exe' },
    @{ Name='Picer.IndexService'; Project='server/Picer.IndexService/Picer.IndexService.csproj'; Rid='linux-x64'; Executable='Picer.IndexService' },
    @{ Name='Picer.ModelServer'; Project='server/Picer.ModelServer/Picer.ModelServer.csproj'; Rid='win-x64'; Executable='Picer.ModelServer.exe' },
    @{ Name='Picer.ModelServer'; Project='server/Picer.ModelServer/Picer.ModelServer.csproj'; Rid='linux-x64'; Executable='Picer.ModelServer' }
)
function Publish-Binary($project, $rid, $destination) {
    dotnet publish (Join-Path $repoRoot $project) -c Release -r $rid --self-contained true -o $destination --warnaserror -p:IncludeRuntimeAssets=false
    if ($LASTEXITCODE -ne 0) { throw "Publish failed: $project ($rid)" }
    if ($rid -eq 'win-x64') {
        & (Join-Path $PSScriptRoot 'copy-native-prerequisites.ps1') -Destination $destination
    }
    $licenses = Join-Path $destination 'licenses'
    New-Item -ItemType Directory -Path $licenses -Force | Out-Null
    $runtimeLicenses = Join-Path $repoRoot 'third_party/runtime/licenses'
    if (-not (Test-Path -LiteralPath $runtimeLicenses)) { throw 'Checked-in third-party licenses are missing.' }
    Get-ChildItem -LiteralPath $runtimeLicenses -File | Copy-Item -Destination $licenses
    $assetsPath = Join-Path (Split-Path -Parent (Join-Path $repoRoot $project)) 'obj/project.assets.json'
    $assets = Get-Content -LiteralPath $assetsPath -Raw | ConvertFrom-Json
    foreach ($library in $assets.libraries.PSObject.Properties) {
        if ($library.Value.type -ne 'package') { continue }
        foreach ($folder in $assets.packageFolders.PSObject.Properties.Name) {
            $packageRoot = Join-Path $folder $library.Value.path
            if (-not (Test-Path -LiteralPath $packageRoot)) { continue }
            $licenseDestination = Join-Path $licenses ($library.Name.Replace('/', '-'))
            New-Item -ItemType Directory -Path $licenseDestination -Force | Out-Null
            Get-ChildItem -LiteralPath $packageRoot -Recurse -File | Where-Object {
                $_.Name -match '(?i)license|notice|copying|\.nuspec$'
            } | ForEach-Object {
                $relative = $_.FullName.Substring($packageRoot.TrimEnd('\', '/').Length + 1)
                $target = Join-Path $licenseDestination $relative
                New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
                Copy-Item -LiteralPath $_.FullName -Destination $target
            }
            break
        }
    }
    # This distribution deliberately separates model/tool assets, not native libraries.
    # The full offline publisher and its fixed-hash gates remain independent.
    $runtime = [IO.Path]::GetFullPath((Join-Path $destination 'runtime'))
    if (-not $runtime.StartsWith($stage + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Runtime removal escaped new staging directory.'
    }
    if (Test-Path -LiteralPath $runtime) {
        $removed = $false
        for ($attempt = 1; $attempt -le 10; $attempt++) {
            try {
                Remove-Item -LiteralPath $runtime -Recurse -Force -ErrorAction Stop
                $removed = $true
                break
            } catch [System.UnauthorizedAccessException] {
                if ($attempt -eq 10) { throw }
                # dotnet publish or malware scanning may briefly retain a native
                # tool handle after publication. Wait without weakening the
                # checked staging-directory boundary above.
                Start-Sleep -Seconds 1
            } catch [System.IO.IOException] {
                if ($attempt -eq 10) { throw }
                Start-Sleep -Seconds 1
            }
        }
        if (-not $removed) { throw "Unable to remove external runtime overlay: $runtime" }
    }
}
foreach ($package in $packages) {
    $name = "$($package.Name)-$Version-$($package.Rid)-binary"
    $destination = Join-Path $stage $name
    Publish-Binary $package.Project $package.Rid $destination
    if (-not (Test-Path -LiteralPath (Join-Path $destination $package.Executable))) { throw "Missing executable: $name" }
    if ($package.Name -eq 'Picer') {
        Publish-Binary 'src/Picer.MediaAgent/Picer.MediaAgent.csproj' 'win-x64' (Join-Path $destination 'media-agent')
    }
    if ($package.Name -in @('Picer', 'Picer.MediaAgent')) {
        $agentDirectory = if ($package.Name -eq 'Picer') { Join-Path $destination 'media-agent' } else { $destination }
        $toolDirectory = Join-Path $agentDirectory 'tools/ffmpeg'
        New-Item -ItemType Directory -Path $toolDirectory -Force | Out-Null
        Get-ChildItem -LiteralPath $installerDependencies.MediaTools -File | Copy-Item -Destination $toolDirectory -Force
    }
    if ($package.Name -eq 'Picer.ModelServer') {
        Copy-Item -LiteralPath (Join-Path $repoRoot 'server/Picer.ModelServer/worker') -Destination $destination -Recurse -Force
        foreach ($setupFile in @('setup-modelserver.py', 'modelserver-requirements-cpu.txt', 'modelserver-requirements-cuda.txt')) {
            Copy-Item -LiteralPath (Join-Path $repoRoot "server/$setupFile") -Destination $destination
        }
        Copy-Item -LiteralPath (Join-Path $repoRoot 'server/README.md') -Destination (Join-Path $destination 'SERVER-README.md')
    }
    Copy-Item -LiteralPath (Join-Path $repoRoot 'docs/RELEASE_GUIDE.md') -Destination (Join-Path $destination 'INSTALL.md')
    foreach ($license in @(Get-ChildItem -LiteralPath $repoRoot -File | Where-Object { $_.Name -like 'LICENSE*' -or $_.Name -like 'NOTICE*' })) {
        Copy-Item -LiteralPath $license.FullName -Destination $destination
    }
    if ($package.Rid -eq 'linux-x64') {
        # Windows cross-publish cannot retain Unix executable bits; INSTALL.md covers chmod.
        $archive = Join-Path $output "$name.tar.gz"
        tar -czf $archive -C $destination .
        if ($LASTEXITCODE -ne 0) { throw "Archive failed: $name" }
    } else {
        $archive = Join-Path $output "$name.zip"
        Compress-Archive -Path (Join-Path $destination '*') -DestinationPath $archive -CompressionLevel Optimal
    }
    if ((Get-Item -LiteralPath $archive).Length -ge 2000000000) { throw "Asset exceeds 2 GB limit: $name" }
}
& (Join-Path $PSScriptRoot 'build-installers.ps1') -Version $Version `
    -GuiDirectory (Join-Path $stage "Picer-$Version-win-x64-binary") `
    -AgentDirectory (Join-Path $stage "Picer.MediaAgent-$Version-win-x64-binary") `
    -OutputDirectory $output -CompilerPath $installerDependencies.Compiler
Copy-Item -LiteralPath (Join-Path $repoRoot 'docs/RELEASE_GUIDE.md') -Destination (Join-Path $output 'INSTALL.md')
Get-ChildItem -LiteralPath $output -File | Sort-Object Name | ForEach-Object {
    "$((Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant())  $($_.Name)"
} | Set-Content -LiteralPath (Join-Path $output 'SHA256SUMS.txt') -Encoding ascii
Write-Output "All component packages and self-contained user installers ready: $output. Model services are configured separately."
