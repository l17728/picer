[CmdletBinding()]
param(
    [string]$Project = "tests/Picer.Tests/Picer.Tests.csproj",
    [string]$Filter = "Category!=RuntimeComponent",
    [int]$HeartbeatSeconds = 30
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$projectPath = (Resolve-Path (Join-Path $repo $Project)).Path
$logDir = Join-Path $repo 'artifacts/test-logs'
New-Item -ItemType Directory -Force -Path $logDir | Out-Null
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$stdout = Join-Path $logDir "picer-tests-$stamp.out.log"
$stderr = Join-Path $logDir "picer-tests-$stamp.err.log"

# A second full test host can contend for the same temporary SQLite/ONNX
# resources and looks like a hang.  Serialize full-suite invocations.
$mutex = [Threading.Mutex]::new($false, 'Global\Picer.Tests.FullSuite')
if (-not $mutex.WaitOne(0)) { throw 'Another Picer.Tests full-suite run is already active.' }
try {
    $arguments = @('test', $projectPath, '-c', 'Debug', '--no-build', '--no-restore', '--filter', $Filter, '--logger', 'console;verbosity=normal')
    $process = Start-Process -FilePath 'dotnet' -ArgumentList $arguments -WorkingDirectory $repo `
        -RedirectStandardOutput $stdout -RedirectStandardError $stderr -PassThru -WindowStyle Hidden
    $started = Get-Date
    while (-not $process.HasExited) {
        Start-Sleep -Seconds ([Math]::Max(5, $HeartbeatSeconds))
        $process.Refresh()
        $last = if (Test-Path $stdout) { Get-Content $stdout -Tail 1 -ErrorAction SilentlyContinue } else { '' }
        $elapsed = ((Get-Date) - $started).ToString('hh\:mm\:ss')
        Write-Host "[$elapsed] Picer.Tests is still running; last output: $last"
    }
    exit $process.ExitCode
}
finally {
    $mutex.ReleaseMutex() | Out-Null
    $mutex.Dispose()
}
