namespace ApplicationPackagerEngine.Services;

/// <summary>
/// Silent install/uninstall command templates per installer engine, plus the
/// substitution that turns a template into a concrete command for one file.
/// </summary>
public static class SilentSwitchDatabase
{
    private static readonly Dictionary<string, SilentSwitchEntry> Table = new(StringComparer.Ordinal)
    {
        ["NativeMSI"] = new()
        {
            Install = "msiexec.exe /i \"<MSI>\" /qn /norestart",
            Uninstall = "msiexec.exe /x \"<ProductCode>\" /qn /norestart",
            InstallArgs = "/qn /norestart",
            UninstallArgs = "/qn /norestart",
            Notes = "ProductCode required for uninstall. Add /L*v \"<log>\" for verbose logging."
        },
        ["NSIS"] = new()
        {
            Install = "\"<EXE>\" /S",
            Uninstall = "\"<UninstallEXE>\" /S",
            Notes = "/S is case sensitive (uppercase S). Some NSIS installers also support /D=<path> for install directory."
        },
        ["InnoSetup"] = new()
        {
            Install = "\"<EXE>\" /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP-",
            Uninstall = "\"<UninstallEXE>\" /VERYSILENT /SUPPRESSMSGBOXES /NORESTART",
            Notes = "/SP- suppresses the initial \"This will install...\" prompt. /DIR=\"<path>\" for a custom directory."
        },
        ["InstallShield"] = new()
        {
            Install = "\"<EXE>\" /s /v\"/qn\"",
            Uninstall = "\"<EXE>\" /s /v\"/qn\" /x",
            Notes = "Some InstallShield installers require a response file: -s -f1\"setup.iss\""
        },
        ["WixBurn"] = new()
        {
            Install = "\"<EXE>\" /quiet /norestart",
            Uninstall = "\"<EXE>\" /uninstall /quiet /norestart",
            Notes = "/log \"<path>\" for logging. Burn bundles contain embedded MSI/EXE payloads."
        },
        ["AdvancedInstaller"] = new()
        {
            Install = "\"<EXE>\" /i /qn",
            Uninstall = "\"<EXE>\" /x /qn",
            Notes = "Based on MSI technology. May also accept msiexec.exe switches on the embedded MSI."
        },
        ["BitRock"] = new()
        {
            Install = "\"<EXE>\" --mode unattended --unattendedmodeui none",
            Uninstall = "\"<UninstallEXE>\" --mode unattended --unattendedmodeui none",
            Notes = "BitRock InstallBuilder. Modes: unattended, text, gtk, win32, osx, xwindow. The uninstaller lives at %INSTALLDIR%\\uninstall.exe by default."
        },
        ["Squirrel"] = new()
        {
            Install = "\"<EXE>\" --silent",
            Uninstall = "\"%LOCALAPPDATA%\\<AppName>\\Update.exe\" --uninstall -s",
            Notes = "Installs per-user to %LOCALAPPDATA%\\<AppName>\\. Use --silent (double dash, lowercase), not the /S NSIS switch."
        },
        ["Velopack"] = new()
        {
            Install = "\"<EXE>\" --silent",
            Uninstall = "",
            Notes = "Silent install: --silent or -s. Installs per-user to %LOCALAPPDATA%\\<PackageId> by default. --installto (-t) overrides the folder, --log (-l) sets the log file. Uninstall runs the installed Update.exe --uninstall --silent; its path is resolved from the embedded nuspec."
        },
        ["MSIX"] = new()
        {
            Install = "Add-AppxPackage -Path \"<EXE>\"",
            Uninstall = "Remove-AppxPackage -Package \"<PackageFullName>\"",
            Notes = "Per-user: Add-AppxPackage. All-users: Add-AppxProvisionedPackage -Online -PackagePath \"<msix>\" -SkipLicense."
        },
        ["MsixBundle"] = new()
        {
            Install = "Add-AppxPackage -Path \"<EXE>\"",
            Uninstall = "Remove-AppxPackage -Package \"<PackageFullName>\"",
            Notes = "Same cmdlets as a single MSIX. The bundle targets multiple architectures and/or language resource packs; AppxBundleManifest.xml lists which."
        },
        ["PsadtV3"] = new()
        {
            Install = "Deploy-Application.exe -DeploymentType Install -DeployMode Silent",
            Uninstall = "Deploy-Application.exe -DeploymentType Uninstall -DeployMode Silent",
            Notes = "PSAppDeployToolkit v3. Per-app logic lives in Deploy-Application.ps1."
        },
        ["PsadtV4"] = new()
        {
            Install = "Invoke-AppDeployToolkit.exe -DeploymentType Install -DeployMode Silent",
            Uninstall = "Invoke-AppDeployToolkit.exe -DeploymentType Uninstall -DeployMode Silent",
            Notes = "PSAppDeployToolkit v4. Per-app logic lives in Invoke-AppDeployToolkit.ps1."
        },
        ["Chocolatey"] = new()
        {
            Install = "choco install <AppName> --version=<Version> -y --source=\"<SourceDirOrFeed>\"",
            Uninstall = "choco uninstall <AppName> -y",
            Notes = "Requires the Chocolatey runtime. Package metadata lives in <PackageId>.nuspec at the archive root."
        },
        ["NuGet"] = new()
        {
            Install = "nuget install <AppName> -Version <Version> -Source \"<SourceDirOrFeed>\"",
            Uninstall = "N/A (NuGet is a package source, not an installer)",
            Notes = "Plain NuGet package without Chocolatey tools. Not end-user installable by itself."
        },
        ["Intunewin"] = new()
        {
            Install = "\"<ExtractedSetup>\" <OriginalSilentSwitches>",
            Uninstall = "Defined in the Intune portal, not in the .intunewin file.",
            Notes = "Not directly executable. The inner IntunePackage.intunewin is encrypted; keys are delivered by Intune to the managed endpoint."
        },
        ["SevenZipSfx"] = new()
        {
            Install = "Self-extracting archive. Extract, then run the embedded installer.",
            Uninstall = "Depends on the extracted payload.",
            Notes = "Not a true installer. Extract the contents to identify the actual installer inside."
        },
        ["WinRarSfx"] = new()
        {
            Install = "Self-extracting archive. Extract, then run the embedded installer.",
            Uninstall = "Depends on the extracted payload.",
            Notes = "Not a true installer. Extract the contents to identify the actual installer inside."
        },
        ["ZIP"] = new()
        {
            Install = "Archive. Extract, then run the embedded installer.",
            Uninstall = "Depends on the extracted payload.",
            Notes = "Not a true installer. Extract the contents to identify the actual installer inside."
        },
        ["MSP"] = new()
        {
            Install = "msiexec.exe /p \"<MSI>\" /qn /norestart",
            Uninstall = "msiexec.exe /package \"<ProductCode>\" /uninstall \"<MSI>\" /qn /norestart",
            InstallArgs = "/qn /norestart",
            UninstallArgs = "/qn /norestart",
            Notes = "Patches apply against an installed base MSI; an .msp is not a standalone deployment."
        },
        ["Unknown"] = new()
        {
            Install = "",
            Uninstall = "",
            Notes = "Installer engine not recognized. Common switches to try: /S, /silent, /quiet, /q, /VERYSILENT."
        }
    };

