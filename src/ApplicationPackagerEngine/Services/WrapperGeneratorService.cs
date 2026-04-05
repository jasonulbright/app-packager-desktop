using System.Text;

namespace ApplicationPackagerEngine.Services;

/// <summary>
/// Generates install.bat/ps1 and uninstall.bat/ps1 wrapper files.
/// Matches the PS1 Write-ContentWrappers + New-MsiWrapperContent output exactly.
/// </summary>
public sealed class WrapperGeneratorService
{
    /// <summary>
    /// Generates all 4 wrapper files for an MSI installer.
    /// </summary>
    public void GenerateMsiWrappers(string outputPath, string msiFileName)
    {
        var installPs1 = $"""
            $msiPath = Join-Path $PSScriptRoot '{msiFileName}'
            $proc = Start-Process -FilePath 'msiexec.exe' -ArgumentList @('/i', "`"$msiPath`"", '/qn', '/norestart') -Wait -PassThru -NoNewWindow
            $proc.ExitCode | Out-File (Join-Path $PSScriptRoot 'exitcode.txt') -NoNewline
            exit $proc.ExitCode
            """;

        var uninstallPs1 = $"""
            $msiPath = Join-Path $PSScriptRoot '{msiFileName}'
            $proc = Start-Process -FilePath 'msiexec.exe' -ArgumentList @('/x', "`"$msiPath`"", '/qn', '/norestart') -Wait -PassThru -NoNewWindow
            $proc.ExitCode | Out-File (Join-Path $PSScriptRoot 'exitcode.txt') -NoNewline
            exit $proc.ExitCode
            """;

        WriteWrappers(outputPath, installPs1, uninstallPs1);
    }

