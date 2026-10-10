param([string]$PackageRoot = 'D:\picer\artifacts\p1-release-20261008\Picer-win-x64', [string]$TestRoot)
$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($TestRoot)) { $TestRoot = Join-Path $PSScriptRoot '../artifacts/p1-tests' }
$PackageRoot = (Resolve-Path -LiteralPath $PackageRoot).Path.TrimEnd('\', '/')
$smokeRoot = Join-Path ([IO.Path]::GetFullPath($TestRoot)) ('package-smoke-' + [Guid]::NewGuid().ToString('N'))
$bootstrap = Join-Path $smokeRoot 'bootstrap'
New-Item -ItemType Directory -Path $bootstrap -Force | Out-Null
Set-Content -LiteralPath (Join-Path $bootstrap 'backend.json') -Value '{"mode":"local"}' -Encoding UTF8
$launch = New-Object System.Diagnostics.ProcessStartInfo
$launch.FileName = Join-Path $PackageRoot 'Picer.App.exe'
$launch.WorkingDirectory = $PackageRoot
$launch.UseShellExecute = $false
$launch.EnvironmentVariables['PICER_BOOTSTRAP_ROOT'] = $bootstrap
$launch.EnvironmentVariables['PICER_DATA_ROOT'] = Join-Path $smokeRoot 'data'
$launch.EnvironmentVariables['DOTNET_ROOT'] = Join-Path $smokeRoot 'no-installed-runtime'
$launch.EnvironmentVariables['DOTNET_MULTILEVEL_LOOKUP'] = '0'
$process = [System.Diagnostics.Process]::Start($launch)
$failure = $null
$result = $null
try {
    $deadline = [DateTime]::UtcNow.AddSeconds(45)
    do {
        Start-Sleep -Milliseconds 500
        $process.Refresh()
        if ($process.HasExited) { throw "Packaged GUI exited: $($process.ExitCode)" }
    } until ($process.MainWindowHandle -ne 0 -or [DateTime]::UtcNow -gt $deadline)
    if ($process.MainWindowHandle -eq 0) {
        $startupLog = Join-Path $smokeRoot 'data\logs\crash.log'
        throw "Packaged GUI did not show a window within 45 seconds; startupLogPresent=$(Test-Path -LiteralPath $startupLog); processId=$($process.Id); isolatedDataRoot=$smokeRoot"
    }
    if ($process.MainWindowTitle -ne 'Picer - 图片管理（本地）') { throw "Unexpected GUI window: $($process.MainWindowTitle)" }
    $runtime = @($process.Modules | Where-Object { $_.ModuleName -eq 'coreclr.dll' })
    if ($runtime.Count -ne 1 -or -not $runtime[0].FileName.StartsWith($PackageRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'GUI did not load its self-contained runtime' }
    Start-Sleep -Seconds 5
    $process.Refresh()
    if ($process.HasExited -or -not $process.Responding) { throw 'Packaged GUI is not responding' }
    $result = [ordered]@{ package=$PackageRoot; dataRoot=$smokeRoot; window=$process.MainWindowTitle; selfContainedRuntime=$runtime[0].FileName; responding=$process.Responding; processId=$process.Id; checkedUtc=[DateTime]::UtcNow.ToString('o') }
}
catch { $failure = $_ }
finally {
    if (-not $process.HasExited) {
        $process.Refresh()
        $closeRequested = $process.MainWindowHandle -ne 0 -and $process.CloseMainWindow()
        if (-not $closeRequested -or -not $process.WaitForExit(15000)) {
            if ($null -eq $failure) { $failure = 'Packaged GUI did not close normally after a successful startup' }
            # Only this script-owned isolated process may be forcibly cleaned up.
            if (-not $process.HasExited) {
                $actualPath = $process.MainModule.FileName
                if (-not $actualPath.Equals($launch.FileName, [StringComparison]::OrdinalIgnoreCase)) { throw 'Isolated cleanup executable mismatch' }
                $process.Kill()
                if (-not $process.WaitForExit(5000)) { throw 'Isolated smoke process cleanup timed out' }
            }
        }
    }
    $process.Dispose()
}
if ($null -ne $failure) { throw $failure }
$result['closedNormally'] = $true
$result | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $smokeRoot 'result.json') -Encoding UTF8
$result | ConvertTo-Json
