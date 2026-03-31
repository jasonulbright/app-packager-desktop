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
            exit $proc.ExitCode
            """;

        var uninstallPs1 = $"""
            $msiPath = Join-Path $PSScriptRoot '{msiFileName}'
            $proc = Start-Process -FilePath 'msiexec.exe' -ArgumentList @('/x', "`"$msiPath`"", '/qn', '/norestart') -Wait -PassThru -NoNewWindow
            exit $proc.ExitCode
            """;

        WriteWrappers(outputPath, installPs1, uninstallPs1);
    }

    /// <summary>
    /// Generates all 4 wrapper files for an EXE installer.
    /// </summary>
    public void GenerateExeWrappers(string outputPath, string exeFileName, string installArgs, string uninstallCommand, string uninstallArgs = "")
    {
        var installPs1 = $"""
            $exePath = Join-Path $PSScriptRoot '{exeFileName}'
            $proc = Start-Process -FilePath $exePath -ArgumentList '{installArgs}' -Wait -PassThru -NoNewWindow
            exit $proc.ExitCode
            """;

        var uninstallPs1 = string.IsNullOrEmpty(uninstallArgs)
            ? $"""
            $proc = Start-Process -FilePath '{uninstallCommand}' -Wait -PassThru -NoNewWindow
            exit $proc.ExitCode
            """
            : $"""
            $proc = Start-Process -FilePath '{uninstallCommand}' -ArgumentList '{uninstallArgs}' -Wait -PassThru -NoNewWindow
            exit $proc.ExitCode
            """;

        WriteWrappers(outputPath, installPs1, uninstallPs1);
    }

    private static void WriteWrappers(string outputPath, string installPs1Content, string uninstallPs1Content)
    {
        var encoding = new UTF8Encoding(false); // ASCII-compatible, no BOM

        var batTemplate = """
            @echo off
            powershell.exe -NonInteractive -ExecutionPolicy Bypass -File "%~dp0{0}"
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
