param([switch]$PublishSelfContained)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
Push-Location $repoRoot
try {
    dotnet restore picer.sln
    if ($LASTEXITCODE -ne 0) { throw 'Restore failed.' }
    dotnet build picer.sln --no-restore -warnaserror
    if ($LASTEXITCODE -ne 0) { throw 'Build failed or emitted warnings.' }
    dotnet test picer.sln --no-build --no-restore -m:1
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
    if ($PublishSelfContained) {
        & (Join-Path $PSScriptRoot 'publish-self-contained.ps1') -SkipArchive
        if ($LASTEXITCODE -ne 0) { throw 'Self-contained publish verification failed.' }
    }
}
finally {
    Pop-Location
}
