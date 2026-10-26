param([string]$ServiceName='CcmExec')
Get-Service -Name $ServiceName -ErrorAction Stop | Select-Object @{Name='Device';Expression={$env:COMPUTERNAME}},Name,@{Name='Status';Expression={[string]$_.Status}} | ConvertTo-Json -Compress