    /// <summary>
    /// Generates all 4 wrapper files for an EXE installer.
    /// Uninstall uses the same EXE with uninstall args, or looks up
    /// the uninstall string from ARP registry for InnoSetup/NSIS apps.
    /// </summary>
    public void GenerateExeWrappers(string outputPath, string exeFileName,
        string installArgs, string uninstallArgs, string? installerFramework = null)
    {
        var installPs1 = $"""
            $exePath = Join-Path $PSScriptRoot '{exeFileName}'
            $proc = Start-Process -FilePath $exePath -ArgumentList '{installArgs}' -Wait -PassThru -NoNewWindow
            $proc.ExitCode | Out-File (Join-Path $PSScriptRoot 'exitcode.txt') -NoNewline
            exit $proc.ExitCode
            """;

        // Framework-aware uninstall script
        string uninstallPs1;
        if (installerFramework is "InnoSetup")
        {
            // InnoSetup: find and run unins000.exe from the install directory via ARP UninstallString
            uninstallPs1 = $$"""
                # Find uninstall string from ARP registry
                $arpPaths = @(
                    'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall',
                    'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall'
                )
                foreach ($arpPath in $arpPaths) {
                    Get-ChildItem $arpPath -ErrorAction SilentlyContinue | ForEach-Object {
                        $key = Get-ItemProperty $_.PSPath -ErrorAction SilentlyContinue
                        if ($key.UninstallString -like '*unins*') {
                            $uninstaller = $key.UninstallString -replace '"', ''
                            if (Test-Path $uninstaller) {
                                $proc = Start-Process -FilePath $uninstaller -ArgumentList '{{uninstallArgs}}' -Wait -PassThru -NoNewWindow
                                $proc.ExitCode | Out-File (Join-Path $PSScriptRoot 'exitcode.txt') -NoNewline
                                exit $proc.ExitCode
                            }
                        }
                    }
                }
                # Fallback: try the installer with uninstall args
                $exePath = Join-Path $PSScriptRoot '{{exeFileName}}'
                $proc = Start-Process -FilePath $exePath -ArgumentList '{{uninstallArgs}}' -Wait -PassThru -NoNewWindow
                $proc.ExitCode | Out-File (Join-Path $PSScriptRoot 'exitcode.txt') -NoNewline
                exit $proc.ExitCode
                """;
        }
        else if (installerFramework is "NSIS")
        {
            // NSIS: find uninstaller from ARP UninstallString
            uninstallPs1 = $$"""
                $arpPaths = @(
                    'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall',
                    'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall'
                )
                foreach ($arpPath in $arpPaths) {
                    Get-ChildItem $arpPath -ErrorAction SilentlyContinue | ForEach-Object {
                        $key = Get-ItemProperty $_.PSPath -ErrorAction SilentlyContinue
                        if ($key.UninstallString -and $key.UninstallString -notlike '*msiexec*') {
                            $uninstaller = $key.UninstallString -replace '"', ''
                            if ((Test-Path $uninstaller) -and ($key.DisplayName -like '*{{exeFileName.Replace(".exe","")}}*')) {
                                $proc = Start-Process -FilePath $uninstaller -ArgumentList '{{uninstallArgs}}' -Wait -PassThru -NoNewWindow
                                $proc.ExitCode | Out-File (Join-Path $PSScriptRoot 'exitcode.txt') -NoNewline
                                exit $proc.ExitCode
                            }
                        }
                    }
                }
                exit 1
                """;
        }
        else
        {
            // Default: look up vendor uninstaller from ARP, fall back to installer EXE with args
            uninstallPs1 = $$"""
                # Try ARP UninstallString first (vendor's own uninstaller)
                $arpPaths = @(
                    'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall',
                    'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall'
                )
                foreach ($arpPath in $arpPaths) {
                    Get-ChildItem $arpPath -ErrorAction SilentlyContinue | ForEach-Object {
                        $key = Get-ItemProperty $_.PSPath -ErrorAction SilentlyContinue
                        if ($key.UninstallString -and $key.UninstallString -notlike '*msiexec*') {
                            $uninstaller = $key.UninstallString -replace '"', ''
                            if (Test-Path $uninstaller) {
                                $proc = Start-Process -FilePath $uninstaller -ArgumentList '{{uninstallArgs}}' -Wait -PassThru -NoNewWindow
                                $proc.ExitCode | Out-File (Join-Path $PSScriptRoot 'exitcode.txt') -NoNewline
                                exit $proc.ExitCode
                            }
                        }
                    }
                }
                # Fallback: try the installer EXE with uninstall args
                $exePath = Join-Path $PSScriptRoot '{{exeFileName}}'
                $proc = Start-Process -FilePath $exePath -ArgumentList '{{uninstallArgs}}' -Wait -PassThru -NoNewWindow
                $proc.ExitCode | Out-File (Join-Path $PSScriptRoot 'exitcode.txt') -NoNewline
                exit $proc.ExitCode
                """;
        }

        WriteWrappers(outputPath, installPs1, uninstallPs1);
    }

    private static void WriteWrappers(string outputPath, string installPs1Content, string uninstallPs1Content)
    {
        var encoding = new UTF8Encoding(false); // ASCII-compatible, no BOM

        // PowerShell only returns 0 or 1 to cmd.exe. The ps1 writes the real
        // exit code to exitcode.txt, and the bat reads it back for the true value.
        var batTemplate = """
            @echo off
            del /q "%~dp0exitcode.txt" >nul 2>&1
            powershell.exe -NonInteractive -ExecutionPolicy Bypass -File "%~dp0{0}"
            if exist "%~dp0exitcode.txt" (
                set /p REALCODE=<"%~dp0exitcode.txt"
                del /q "%~dp0exitcode.txt" >nul 2>&1
                exit /b %REALCODE%
            )
            exit /b %ERRORLEVEL%
            """;

        WriteIfMissing(Path.Combine(outputPath, "install.ps1"), installPs1Content, encoding);
        WriteIfMissing(Path.Combine(outputPath, "uninstall.ps1"), uninstallPs1Content, encoding);
        WriteIfMissing(Path.Combine(outputPath, "install.bat"), string.Format(batTemplate, "install.ps1"), encoding);
        WriteIfMissing(Path.Combine(outputPath, "uninstall.bat"), string.Format(batTemplate, "uninstall.ps1"), encoding);
    }

    private static void WriteIfMissing(string path, string content, Encoding encoding)
    {
        if (!File.Exists(path))
            File.WriteAllText(path, content, encoding);
    }
}
