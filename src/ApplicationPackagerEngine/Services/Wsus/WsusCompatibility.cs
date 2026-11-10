using System.Text.RegularExpressions;
using ApplicationPackagerEngine.Models;

namespace ApplicationPackagerEngine.Services.Wsus;

/// <summary>
/// What a WSUS publish of a manifest would and would not do. The publisher
/// reads this list before it connects, so a blocking gap never leaves a
/// half-published update.
/// </summary>
public static partial class WsusCompatibility
{
    [GeneratedRegex(@"(?m)^[ \t]*if \(\$proc\.ExitCode -ne 0(?: -and \$proc\.ExitCode -ne -?\d+)*\) \{ exit \$proc\.ExitCode \}[ \t]*\r?$")]
    private static partial Regex PostInstallGuard();

    public static IReadOnlyList<WsusFinding> Evaluate(StageManifest manifest, string? contentFolder = null, WsusRuleBuilder? rules = null)
    {
        rules ??= new WsusRuleBuilder();
        var findings = new List<WsusFinding>();
        void Add(WsusSeverity severity, string code, string message) => findings.Add(new WsusFinding(severity, code, message));
        var hasContent = !string.IsNullOrEmpty(contentFolder);

        if (manifest.DeploymentTypes?.Any(d => d is not null) == true)
            Add(WsusSeverity.Blocking, WsusRefusalCodes.MultipleDeploymentTypes, "This application has deployment-type variants. A WSUS update carries one installer, so the variant set cannot be published as one update.");
        var installerType = manifest.InstallerType ?? "";
        if (!installerType.Equals("MSI", StringComparison.OrdinalIgnoreCase) && !installerType.Equals("EXE", StringComparison.OrdinalIgnoreCase))
            Add(WsusSeverity.Blocking, WsusRefusalCodes.InstallerTypeUnsupported, $"Installer type '{installerType}' cannot be published: WSUS installs Windows Installer packages and command-line executables only.");
        if (IsPerUser(manifest))
            Add(WsusSeverity.Blocking, WsusRefusalCodes.PerUserInstall, "This application installs for the signed-in user. WSUS installs as SYSTEM, so a per-user installer would land in the wrong profile.");
        if (manifest.RequireUserInteraction == true)
            Add(WsusSeverity.Blocking, WsusRefusalCodes.InteractiveInstall, "This deployment type lets the user interact with the installation. WSUS installs silently.");

        var setupFile = (manifest.SetupFile ?? "").Trim().Trim('"');
        var customInstall = manifest.CustomAssets?.Any(a => a is not null && (a.Category ?? "").StartsWith("Install", StringComparison.OrdinalIgnoreCase)) == true;
        var overrideInstall = !string.IsNullOrWhiteSpace(manifest.CommandOverrides?.Install);
        var installScript = hasContent ? Path.Combine(contentFolder!, "install.ps1") : null;
        if ((setupFile.Length > 0 && !setupFile.Equals("install.bat", StringComparison.OrdinalIgnoreCase)) || customInstall || overrideInstall)
        {
            Add(WsusSeverity.Blocking, WsusRefusalCodes.CustomInstall, "This build installs through a custom command or script. WSUS runs the vendor installer with its silent arguments, so the customization would not reach the client.");
        }
        else if (installScript is not null && File.Exists(installScript))
        {
            var extras = InstallScriptExtras(installScript);
            if (extras.Count > 0)
                Add(WsusSeverity.Blocking, WsusRefusalCodes.CustomInstall, $"install.ps1 does more than run the installer: {string.Join(", ", extras)}. WSUS runs only the installer with its arguments, so these steps would not reach the client.");
        }

        // The EXE wrappers turn the exit codes of the definition into 3010 (reboot required) and 0 (complete install). WSUS runs the
        // installer itself and knows only the table of the publisher, so any other code of the installer counts as a failed install.
        if (installerType.Equals("EXE", StringComparison.OrdinalIgnoreCase)
            && (manifest.RebootExitCodes is { Length: > 0 } || manifest.SuccessExitCodes is { Length: > 0 }))
        {
            var codes = new List<string>();
            if (manifest.RebootExitCodes is { Length: > 0 } reboot) codes.Add($"reboot required: {string.Join(", ", reboot)}");
            if (manifest.SuccessExitCodes is { Length: > 0 } success) codes.Add($"success: {string.Join(", ", success)}");
            var known = string.Join(", ", WsusPublishService.ExeReturnCodes.Select(c => c.Code));
            Add(WsusSeverity.Blocking, WsusRefusalCodes.ExitCodes,
                $"The installer reports exit codes of its own ({string.Join("; ", codes)}). WSUS knows only {known}, so a successful install would be recorded as failed.");
        }

        var installerFile = manifest.InstallerFile ?? "";
        var outside = new List<string>();
        if (hasContent)
        {
            foreach (var file in WsusPayload.Files(manifest).Where(f => !string.IsNullOrWhiteSpace(f)))
            {
                try { WsusPayload.ResolvePath(contentFolder!, file); }
                catch (WsusPayloadException) { outside.Add(file); }
            }
        }
        if (outside.Count > 0)
            Add(WsusSeverity.Blocking, WsusRefusalCodes.PayloadOutsideStage, $"The stage manifest names {string.Join(", ", outside.Select(f => "'" + f + "'"))}, which is not a file inside the content folder.");
        if (string.IsNullOrWhiteSpace(installerFile))
            Add(WsusSeverity.Blocking, WsusRefusalCodes.InstallerMissing, "The manifest names no installer file.");
        else if (outside.Contains(installerFile)) { }
        else if (hasContent && !File.Exists(Path.Combine(contentFolder!, installerFile)))
            Add(WsusSeverity.Blocking, WsusRefusalCodes.InstallerMissing, $"The installer '{installerFile}' is not in the staged content.");
        else if (installerFile.Contains('\\') || installerFile.Contains('/'))
            Add(WsusSeverity.Blocking, WsusRefusalCodes.InstallerInSubfolder, $"The installer '{installerFile}' sits in a subfolder; a WSUS update carries its files in one folder.");

        if (hasContent && findings.All(f => f.Severity != WsusSeverity.Blocking))
        {
            try
            {
                var built = rules.Build(manifest, contentFolder);
                if (built.Source == WsusRuleSource.MsiEntry)
                    Add(WsusSeverity.Info, WsusRefusalCodes.WsusRulesFromMsi, $"The detection cannot tell an older installed version apart, so WSUS reads the Add/Remove Programs entry that the MSI registers: {built.Entry!.Describe()}.");
            }
            catch (WsusRuleException ex)
            {
                Add(WsusSeverity.Blocking, WsusRefusalCodes.DetectionNotMappable, ex.Message);
            }

            var payload = WsusPayload.Files(manifest);
            var nested = payload.Skip(1).Where(f => f.Contains('\\')).ToList();
            if (nested.Count > 0)
                Add(WsusSeverity.Blocking, WsusRefusalCodes.PayloadInSubfolder, $"The staged content carries {string.Join(", ", nested)} in a subfolder; a WSUS update carries its files in one folder.");
            var missing = payload.Skip(1).Where(f => !File.Exists(WsusPayload.ResolvePath(contentFolder!, f))).ToList();
            if (missing.Count > 0)
                Add(WsusSeverity.Blocking, WsusRefusalCodes.PayloadMissing, $"The stage recorded {string.Join(", ", missing)}, which is no longer in the staged content; re-run the Stage phase.");
            var megabytes = WsusPayload.Megabytes(contentFolder!, payload);
            if (megabytes > WsusPayload.CabHardLimitMegabytes)
                Add(WsusSeverity.Blocking, WsusRefusalCodes.PayloadTooLarge, $"The installer payload is {megabytes:N0} MB. A locally published update is one signed cab, and a cab cannot exceed {WsusPayload.CabHardLimitMegabytes} MB.");
        }

        var requirements = manifest.Requirements?.Count(r => r is not null) ?? 0;
        if (requirements > 0)
            Add(WsusSeverity.Review, WsusRefusalCodes.RequirementsNotTranslated, $"This application carries {requirements} ConfigMgr requirement rule(s). The WSUS publisher does not translate them; approve the update only for computer groups that meet them.");
        var running = (manifest.RunningProcess ?? []).Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        if (running.Count > 0)
            Add(WsusSeverity.Review, WsusRefusalCodes.RunningProcessNotClosed, $"WSUS cannot close {string.Join(", ", running)} before the install; the installer's own handling of a running copy applies.");
        if (manifest.Timing is { EstimatedMinutes: not null } or { MaximumMinutes: not null })
            Add(WsusSeverity.Info, WsusRefusalCodes.TimingNotApplied, "Estimated and maximum runtime minutes are not sent to WSUS; the client applies its own installation timeout.");
        Add(WsusSeverity.Info, WsusRefusalCodes.UninstallNotPublished, "WSUS publishes the install only; removal stays with ConfigMgr, Intune, or the vendor uninstaller.");

        return findings;
    }

    /// <summary>
    /// The extra steps of a staged install.ps1: the token scan, plus the
    /// post-install configuration section the desktop wrapper writes behind its
    /// installer exit-code guard.
    /// </summary>
    public static IReadOnlyList<string> InstallScriptExtras(string path)
    {
        var text = File.ReadAllText(path);
        var extras = WsusInstallScriptScan.Scan(text).ToList();
        if (extras.Count != 1 || extras[0] != WsusInstallScriptScan.DoesNotParse)
        {
            if (PostInstallGuard().IsMatch(text) && !extras.Contains("post-install configuration"))
                extras.Add("post-install configuration");
        }
        return extras;
    }

    /// <summary>The codes of the blocking findings, in the order found.</summary>
    public static IReadOnlyList<string> BlockingCodes(IEnumerable<WsusFinding> findings) =>
        findings.Where(f => f.Severity == WsusSeverity.Blocking).Select(f => f.Code).ToList();

    private static bool IsPerUser(StageManifest manifest) =>
        manifest.InstallationBehaviorType is { } behavior
            ? behavior.Equals("InstallForUser", StringComparison.OrdinalIgnoreCase)
            : manifest.InstallContext.Equals("User", StringComparison.OrdinalIgnoreCase);
}
