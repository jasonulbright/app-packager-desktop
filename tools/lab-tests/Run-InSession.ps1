<#
.SYNOPSIS
    Runs a command from a local folder in the signed-in session of the lab user on a lab client, under the capture lock, inside a job object.

.DESCRIPTION
    Run it on the development computer. It takes the lock file C:\guide\capture.lock (owner|pid|start time; the file that every window run of the
    lab uses), copies the folder into a run folder C:\guide\runs\<owner>-<time>\app, starts the command through a scheduled task with the interactive
    logon of the lab user, streams the output, copies the output folder back, and removes the task, the run folder and its lock.

    The command runs in a job object (ProcessJob.ps1). When it ends, fails or passes a time limit, the job is closed and that ends what the command
    started, and nothing else. This script reads no process list and stops no process. It never signs out, locks, disconnects or restarts anything.
    A lock of another run is never removed: when the lock stays held, the run gives up.

    The task refuses to run unless its session is the session that -SessionId names, so a run never goes to another session of the same user.

.PARAMETER Folder
    The local folder to copy. It must hold ProcessJob.ps1 or the script takes the one that lies beside it.
.PARAMETER Command
    PowerShell text that runs in the copied folder. $RunOutput names the folder whose files are copied back.
.PARAMETER SessionId
    The id of the active session of the lab user, as quser shows it.
