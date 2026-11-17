$ErrorActionPreference='Continue'
$root=(Resolve-Path -LiteralPath 'D:\picer\artifacts').Path
$keep=@('release-v1.5.6','release-candidate-20261112','agent-probe-20261112')
$cutoff=[datetime]'2026-11-01'
$dirs=@(Get-ChildItem -LiteralPath $root -Directory -Force | Where-Object {
  $n=$_.Name
  if($keep -contains $n){ return $false }
  if($n -like 'sync-*' -or $n -like 'test-insight-*' -or $n -like 'remote-analysis-*' -or $n -like 'deployment-backup-*' -or $n -like 'v*-program-backup-*' -or $n -like 'local-deploy-backup-*'){ return $true }
  if($_.LastWriteTime -lt $cutoff -and ($n -like 'release*' -or $n -like 'final*' -or $n -like 'linux-release*' -or $n -like 'indexservice-v*' -or $n -like 'installers-*' -or $n -like 'installer-v*validation' -or $n -like 'ci-*' -or $n -like 'p1-release*' -or $n -like 'p1-linux-release*' -or $n -like 'p1-repair-*' -or $n -like 'p2-release*' -or $n -like 'p2-linux-release*' -or $n -like 'p2-followup-*' -or $n -like 'remaining-release*' -or $n -like 'remaining-linux-release*' -or $n -like 'clean-linux-release*')){ return $true }
  return $false
})
$files=@(Get-ChildItem -LiteralPath $root -File -Force | Where-Object {
  $n=$_.Name
  ($n -like '*.zip' -or $n -like '*.tar' -or $n -like '*.tar.gz' -or $n -like '*failed*' -or $n -like 'head*.txt' -or $n -like '*.png' -or $n -like '*.jpg' -or $n -like '*.jpeg')
})
$all=@($dirs+$files)
$manifest='D:\picer\artifacts-cleanup-manifest-20261117.txt'
$csv='D:\picer\artifacts-cleanup-manifest-20261117.csv'
$all | Select-Object FullName,Name,Length,LastWriteTime,PSIsContainer | Export-Csv -LiteralPath $csv -NoTypeInformation -Encoding UTF8
@('Picer conservative cleanup manifest','Generated: '+(Get-Date -Format o),'Root: '+$root,'Directories: '+$dirs.Count,'Root files: '+$files.Count,'Total entries: '+$all.Count,'Candidates:') + ($all | ForEach-Object { $_.FullName }) | Set-Content -LiteralPath $manifest -Encoding UTF8
$before=(Get-PSDrive D).Free
$deleted=0;$failed=0
foreach($item in $all){
  try {
    $resolved=(Resolve-Path -LiteralPath $item.FullName -ErrorAction Stop).Path
    if(-not $resolved.StartsWith($root+'\',[StringComparison]::OrdinalIgnoreCase)){ throw 'outside artifacts root' }
    if(Test-Path -LiteralPath $resolved -PathType Container){ Remove-Item -LiteralPath $resolved -Recurse -Force -ErrorAction Stop }
    else { Remove-Item -LiteralPath $resolved -Force -ErrorAction Stop }
    $deleted++
  } catch { $failed++; Add-Content -LiteralPath $manifest -Value ('FAILED: '+$item.FullName+' :: '+$_.Exception.Message) }
}
$after=(Get-PSDrive D).Free
Add-Content -LiteralPath $manifest -Value ('Deleted entries: '+$deleted)
Add-Content -LiteralPath $manifest -Value ('Failed entries: '+$failed)
Add-Content -LiteralPath $manifest -Value ('D free before: '+$before)
Add-Content -LiteralPath $manifest -Value ('D free after: '+$after)
Add-Content -LiteralPath $manifest -Value ('D free delta: '+($after-$before))
Write-Output ('dirs='+$dirs.Count+' files='+$files.Count+' deleted='+$deleted+' failed='+$failed)
Write-Output ('freeBefore='+$before+' freeAfter='+$after+' delta='+($after-$before))
Write-Output ('manifest='+$manifest)
