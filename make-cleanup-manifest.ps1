$artifactRoot=(Resolve-Path 'D:\picer\artifacts').Path
$keep=@('release-v1.5.6','release-candidate-20261112','agent-probe-20261112')
$dirs=Get-ChildItem $artifactRoot -Directory | Where-Object {
  $n=$_.Name
  $old=$_.LastWriteTime -lt [datetime]'2026-11-01'
  $match=($n -like 'sync-*') -or ($n -like 'test-insight-*') -or ($n -like 'remote-analysis-*') -or (($n -match '^(release|final|p[12]-release|remaining-release|linux-release|indexservice|installers|clean-linux-release|ci-).*') -and $old) -or (($n -like 'deployment-backup-*' -or $n -like 'v*-program-backup-*' -or $n -like 'local-deploy-backup-*') -and $old)
  $match -and ($keep -notcontains $n)
}
$files=Get-ChildItem $artifactRoot -File | Where-Object { $_.Name -match '(?i)\.(zip|tar|tar\.gz)$|failed|screenshot|^head\d*\.txt$' }
$items=@($dirs)+@($files)
$manifest='D:\picer\artifacts-cleanup-manifest-20261117.txt'
$rows=@()
foreach($item in $items){$sum=if($item.PSIsContainer){(Get-ChildItem $item.FullName -File -Recurse -ErrorAction SilentlyContinue | Measure-Object Length -Sum).Sum}else{$item.Length};$rows += [pscustomobject]@{Path=$item.FullName;GB=[math]::Round($sum/1GB,3);Type=if($item.PSIsContainer){'Directory'}else{'File'}}}
$rows | Sort-Object Path | Format-Table -AutoSize | Out-String -Width 240 | Set-Content $manifest -Encoding UTF8
$rows | Sort-Object Path | Export-Csv ($manifest+'.csv') -NoTypeInformation -Encoding UTF8
Write-Output "Candidates=$($items.Count)"
Write-Output "BytesGB=$([math]::Round((($rows|Measure-Object GB -Sum).Sum),3))"
Write-Output "Manifest=$manifest"
