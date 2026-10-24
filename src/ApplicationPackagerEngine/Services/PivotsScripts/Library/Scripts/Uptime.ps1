$os=Get-CimInstance Win32_OperatingSystem -ErrorAction Stop
[pscustomobject]@{Device=$env:COMPUTERNAME;LastBoot=$os.LastBootUpTime.ToUniversalTime().ToString('o');UptimeHours=[math]::Round(((Get-Date)-$os.LastBootUpTime).TotalHours,1)} | ConvertTo-Json -Compress
