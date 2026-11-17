param(
    [string]$CertificatePath = "$PSScriptRoot\..\artifacts\Picer-LocalDev-CodeSigning.pfx",
    [string]$CertificatePassword = "PicerLocalDevSigning2026!",
    [string]$OutputRoot = "$PSScriptRoot\..\src"
)
$ErrorActionPreference = 'Stop'
$signtool = Get-ChildItem 'C:\Program Files (x86)\Windows Kits\10\bin','C:\Program Files\Windows Kits\10\bin' -Filter signtool.exe -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $signtool) { throw '未找到 signtool.exe，请安装 Windows SDK 后重新运行。' }
if (-not (Test-Path -LiteralPath $CertificatePath)) { throw "找不到签名证书：$CertificatePath" }
$targets = Get-ChildItem -LiteralPath $OutputRoot -Recurse -File -Include *.exe,*.dll | Where-Object { $_.FullName -match '\\bin\\(Debug|Release)\\' }
foreach ($target in $targets) {
    & $signtool.FullName sign /fd SHA256 /f $CertificatePath /p $CertificatePassword $target.FullName
    if ($LASTEXITCODE -ne 0) { throw "签名失败：$($target.FullName)" }
}
Write-Output "已签名 $($targets.Count) 个文件。"