    /// <summary>Whether the engine has a switch entry of its own.</summary>
    public static bool IsKnownEngine(string engine) => Table.ContainsKey(engine);

    /// <summary>Returns the raw template entry for an engine, falling back to Unknown.</summary>
    public static SilentSwitchEntry Lookup(string engine) =>
        Table.TryGetValue(engine, out var entry) ? entry : Table["Unknown"];

    /// <summary>
    /// Resolves the engine's templates against one concrete file, substituting the
    /// installer filename, the MSI ProductCode, and the app name where each applies.
    /// </summary>
    public static ResolvedSwitches Resolve(
        string engine, string installerFileName, string? productCode = null, string? appName = null)
    {
        var entry = Lookup(engine);
        var install = Substitute(entry.Install, installerFileName, productCode, appName, engine);
        var uninstall = Substitute(entry.Uninstall, installerFileName, productCode, appName, engine);

        var installArgs = entry.InstallArgs ?? ExtractArgs(install);
        var uninstallArgs = entry.UninstallArgs ?? ExtractArgs(uninstall);

        return new ResolvedSwitches
        {
            Engine = engine,
            InstallCommand = install,
            UninstallCommand = uninstall,
            InstallArgs = installArgs,
            UninstallArgs = uninstallArgs,
            Notes = entry.Notes
        };
    }

    private static string Substitute(
        string template, string installerFileName, string? productCode, string? appName, string engine)
    {
        if (string.IsNullOrEmpty(template)) return "";

        var text = template
            .Replace("<EXE>", installerFileName, StringComparison.Ordinal)
            .Replace("<MSI>", installerFileName, StringComparison.Ordinal)
            .Replace("<UninstallEXE>", UninstallExeFor(engine, appName), StringComparison.Ordinal);

        if (!string.IsNullOrEmpty(productCode))
            text = text.Replace("<ProductCode>", productCode, StringComparison.Ordinal);
        if (!string.IsNullOrEmpty(appName))
            text = text.Replace("<AppName>", appName, StringComparison.Ordinal);

        return text;
    }

    private static string UninstallExeFor(string engine, string? appName) =>
        engine == "Squirrel" && !string.IsNullOrEmpty(appName)
            ? $"%LOCALAPPDATA%\\{appName}\\Update.exe"
            : "uninstall.exe";

    /// <summary>
    /// Strips the leading executable from a resolved command line, leaving the bare
    /// arguments the wrapper generators expect. Returns an empty string when the first
    /// token is not an executable — the entry is then prose or a cmdlet invocation,
    /// and its remainder must not be handed to a wrapper as switches.
    /// </summary>
    private static string ExtractArgs(string command)
    {
        var text = (command ?? "").Trim();
        if (text.Length == 0) return "";

        string leading;
        string remainder;

        if (text[0] == '"')
        {
            var close = text.IndexOf('"', 1);
            if (close < 0) return "";
            leading = text[1..close];
            remainder = text[(close + 1)..];
        }
        else
        {
            var space = text.IndexOf(' ');
            if (space < 0) return "";
            leading = text[..space];
            remainder = text[space..];
        }

        return leading.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? remainder.Trim()
            : "";
    }
}

public sealed class SilentSwitchEntry
{
    public string Install { get; init; } = "";
    public string Uninstall { get; init; } = "";
    /// <summary>Explicit bare arguments; when null they are derived from the command line.</summary>
    public string? InstallArgs { get; init; }
    public string? UninstallArgs { get; init; }
    public string Notes { get; init; } = "";
}

public sealed class ResolvedSwitches
{
    public string Engine { get; init; } = "Unknown";
    public string InstallCommand { get; init; } = "";
    public string UninstallCommand { get; init; } = "";
    public string InstallArgs { get; init; } = "";
    public string UninstallArgs { get; init; } = "";
    public string Notes { get; init; } = "";
}
