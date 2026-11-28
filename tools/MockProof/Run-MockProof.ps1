<#
.SYNOPSIS
    Builds the image proof tool, runs it on a lab client under the capture lock and copies the images back.
.DESCRIPTION
    Run it on the development computer. The application renders without the desktop of any user (session 0 of the remote session), so no
    signed-in session is touched. The lock file C:\guide\capture.lock keeps other lab runs from running at the same time.
.EXAMPLE
    pwsh tools\MockProof\Run-MockProof.ps1 -OutDir C:\temp\proof -Screens refine
#>
param(
    [Parameter(Mandatory)][string]$OutDir,
    [string[]]$Screens = @('refine'),
    [string[]]$Themes = @('light', 'dark'),
    [string]$Target = 'CLIENT01',
    [string]$CredentialScript = 'C:\projects\app-packager\docs\lab-credentials.local.ps1',
    [int]$LockWaitMinutes = 90,
    [switch]$NoBuild
)
$ErrorActionPreference = 'Stop'
. $CredentialScript
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
if (-not $NoBuild) {
    $log = dotnet build (Join-Path $PSScriptRoot 'MockProof.csproj') -c Release 2>&1
    if ($LASTEXITCODE -ne 0) { $log | Select-String -Pattern ' error ' | Select-Object -Unique -First 20; throw 'build failed' }
}
$bin = Join-Path $PSScriptRoot 'bin\Release\net10.0-windows'
New-Item -ItemType Directory -Force $OutDir | Out-Null
$zip = Join-Path ([IO.Path]::GetTempPath()) "mockproof-$PID.zip"
Compress-Archive -Path (Join-Path $bin '*') -DestinationPath $zip -Force
$token = 'mockproof-{0}|{1}|{2}' -f (Get-Date -Format yyyyMMdd-HHmmss), $PID, (Get-Date -Format s)
$dest = "C:\guide\mockproof-$PID"
$session = New-PSSession -ComputerName $Target -Credential $LabCredential
$held = $false
try {
    $deadline = (Get-Date).AddMinutes($LockWaitMinutes)
    while (-not $held) {
        $held = Invoke-Command $session {
            param($value)
            $null = New-Item -ItemType Directory -Force C:\guide
            try {
                $stream = [IO.File]::Open('C:\guide\capture.lock', [IO.FileMode]::CreateNew, [IO.FileAccess]::Write)
                $bytes = [Text.Encoding]::ASCII.GetBytes($value); $stream.Write($bytes, 0, $bytes.Length); $stream.Close(); $true
            } catch [IO.IOException] { $false }
        } -ArgumentList $token
        if (-not $held) {
            if ((Get-Date) -ge $deadline) { throw "The capture lock stayed held: $(Invoke-Command $session { Get-Content C:\guide\capture.lock -ErrorAction SilentlyContinue })" }
            Start-Sleep -Seconds 30
        }
    }
    Invoke-Command $session { param($d) $null = New-Item -ItemType Directory -Force "$d\app", "$d\out" } -ArgumentList $dest
    Copy-Item -LiteralPath $zip -Destination "$dest\app.zip" -ToSession $session
    Invoke-Command $session { param($d) Expand-Archive -LiteralPath "$d\app.zip" -DestinationPath "$d\app" -Force } -ArgumentList $dest
    foreach ($theme in $Themes) {
        Invoke-Command $session {
            param($d, $theme, $screens)
            $env:DOTNET_ROOT = 'C:\tools\dotnet'
            # The data folder and the sample report of the tool go into the temporary folder, so they stand in the run folder, which is removed.
            $env:TEMP = $env:TMP = "$d\temp"
            $null = New-Item -ItemType Directory -Force $env:TEMP
            $p = Start-Process -FilePath 'C:\tools\dotnet\dotnet.exe' -ArgumentList "`"$d\app\MockProof.dll`" `"$d\out`" $theme $screens" -PassThru -NoNewWindow -RedirectStandardOutput "$d\$theme.log" -RedirectStandardError "$d\$theme.err"
            $null = $p.Handle
            if (-not $p.WaitForExit(600000)) { $p.Kill(); 'the render did not end in 10 minutes' }
            Get-Content "$d\$theme.log", "$d\$theme.err" -ErrorAction SilentlyContinue
        } -ArgumentList $dest, $theme, ($Screens -join ',')
    }
    $files = Invoke-Command $session { param($d) Get-ChildItem "$d\out" -Filter *.png | ForEach-Object FullName } -ArgumentList $dest
    foreach ($f in $files) { Copy-Item -FromSession $session -LiteralPath $f -Destination $OutDir -Force }
}
finally {
    Invoke-Command $session {
        param($d, $value)
        if (Test-Path -LiteralPath $d) { Remove-Item -LiteralPath $d -Recurse -Force -ErrorAction SilentlyContinue }
        if (([string](Get-Content C:\guide\capture.lock -Raw -ErrorAction SilentlyContinue)).Trim() -eq $value) { Remove-Item C:\guide\capture.lock -Force }
    } -ArgumentList $dest, $token -ErrorAction SilentlyContinue
    Remove-PSSession $session
    Remove-Item -LiteralPath $zip -Force -ErrorAction SilentlyContinue
}
