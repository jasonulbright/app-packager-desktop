using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// A definition's UninstallCommand written as a plain command line used to
/// be pasted into the uninstall wrapper as written, where PowerShell parses
/// the quoted path as a string expression and stops at the first switch.
/// </summary>
public sealed class UninstallCommandNormalizationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "packr-unc-" + Guid.NewGuid().ToString("N"));

    public UninstallCommandNormalizationTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    [Fact]
    public void QuotedPathCommandLine_BecomesTheStartProcessForm()
    {
        var body = WrapperGeneratorService.NormalizeUninstallCommand(
            @"""C:\Program Files\Stellarium\unins000.exe"" /VERYSILENT /SUPPRESSMSGBOXES /NORESTART");

        Assert.NotNull(body);
        Assert.Contains(@"$uninstaller = [Environment]::ExpandEnvironmentVariables('C:\Program Files\Stellarium\unins000.exe')", body);
        Assert.Contains("if (-not (Test-Path -LiteralPath $uninstaller)) { exit 0 }", body);
        Assert.Contains("$proc = Start-Process -FilePath $uninstaller -ArgumentList @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART') -Wait -PassThru -NoNewWindow", body);
    }

    [Fact]
    public void BareFileName_RunsTheStagedFile()
    {
        var body = WrapperGeneratorService.NormalizeUninstallCommand("adksetup.exe /quiet /uninstall");

        Assert.Equal("$exePath = Join-Path $PSScriptRoot 'adksetup.exe'\r\n" +
                     "$proc = Start-Process -FilePath $exePath -ArgumentList @('/quiet', '/uninstall') -Wait -PassThru -NoNewWindow", body);
    }

    [Fact]
    public void ProcExpression_IsLeftAsWritten()
    {
        const string expression = "$proc = Start-Process -FilePath \"C:\\Program Files\\KDiff3\\uninstall.exe\" -ArgumentList \"/S\" -Wait -PassThru -NoNewWindow; while (Get-Process -Name 'Au_' -ErrorAction SilentlyContinue) { Start-Sleep -Seconds 1 }";

        Assert.Same(expression, WrapperGeneratorService.NormalizeUninstallCommand(expression));
        Assert.Null(WrapperGeneratorService.NormalizeUninstallCommand("   "));
        Assert.Null(WrapperGeneratorService.NormalizeUninstallCommand(null));
    }

    [Fact]
    public void InnerQuotes_SurviveTokenization()
    {
        var body = WrapperGeneratorService.NormalizeUninstallCommand(
            @"""C:\Setup\setup.exe"" /Q /SQLSYSADMINACCOUNTS=""BUILTIN\Administrators""");

        Assert.Contains(@"@('/Q', '/SQLSYSADMINACCOUNTS=""BUILTIN\Administrators""')", body);
    }

    [Fact]
    public void GeneratedWrapper_CarriesTheNormalizedBody()
    {
        new WrapperGeneratorService().GenerateExeWrappers(_dir, "stellarium-25.3-win64.exe",
            "/ALLUSERS /VERYSILENT", "/VERYSILENT", "InnoSetup", "Stellarium (x64)",
            @"""C:\Program Files\Stellarium\unins000.exe"" /VERYSILENT /SUPPRESSMSGBOXES /NORESTART");
        var script = File.ReadAllText(Path.Combine(_dir, "uninstall.ps1"));

        Assert.DoesNotContain("\"C:\\Program Files\\Stellarium\\unins000.exe\" /VERYSILENT", script);
        Assert.Contains("$proc = Start-Process -FilePath $uninstaller", script);
        Assert.Contains("exit $proc.ExitCode", script);
    }

    [Fact]
    public void EveryDefinitionUninstallCommand_NormalizesToAProcAssignment()
    {
        var folder = Path.Combine(AppContext.BaseDirectory, "Definitions");
        var offenders = new List<string>();
        foreach (var file in Directory.GetFiles(folder, "*.json"))
        {
            var def = DefinitionFixture.Load(Path.GetFileName(file));
            if (string.IsNullOrWhiteSpace(def.UninstallCommand)) continue;
            var body = WrapperGeneratorService.NormalizeUninstallCommand(def.UninstallCommand);
            if (body is null || !body.Contains("$proc", StringComparison.Ordinal))
                offenders.Add(Path.GetFileName(file));
        }
        Assert.Empty(offenders);
    }
}
