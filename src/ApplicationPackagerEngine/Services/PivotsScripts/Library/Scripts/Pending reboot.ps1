$paths = @('HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending','HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired')
[pscustomobject]@{Device=$env:COMPUTERNAME;PendingReboot=@($paths | Where-Object {Test-Path -LiteralPath $_}).Count -gt 0} | ConvertTo-Json -Compress
