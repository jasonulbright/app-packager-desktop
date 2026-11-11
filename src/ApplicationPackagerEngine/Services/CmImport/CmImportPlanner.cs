using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ApplicationPackagerEngine.Services.CmImport;

/// <summary>
/// Plans the Intune Win32 apps for a parsed ConfigMgr application: one app per deployment type.
/// ConfigMgr installs the first deployment type, by priority, whose requirements pass. Intune has
/// no deployment types and no priority, so the requirement rules of every app keep "first match
/// wins" (strategy PerDeploymentType), or each app carries only its own requirements and the
/// operator assigns groups (strategy AssignByGroup). Nothing is written.
/// </summary>
public static class CmImportPlanner
{
    public const string PerDeploymentType = "PerDeploymentType";
    public const string AssignByGroup = "AssignByGroup";

    // ---- commands, return codes, setup file -----------------------------

    public static IReadOnlyList<JsonObject> ReturnCodes(IReadOnlyList<CmExitCode>? exitCodes)
    {
        var typeMap = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Success"] = "success", ["SoftReboot"] = "softReboot", ["HardReboot"] = "hardReboot", ["FastRetry"] = "retry", ["Failure"] = "failed"
        };
        var codes = (exitCodes ?? []).Select(e => new JsonObject { ["returnCode"] = e.Code, ["type"] = typeMap.GetValueOrDefault(e.Class, "failed") }).ToList();
        if (codes.Count > 0) return codes;
        return
        [
            new JsonObject { ["returnCode"] = 0, ["type"] = "success" }, new JsonObject { ["returnCode"] = 1707, ["type"] = "success" },
            new JsonObject { ["returnCode"] = 3010, ["type"] = "softReboot" }, new JsonObject { ["returnCode"] = 1641, ["type"] = "hardReboot" },
            new JsonObject { ["returnCode"] = 1618, ["type"] = "retry" }
        ];
    }

    public static string RestartBehavior(string postInstallBehavior) => IntunePublishService.DeviceRestartBehavior(postInstallBehavior);

    private static string FirstToken(string commandLine)
    {
        var text = commandLine.Trim();
        if (text.StartsWith('"'))
        {
            var end = text.IndexOf('"', 1);
            if (end > 0) return text[1..end];
        }
        return Regex.Split(text, @"\s+", RegexOptions.None, TimeSpan.FromSeconds(2)).FirstOrDefault() ?? "";
    }

    /// <summary>
    /// The setup file the Intune content prep tool needs: the file the install command runs, or the MSI
    /// it installs. When the command names no file of the content, the first installer-like file at the
    /// root of the content is used. Empty when none fits.
    /// </summary>
    public static string SetupFile(string installCommand, IReadOnlyList<string> contentFiles)
    {
        var files = contentFiles.ToList();
        var lookup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files) lookup[file.Replace('/', '\\')] = file;

        string Find(string reference)
        {
            if (reference.Length == 0) return "";
            var relative = Regex.Replace(reference.Trim('"').Replace('/', '\\'), @"^(\.\\)+", "");
            if (lookup.TryGetValue(relative, out var exact)) return exact;
            var name = Path.GetFileName(relative);
            var sameName = files.Where(f => string.Equals(Path.GetFileName(f.Replace('/', '\\')), name, StringComparison.OrdinalIgnoreCase)).ToList();
            return name.Length > 0 && sameName.Count == 1 ? sameName[0] : "";
        }

        var found = Find(FirstToken(installCommand));
        if (found.Length > 0) return found;
        foreach (Match match in Regex.Matches(installCommand, @"(?i)""([^""]+\.(?:msi|msp|exe|bat|cmd|ps1|vbs))""|(\S+\.(?:msi|msp|exe|bat|cmd|ps1|vbs))"))
        {
            found = Find(match.Groups[1].Value + match.Groups[2].Value);
            if (found.Length > 0) return found;
        }
        foreach (var extension in new[] { ".msi", ".exe", ".bat", ".cmd", ".ps1" })
        {
            var hit = files.FirstOrDefault(f => f.IndexOfAny(['\\', '/']) < 0 && f.EndsWith(extension, StringComparison.OrdinalIgnoreCase));
            if (hit is not null) return hit;
        }
        return "";
    }

    // ---- the plan -------------------------------------------------------

    public static CmImportPlan Plan(CmApplication model, CmImportOptions? options = null)
    {
        options ??= new CmImportOptions();
        var dts = model.DeploymentTypes;
        var isMulti = dts.Count > 1;
        var requirementModels = dts.Select(d => CmRequirementConverter.Convert(d.Requirements)).ToList();
        var appId = Regex.Replace(model.LogicalName, "^.*/", "");
        var relationships = new List<CmRelationship>();
        var plans = new List<CmDeploymentTypePlan>();
        var byGroup = options.VariantStrategy == AssignByGroup;

        for (var i = 0; i < dts.Count; i++)
        {
            var dt = dts[i];
            var req = requirementModels[i];
            var findings = new List<CmFinding>(req.Findings);
            void Add(string severity, string code, string message) => findings.Add(new CmFinding(severity, code, message));

            var intuneName = isMulti ? $"{model.Name} - {dt.Name}" : model.Name;
            var blocked = false;

            if (dt.UnsupportedReason.Length > 0)
            {
                Add("Blocking", "TechnologyNotConverted", dt.UnsupportedReason);
                blocked = true;
            }

            CmDetectionResult? detection = null;
            if (!blocked)
            {
                if (dt.Detection is null)
                {
                    Add("Blocking", "DetectionMissing", "The deployment type stores no detection.");
                    blocked = true;
                }
                else
                {
                    detection = CmDetectionConverter.Convert(dt.Detection, options.NativeDetectionOnly);
                    findings.AddRange(detection.Findings);
                    if (detection.Findings.Any(f => f.Severity == "Blocking")) blocked = true;
                }
            }

            var exclusion = CmExclusion.None;
            if (isMulti && i > 0)
            {
                exclusion = CmRequirementConverter.Exclusion(req, requirementModels.Take(i).ToList());
                switch (exclusion.Status)
                {
                    case "Unreachable":
                        Add("Review", "UnreachableDeploymentType", exclusion.Reason + " ConfigMgr never installs this deployment type, so it is not converted.");
                        blocked = true;
                        break;
                    case "Unprovable" when !byGroup:
                        Add("Blocking", "ExclusivityUnprovable", exclusion.Reason +
                            " The app would install on devices where ConfigMgr picks an earlier variant. Use the AssignByGroup strategy and assign each app to its own group.");
                        blocked = true;
                        break;
                }
            }
            if (isMulti && byGroup)
            {
                Add("Review", "AssignByGroupRequired", "This app does not exclude the other variants. Assign each variant app to its own device group.");
                exclusion = CmExclusion.None;
            }

            var rules = new List<JsonObject>();
            var nativeMinimums = new Dictionary<string, int>(StringComparer.Ordinal);
            var allowedArchitectures = "";
            if (!blocked)
            {
                rules.AddRange(detection!.Rules);
                foreach (var (key, value) in req.Native) nativeMinimums[key] = value;
                if (req.ArchitectureConstrained && (req.Architectures?.Count ?? 0) == 0)
                {
                    Add("Blocking", "ArchitectureConflict", "The operating system requirements share no architecture. ConfigMgr never installs this deployment type.");
                    blocked = true;
                }
                else if (req.ArchitectureConstrained)
                {
                    allowedArchitectures = string.Join(",", req.Architectures!);
                }
                else
                {
                    // Intune needs an architecture. ConfigMgr named none, so both Windows architectures that ran the deployment type are allowed.
                    allowedArchitectures = "x86,x64";
                    Add("Info", "ArchitectureNotConstrained", "The deployment type names no architecture. The Intune app allows x86 and x64.");
                }
                var architectureSet = allowedArchitectures.Split(',', StringSplitOptions.RemoveEmptyEntries).Distinct().OrderBy(a => a, StringComparer.OrdinalIgnoreCase).ToList();
                if (architectureSet.Contains("arm64"))
                    Add("Review", "Arm64ValueUnverified", "The Graph v1.0 metadata lists the architectures x86, x64 and arm for allowedArchitectures; the documentation lists arm64. Check the published app on an ARM64 device.");
                if (architectureSet.Count > 1 && !(architectureSet.Count == 2 && architectureSet.Contains("x86") && architectureSet.Contains("x64")))
                    Add("Review", "ArchitectureCombinationUnverified",
                        $"Graph documents one architecture for allowedArchitectures, or x86 with x64 for applicableArchitectures. The combination {string.Join(" and ", architectureSet)} is sent as allowedArchitectures. Check the published app.");

                if (req.ScriptOnly.Count > 0 || exclusion.Status == "Script")
                {
                    var text = CmRequirementConverter.VariantScript(req.ScriptOnly, exclusion.EarlierSets);
                    rules.Add(CmDetectionConverter.ScriptRule(text, "requirement", "Variant selection"));
                    if (exclusion.Status == "Script")
                        Add("Info", "VariantExclusionScript", "A requirement script keeps this app off devices where a higher-priority deployment type applies.");
                }
                if (req.Unrecognized.Count > 0)
                {
                    if (options.AllowUntranslatedRequirements)
                        Add("Review", "RequirementsNotTranslated", $"{req.Unrecognized.Count} requirement rule(s) have no Intune equivalent. The Intune app does not gate on them, so it can install on devices where ConfigMgr skips this deployment type.");
                    else
                    {
                        Add("Blocking", "RequirementsNotTranslated", $"{req.Unrecognized.Count} requirement rule(s) have no Intune equivalent. The Intune app would install on devices where ConfigMgr skips this deployment type. Use -AllowUntranslatedRequirements to convert it without them.");
                        blocked = true;
                    }
                }
                if (req.Predicates.OfType<CmOsPlatformPredicate>().Any(p => p.Pairs.Any(x => x.MinimumBuild < 19044)))
                    Add("Info", "WindowsFloorRaised", "ConfigMgr allowed Windows 10 releases that Microsoft no longer supports. The Intune app gets the minimum Windows release of the Intune setting (default Windows10_21H2).");
            }

            var installCommand = dt.InstallCommand;
            var uninstallCommand = dt.UninstallCommand;
            if (!blocked && installCommand.Length == 0)
            {
                Add("Blocking", "InstallCommandMissing", "The deployment type stores no install command.");
                blocked = true;
            }
            if (uninstallCommand.Length == 0 || dt.UninstallSetting == "NoneRequired")
            {
                uninstallCommand = "cmd.exe /c exit 0";
                if (!blocked) Add("Review", "NoUninstallCommand", "The deployment type has no uninstall command. The Intune app uses a command that does nothing and reports success.");
            }
            if (dt.UninstallContentId.Length > 0 && dt.ContentId.Length > 0 && dt.UninstallContentId != dt.ContentId && !blocked)
                Add("Review", "UninstallContentDiffers", "The uninstall command uses different content than the install command. Only the install content is copied.");
            if (dt.RunAs32Bit && !blocked)
                Add("Info", "RunAs32BitNotMapped", "The install command runs as a 32-bit process in ConfigMgr. Intune starts it as a 64-bit process on 64-bit devices.");
            var systemInteractive = dt.RequiresUserInteraction && dt.ExecutionContext != "User";
            if (dt.RequiresUserInteraction && dt.ExecutionContext == "User" && !blocked)
                Add("Review", "UserInteractionPossible", "The deployment type lets the user see and interact with the install. Intune runs it in the user context and shows what the installer shows.");
            if (dt.RequiresLogOn == "True" && dt.ExecutionContext != "User" && !blocked)
                Add("Review", "LogonRequirementNotMapped", "The deployment type installs only when a user is logged on, with system rights. Intune installs without that condition.");
            if (dt.MaxRuntimeMins > 0 && !blocked)
                Add("Info", "TimingNotApplied", $"The maximum run time ({dt.MaxRuntimeMins} minutes) is recorded in the manifest. Graph v1.0 has no property for it.");

            var setupFile = "";
            if (!blocked)
            {
                setupFile = SetupFile(installCommand, dt.ContentFiles);
                if (setupFile.Length == 0)
                {
                    Add("Blocking", "SetupFileNotFound", "The content holds no file that the install command runs, and no installer-like file at its root. The Intune content prep tool needs a setup file.");
                    blocked = true;
                }
            }

            // Relationships are reported, never created: Graph v1.0 has no resource for them.
            foreach (var (groups, type) in new[] { (dt.Dependencies, "Dependency"), (dt.Supersedes, "Supersedence") })
                foreach (var group in groups)
                    foreach (var target in group.Targets)
                        relationships.Add(new CmRelationship
                        {
                            Type = type, FromDeploymentType = dt.Name, FromApp = intuneName, GroupName = group.GroupName, GroupOperator = group.Operator,
                            DesiredState = target.DesiredState, TargetApplication = target.ApplicationLogical, TargetDeploymentType = target.DeploymentTypeLogical,
                            TargetApplicationName = target.ApplicationName, TargetDeploymentTypeName = target.DeploymentTypeName
                        });

            CmIntuneManifest? manifest = null;
            if (!blocked)
            {
                var architectureValue = allowedArchitectures.Length > 0 && !allowedArchitectures.Contains(',') ? allowedArchitectures : "";
                manifest = new CmIntuneManifest
                {
                    AppName = intuneName, Publisher = model.Publisher, SoftwareVersion = model.Version, Description = model.Description,
                    ApplicationId = "cm:" + appId,
                    ProfileId = Regex.Replace(dt.LogicalName, "^.*/", ""),
                    InstallCommandLine = installCommand, UninstallCommandLine = uninstallCommand, SetupFile = setupFile,
                    InstallationBehaviorType = dt.ExecutionContext == "User" ? "InstallForUser" : "InstallForSystem",
                    RequireUserInteraction = systemInteractive,
                    Architecture = architectureValue,
                    Timing = new CmTiming(dt.EstimatedRuntimeMins, dt.MaxRuntimeMins, false),
                    IntuneRules = rules,
                    IntuneReturnCodes = ReturnCodes(dt.ExitCodes),
                    IntuneRestartBehavior = RestartBehavior(dt.PostInstallBehavior),
                    IntuneMinimums = nativeMinimums,
                    IntuneAllowedArchitectures = allowedArchitectures,
                    Source = new CmSource(model.LogicalName, dt.LogicalName, dt.Priority, dt.ContentLocation)
                };
            }

            plans.Add(new CmDeploymentTypePlan
            {
                Name = dt.Name, LogicalName = dt.LogicalName, Priority = dt.Priority, Technology = dt.Technology, IntuneName = intuneName,
                Convert = !blocked, ContentLocation = dt.ContentLocation, ContentFiles = dt.ContentFiles,
                Exclusion = exclusion.Status, DetectionMode = detection?.Mode ?? "", Manifest = manifest, Findings = findings
            });
        }

        var appFindings = new List<CmFinding>();
        if (relationships.Count > 0)
            appFindings.Add(new CmFinding("Review", "RelationshipsNotCreated",
                $"{relationships.Count} dependency and supersedence relationship(s) are listed in relationships.json. Microsoft Graph v1.0 has no resource for them, and App Packager calls v1.0 only. Create them in the Intune admin center."));
        if (isMulti)
            appFindings.Add(new CmFinding("Info", "VariantApps",
                $"The application has {dts.Count} deployment types. Each becomes its own Intune app (strategy {options.VariantStrategy})."));

        return new CmImportPlan
        {
            ApplicationName = model.Name, Publisher = model.Publisher, Version = model.Version, LogicalName = model.LogicalName,
            Strategy = options.VariantStrategy, DeploymentTypes = plans, Relationships = relationships, Findings = appFindings
        };
    }

    /// <summary>The text of conversion-report.txt.</summary>
    public static string Report(CmImportPlan plan)
    {
        var lines = new List<string>
        {
            string.Create(CultureInfo.InvariantCulture, $"ConfigMgr application : {plan.ApplicationName} {plan.Version}"),
            $"Variant strategy      : {plan.Strategy}"
        };
        lines.AddRange(plan.Findings.Select(f => $"[{f.Severity}] {f.Code}: {f.Message}"));
        foreach (var dt in plan.DeploymentTypes)
        {
            lines.Add("");
            lines.Add($"Deployment type {dt.Priority}: {dt.Name} ({dt.Technology}; {(dt.Convert ? "converted" : "not converted")})");
            if (dt.Convert) lines.Add($"  Intune app          : {dt.IntuneName}");
            if (dt.DetectionMode.Length > 0) lines.Add($"  Detection           : {dt.DetectionMode}");
            lines.AddRange(dt.Findings.Select(f => $"  [{f.Severity}] {f.Code}: {f.Message}"));
        }
        return string.Join("\r\n", lines);
    }
}
