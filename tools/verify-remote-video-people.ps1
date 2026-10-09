[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][long]$VideoId,
    [string]$EndpointFile = "$env:LOCALAPPDATA\Picer\bootstrap\service-endpoint.json"
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Security
$settings = Get-Content -LiteralPath $EndpointFile -Raw -Encoding UTF8 | ConvertFrom-Json
$cipherText = [string]$settings.TokenCipher
if ($cipherText.StartsWith('plain:')) {
    $tokenBytes = [Convert]::FromBase64String($cipherText.Substring(6))
} else {
    if ($cipherText.StartsWith('dpapi:')) { $cipherText = $cipherText.Substring(6) }
    $tokenBytes = [Security.Cryptography.ProtectedData]::Unprotect(
        [Convert]::FromBase64String($cipherText), $null, [Security.Cryptography.DataProtectionScope]::CurrentUser)
}

$names = @('PICER_PROBE_ENDPOINT', 'PICER_PROBE_PIN', 'PICER_PROBE_TOKEN', 'PICER_PROBE_VIDEO_ID')
$previous = @{}
foreach ($name in $names) { $previous[$name] = [Environment]::GetEnvironmentVariable($name, 'Process') }
try {
    $env:PICER_PROBE_ENDPOINT = $settings.Url
    $env:PICER_PROBE_PIN = $settings.SpkiPin
    $env:PICER_PROBE_TOKEN = [Text.Encoding]::UTF8.GetString($tokenBytes)
    $env:PICER_PROBE_VIDEO_ID = [string]$VideoId
    & python (Join-Path $PSScriptRoot 'verify-remote-video-people.py')
    if ($LASTEXITCODE -ne 0) { throw 'Remote video people analysis failed; see sanitized diagnostics above.' }
} finally {
    foreach ($name in $names) { [Environment]::SetEnvironmentVariable($name, $previous[$name], 'Process') }
    [Array]::Clear($tokenBytes, 0, $tokenBytes.Length)
}
