$client = Get-CimInstance -Namespace root\ccm -ClassName SMS_Client -ErrorAction Stop
[pscustomobject]@{Device=$env:COMPUTERNAME;ClientVersion=$client.ClientVersion} | ConvertTo-Json -Compress