#>
param(
    [Parameter(Mandatory)][string]$Folder,
    [Parameter(Mandatory)][string]$Command,
    [Parameter(Mandatory)][int]$SessionId,
    [Parameter(Mandatory)][string]$OutFile,
    [string]$ResultFolder,
    [int]$Minutes = 40,
    [int]$QuietMinutes = 15,
    [int]$LockWaitMinutes = 60,
    [string]$Owner = 'apd-release',
    [string]$Target = 'CLIENT01',
    [string]$LogonUser = 'contoso\labadmin',
    [string]$CredentialScript = 'C:\projects\app-packager\docs\lab-credentials.local.ps1'
)
$ErrorActionPreference = 'Stop'
. $CredentialScript
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$leaf = "$Owner-$stamp"
$dest = "C:\guide\runs\$leaf"
$task = "ApdRun-$leaf"
$token = '{0}|{1}|{2}' -f $leaf, $PID, (Get-Date -Format s)
$zip = Join-Path ([IO.Path]::GetTempPath()) "$leaf.zip"
$helper = Join-Path $PSScriptRoot 'ProcessJob.ps1'
$haveLock = $false
$failure = $null
$session = New-PSSession -ComputerName $Target -Credential $LabCredential
try {
    $deadline = (Get-Date).AddMinutes($LockWaitMinutes)
    while (-not $haveLock) {
        $haveLock = Invoke-Command $session {
            param($value)
            $null = New-Item -ItemType Directory -Force C:\guide
            try {
                $stream = [IO.File]::Open('C:\guide\capture.lock', [IO.FileMode]::CreateNew, [IO.FileAccess]::Write)
                $bytes = [Text.Encoding]::ASCII.GetBytes($value)
                $stream.Write($bytes, 0, $bytes.Length); $stream.Close(); $true
            } catch [IO.IOException] { $false }
        } -ArgumentList $token
        if (-not $haveLock) {
            if ((Get-Date) -ge $deadline) { throw "The capture lock stayed held: $(Invoke-Command $session { Get-Content C:\guide\capture.lock -ErrorAction SilentlyContinue })" }
            Start-Sleep -Seconds 30
        }
    }

    Compress-Archive -Path (Join-Path $Folder '*') -DestinationPath $zip -Force
    Invoke-Command $session { param($d) $null = New-Item -ItemType Directory -Force "$d\app", "$d\out" } -ArgumentList $dest
    Copy-Item -LiteralPath $zip -Destination "$dest\app.zip" -ToSession $session
    Copy-Item -LiteralPath $helper -Destination "$dest\ProcessJob.ps1" -ToSession $session

    # command.ps1 holds the command; wrap.ps1 runs it in a job and watches the time limits.
    $commandText = "`$ErrorActionPreference = 'Continue'`r`n`$RunOutput = '$dest\out'`r`nSet-Location -LiteralPath '$dest\app'`r`n& { $Command } *>&1 | ForEach-Object { (`$_ | Out-String -Width 220).TrimEnd() }`r`n"
    $wrapText = @'
param([string]$Dir, [int]$Session, [int]$Minutes, [int]$Quiet)
$out = "$Dir\out.txt"
try {
    $mine = (Get-Process -Id $PID).SessionId
    if ($mine -ne $Session) { throw "The task runs in session $mine, not in session $Session. Nothing was run." }
    . "$Dir\ProcessJob.ps1"
    $env:SUITE_ALLOW_WINDOW_TESTS = '1'
    $env:SUITE_VISIBLE_WINDOWS = '1'
    Set-Content "$Dir\started" "session $mine"
    $run = Start-JobProcess -FilePath 'powershell.exe' -ArgumentList @('-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-STA', '-File', "`"$Dir\command.ps1`"") -StandardOutput $out -StandardError "$Dir\err.txt"
    try {
        $start = Get-Date; $changed = Get-Date; $length = -1L
        while (-not $run.Process.HasExited) {
            Start-Sleep -Seconds 5
            $now = if (Test-Path -LiteralPath $out) { (Get-Item -LiteralPath $out).Length } else { 0 }
            if ($now -ne $length) { $length = $now; $changed = Get-Date }
            if (((Get-Date) - $start).TotalMinutes -ge $Minutes) { Set-Content "$Dir\failure" "the run did not finish in $Minutes minutes"; break }
            if (((Get-Date) - $changed).TotalMinutes -ge $Quiet) { Set-Content "$Dir\failure" "no output for $Quiet minutes"; break }
        }
    }
    finally { Stop-JobProcess $run }
}
catch { Set-Content "$Dir\failure" ($_ | Out-String) }
finally { Set-Content "$Dir\done" 'done' }
'@
    Invoke-Command $session {
        param($d, $commandText, $wrapText, $user, $task, $sessionId, $minutes, $quiet)
        Expand-Archive -LiteralPath "$d\app.zip" -DestinationPath "$d\app" -Force
        Remove-Item -LiteralPath "$d\app.zip"
        Set-Content -LiteralPath "$d\command.ps1" -Value $commandText -Encoding UTF8
        Set-Content -LiteralPath "$d\wrap.ps1" -Value $wrapText -Encoding Ascii
        $action = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument "-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$d\wrap.ps1`" -Dir `"$d`" -Session $sessionId -Minutes $minutes -Quiet $quiet"
        $principal = New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive -RunLevel Highest
        $settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit (New-TimeSpan -Minutes ($minutes + 10))
        $null = Register-ScheduledTask -TaskName $task -Action $action -Principal $principal -Settings $settings -Force
        Start-ScheduledTask -TaskName $task
    } -ArgumentList $dest, $commandText, $wrapText, $LogonUser, $task, $SessionId, $Minutes, $QuietMinutes

    $end = (Get-Date).AddMinutes($Minutes + 5)
    $startBy = (Get-Date).AddMinutes(3)
    do {
        Start-Sleep -Seconds 10
        $state = Invoke-Command $session {
            param($d, $value)
            # The holder keeps its own lock fresh, and only its own.
            if (([string](Get-Content C:\guide\capture.lock -Raw -ErrorAction SilentlyContinue)).Trim() -eq $value) { (Get-Item C:\guide\capture.lock).LastWriteTime = Get-Date }
            [pscustomobject]@{ Done = (Test-Path "$d\done"); Started = (Test-Path "$d\started") }
        } -ArgumentList $dest, $token
        if (-not $state.Started -and -not $state.Done -and (Get-Date) -ge $startBy) { $failure = 'The task did not start in 3 minutes.'; break }
    } while (-not $state.Done -and (Get-Date) -lt $end)
    if (-not $state.Done -and -not $failure) { $failure = 'The run did not report an end in time.' }

    $result = Invoke-Command $session {
        param($d)
        [pscustomobject]@{
            Out = [string](Get-Content "$d\out.txt" -Raw -ErrorAction SilentlyContinue)
            Err = [string](Get-Content "$d\err.txt" -Raw -ErrorAction SilentlyContinue)
            Failure = [string](Get-Content "$d\failure" -Raw -ErrorAction SilentlyContinue)
            Started = [string](Get-Content "$d\started" -Raw -ErrorAction SilentlyContinue)
        }
    } -ArgumentList $dest
    if ($result.Failure) { $failure = $result.Failure.Trim() }
    $text = "RUN $leaf $($result.Started.Trim())`r`n$($result.Out)"
    if ($result.Err.Trim()) { $text += "`r`nSTDERR:`r`n$($result.Err)" }
    if ($failure) { $text += "`r`nFAIL: $failure" }
    Set-Content -LiteralPath $OutFile -Value $text -Encoding UTF8
    if ($ResultFolder) {
        $null = New-Item -ItemType Directory -Force $ResultFolder
        Copy-Item -Path "$dest\out\*" -Destination $ResultFolder -FromSession $session -Recurse -Force -ErrorAction SilentlyContinue
    }
}
finally {
    Remove-Item -LiteralPath $zip -Force -ErrorAction SilentlyContinue
    if ($haveLock) {
        try {
            Invoke-Command $session {
                param($d, $task, $value)
                # Stopping the task ends the wrapper if it still runs; its job then closes and ends the members.
                Stop-ScheduledTask -TaskName $task -ErrorAction SilentlyContinue
                Unregister-ScheduledTask -TaskName $task -Confirm:$false -ErrorAction SilentlyContinue
                Start-Sleep -Seconds 3
                Remove-Item -LiteralPath $d -Recurse -Force -ErrorAction SilentlyContinue
                if (([string](Get-Content C:\guide\capture.lock -Raw -ErrorAction SilentlyContinue)).Trim() -eq $value) { Remove-Item C:\guide\capture.lock -Force }
                if (Test-Path -LiteralPath $d) { "LEFT: $d" }
            } -ArgumentList $dest, $task, $token
        } catch { Write-Warning "Cleanup failed: $($_.Exception.Message)" }
    }
    Remove-PSSession $session
}
if ($failure) { Write-Warning $failure; exit 1 }
