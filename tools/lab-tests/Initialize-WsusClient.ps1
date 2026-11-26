<#
.SYNOPSIS
    Run on the development computer. Gives the lab client the WSUS console and proves that it can publish to the lab WSUS server. Repeatable.

.DESCRIPTION
    Step 1 runs Install-WsusConsole.ps1 on the client in a scheduled task as SYSTEM. Step 2 runs Test-WsusPublishing.ps1 with the application's WsusAdapter.ps1 in a
    scheduled task with a password logon of the publishing account, because a WinRM session cannot pass that account on to the WSUS server. Neither task uses the
    desktop of a signed-in user, takes a window lock, or restarts anything. The script removes the tasks and the folder it made.
    The signing certificate and its trust come from the lab build (configmgr-lab-builder, Set-LabWsusPublishing). Step 2 fails and names what is missing.
#>
param(
    [string]$Target = 'CLIENT01',
    [string]$ServerName = 'cm01.contoso.com',
    [int]$PortNumber = 8530,
    [string]$CredentialScript = 'C:\projects\app-packager\docs\lab-credentials.local.ps1'
)
$ErrorActionPreference = 'Stop'
. $CredentialScript
$folder = "C:\guide\runs\wsus-client-$(Get-Date -Format 'yyyyMMdd-HHmmss')"
$adapter = Join-Path $PSScriptRoot '..\..\src\ApplicationPackagerEngine\Services\Wsus\WsusAdapter.ps1'
$session = New-PSSession -ComputerName $Target -Credential $LabCredential

function Invoke-ClientTask {
    param([string]$ScriptLine, [pscredential]$Credential, [int]$Minutes)
    Invoke-Command $session {
        param($folder, $line, $credential, $minutes)
        $ErrorActionPreference = 'Stop'
        $task = "ApdWsusClient-$([guid]::NewGuid().ToString('N').Substring(0, 8))"
        $log = Join-Path $folder "$task.log"
        $command = "try { & $line *>&1 | Out-File '$log' -Encoding utf8 } catch { `$_ | Out-File '$log' -Encoding utf8 -Append; exit 1 }"
        $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($command))
        $register = @{
            TaskName = $task; RunLevel = 'Highest'
            Action   = New-ScheduledTaskAction -Execute powershell.exe -Argument "-NoProfile -ExecutionPolicy Bypass -EncodedCommand $encoded"
            Settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -ExecutionTimeLimit (New-TimeSpan -Minutes $minutes)
        }
        if ($credential) { $register.User = $credential.UserName; $register.Password = $credential.GetNetworkCredential().Password }
        else { $register.User = 'SYSTEM' }
        $null = Register-ScheduledTask @register
        try {
            $started = Get-Date
            Start-ScheduledTask $task
            do { Start-Sleep -Seconds 5; $state = (Get-ScheduledTask $task).State; $info = Get-ScheduledTaskInfo $task }
            while (($state -eq 'Running' -or $info.LastRunTime -lt $started.AddSeconds(-5)) -and (Get-Date) -lt $started.AddMinutes($minutes + 1))
            if (Test-Path $log) { Get-Content $log }
            if ($state -eq 'Running') { throw "The task did not end in $minutes minutes." }
            if ($info.LastTaskResult -ne 0) { throw "The task ended with result $($info.LastTaskResult)." }
        }
        finally { Unregister-ScheduledTask $task -Confirm:$false }
    } -ArgumentList $folder, $ScriptLine, $Credential, $Minutes
}

try {
    Invoke-Command $session { param($f) $null = New-Item -ItemType Directory -Force $f } -ArgumentList $folder
    foreach ($file in (Join-Path $PSScriptRoot 'Install-WsusConsole.ps1'), (Join-Path $PSScriptRoot 'Test-WsusPublishing.ps1'), $adapter) {
        Copy-Item $file $folder -ToSession $session
    }
    Invoke-ClientTask -ScriptLine "'$folder\Install-WsusConsole.ps1' -TimeoutMinutes 40" -Minutes 45
    Invoke-ClientTask -ScriptLine "'$folder\Test-WsusPublishing.ps1' -ServerName '$ServerName' -PortNumber $PortNumber" -Credential $LabCredential -Minutes 15
}
finally {
    Invoke-Command $session { param($f) Remove-Item $f -Recurse -Force -ErrorAction SilentlyContinue } -ArgumentList $folder
    Remove-PSSession $session
}
