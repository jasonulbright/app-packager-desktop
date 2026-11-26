param([switch]$KeepForChecks)   # default: remove what the run created at the end (cleanup.ps1). Use -KeepForChecks when the Check ConfigMgr steps follow, then run cleanup.ps1 yourself.
$names = Get-ChildItem C:\guide\ps-run\Packagers -Filter package-*.ps1 | % { $_.BaseName -replace '^package-','' } | Sort-Object
$skip = '^(m365|vs20|ssms|windowsadk|windowspeaddon|sqlserver|msvcruntimes$|dotnet10both$)'
$out='C:\guide\ps-batch'; New-Item -ItemType Directory $out -Force | Out-Null
$csv="$out\results.csv"; if(-not (Test-Path $csv)){ 'Packager,Status,Created,Version,Seconds,Reason' | Set-Content $csv }
$done = @(Import-Csv $csv | % Packager)
foreach($n in $names){
  if($n -in $done){ continue }
  if($n -match $skip){ "$n,Skipped,,,0,""not run: very large download or a combined packager""" | Add-Content $csv; continue }
  $sw=[Diagnostics.Stopwatch]::StartNew()
  $p = Start-Process powershell.exe -ArgumentList '-NoProfile','-NonInteractive','-File','C:\guide\ps-batch\one.ps1','-Name',$n -RedirectStandardOutput "$out\$n.log" -RedirectStandardError "$out\$n.err" -PassThru -WindowStyle Hidden
  if(-not $p.WaitForExit(600000)){ try{ $p.Kill() }catch{}; Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" | ? { $_.CommandLine -like "*one.ps1*$n*" } | % { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }; $st='Timeout' } else { $st='Done' }
  $log = Get-Content "$out\$n.log" -Raw -ErrorAction SilentlyContinue
  $created=''; $ver=''; $reason=''
  if($log -match 'Created ConfigMgr application\s*:\s*(.+)'){ $created=$Matches[1].Trim() }
  if($log -match 'SoftwareVersion\s*:\s*(\S+)'){ $ver=$Matches[1] }
  if($created){ $st='Created' } elseif($st -eq 'Done'){ $st='Failed'; $m=[regex]::Matches($log,'\[(ERROR|WARN )\]\s*(.+)'); if($m.Count){ $reason=$m[$m.Count-1].Groups[2].Value.Trim() } else { $reason=(($log -split "`n" | ? { $_.Trim() } | select -Last 1)).Trim() } }
  $reason=($reason -replace '[",\r\n]',' ')
  "$n,$st,""$created"",$ver,$([int]$sw.Elapsed.TotalSeconds),""$reason""" | Add-Content $csv
  Remove-Item "C:\guide\ap-dl\*" -Recurse -Force -ErrorAction SilentlyContinue
}
'DONE' | Set-Content "$out\done.flag"
if(-not $KeepForChecks){ & (Join-Path $PSScriptRoot 'cleanup.ps1') -CleanRunFiles }
