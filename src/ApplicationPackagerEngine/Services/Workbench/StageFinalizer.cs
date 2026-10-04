using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Models.Signing;
using ApplicationPackagerEngine.Models.Workbench;
using ApplicationPackagerEngine.Services.Signing;

namespace ApplicationPackagerEngine.Services.Workbench;

/// <summary>
/// Applies one build's run snapshot and signing policy to a staged content folder.
/// A stage creates one finalizer before it writes anything, so an unusable signing
/// certificate refuses the build with no partial content; it then applies the
/// finalizer after the icon lands and before the manifest is written and hashed.
/// Without a run snapshot only the ScriptSigning record is added; with every
/// signing switch off nothing under the stage root changes.
/// </summary>
public sealed class StageFinalizer
{
    private static readonly JsonSerializerOptions ReadOptions = new() { PropertyNameCaseInsensitive = true };
    private static readonly UTF8Encoding Utf8NoBom = new(false);
    private static readonly string[] Sections = ["Install", "Uninstall"];
    private static readonly string[] LauncherFiles = ["install.ps1", "uninstall.ps1", "install.bat", "uninstall.bat"];
    private static readonly string[] IconExtensions = [".ico", ".png", ".jpg", ".jpeg"];
    private static readonly SettingSource[] OverrideSources =
        [SettingSource.Profile, SettingSource.Variant, SettingSource.Target, SettingSource.Run];

    private readonly ScriptSigningService? _signer;

    private StageFinalizer(RunSnapshot? snapshot, SigningPolicy policy, string scriptHost, ScriptSigningService? signer)
    {
        Snapshot = snapshot;
        Policy = policy;
        ScriptHost = scriptHost;
        _signer = signer;
    }

    /// <summary>The run snapshot this build applies; null for a build without a profile.</summary>
    public RunSnapshot? Snapshot { get; }

    /// <summary>The normalized policy in force: the snapshot's own, else the global one, else all off.</summary>
    public SigningPolicy Policy { get; }

    /// <summary>x64 or x86, from the profile's Execution.ScriptHost or a run override.</summary>
    public string ScriptHost { get; }

    /// <summary>The templates requirement scripts resolve from; null reads the configured templates file.</summary>
    public ConditionTemplateDocument? ConditionTemplates { get; set; }

    /// <summary>
    /// Starts one build. The signing certificate resolves here whenever a Sign or
    /// Require switch is on, so a stage calls this before it writes anything.
    /// </summary>
    public static StageFinalizer Begin(StageInputs? inputs)
    {
        var snapshot = inputs?.RunSnapshot;
        var policy = EffectivePolicy(snapshot, inputs?.SigningPolicy);
        if (policy.AnySwitch)
            (inputs?.SigningGate ?? new ScriptSigningCertificateGate()).ResolveCertificate(JsonSerializer.SerializeToNode(policy)!);
        return new StageFinalizer(snapshot, policy, ResolveScriptHost(snapshot), inputs?.Signer);
    }

    /// <summary>The policy a run snapshot carries, else the global policy, normalized.</summary>
    public static SigningPolicy EffectivePolicy(RunSnapshot? snapshot, SigningPolicy? globalPolicy) =>
        snapshot?.SigningPolicy is JsonObject node ? SigningPolicy.FromJsonNode(node) : SigningPolicy.Normalize(globalPolicy);

    /// <summary>True when a run snapshot finalized the manifest; operator settings saved outside the profile then do not apply.</summary>
    public static bool CarriesBuild(StageManifest manifest) => !string.IsNullOrWhiteSpace(manifest.BuildId);

    private static string ResolveScriptHost(RunSnapshot? snapshot)
    {
        if (snapshot is null) return DeploymentLauncher.HostX64;
        var settings = EffectiveSettingsResolver.Resolve(profile: Profile(snapshot), runOverrides: snapshot.RunOverrides);
        var host = WorkbenchJson.AsText(settings["ScriptHost"].Value);
        if (string.IsNullOrWhiteSpace(host)) return DeploymentLauncher.HostX64;
        if (string.Equals(host, DeploymentLauncher.HostX86, StringComparison.OrdinalIgnoreCase)) return DeploymentLauncher.HostX86;
        if (string.Equals(host, DeploymentLauncher.HostX64, StringComparison.OrdinalIgnoreCase)) return DeploymentLauncher.HostX64;
        throw new InvalidOperationException($"Unsupported script host '{host}'. Use x64 or x86.");
    }

    /// <summary>The local staging root: &lt;root&gt;\profiles\&lt;ProfileId&gt; for a non-default profile.</summary>
    public string ScopeDownloadRoot(string downloadRoot) =>
        ProfileContentScope.LocalDownloadRoot(downloadRoot, Snapshot?.ProfileId);

    /// <summary>The wrapper generator for this build: signed launchers under SignDeployment and the profile's script host.</summary>
    public WrapperGeneratorService Wrappers(WrapperGeneratorService shared) =>
        !Policy.SignDeployment && ScriptHost == DeploymentLauncher.HostX64
            ? shared
            : new WrapperGeneratorService { SignedLaunchers = Policy.SignDeployment, ScriptHost = ScriptHost };

    /// <summary>
    /// The .bat body ODT staging writes for one script. Unsigned on the x64 host it
    /// is the fixed ODT launcher; otherwise it is the deployment launcher.
    /// </summary>
    public string OdtLauncher(string script) =>
        !Policy.SignDeployment && ScriptHost == DeploymentLauncher.HostX64
            ? $"@echo off\r\npowershell.exe -NoProfile -ExecutionPolicy Bypass -File \"%~dp0{script}\"\r\nexit /b %ERRORLEVEL%"
            : DeploymentLauncher.Build(script, Policy.SignDeployment, ScriptHost).BatBody;

    /// <summary>
    /// Run before the wrappers are generated. The generator keeps an existing
    /// wrapper, so a stage root reused by a profiled or signed build, or by a build
    /// after one, would otherwise keep the previous build's launchers, Extend
    /// orchestrator or signatures. The generated launchers are removed so they are
    /// written fresh; under an all-off policy a previous build's scripts\ goes too.
    /// A build with no profile and no signing over a folder that never carried
    /// either leaves the stage root unchanged.
    /// </summary>
    public void PrepareStageRoot(string stageRoot)
    {
        if (!Directory.Exists(stageRoot)) return;
        var previous = ReadNode(Path.Combine(stageRoot, "stage-manifest.json"));
        var previousBuild = WorkbenchJson.Get(previous, "BuildId").Text;
        var previousDigest = WorkbenchJson.Get(previous, "ScriptSigning.PolicyDigest").Text;
        var policyChanged = !string.IsNullOrEmpty(previousDigest)
            && !string.Equals(previousDigest, Policy.PolicyDigest, StringComparison.OrdinalIgnoreCase);
        if (Snapshot is null && !Policy.AnySwitch && string.IsNullOrEmpty(previousBuild) && !policyChanged) return;

        foreach (var name in LauncherFiles)
        {
            var path = Path.Combine(stageRoot, name);
            if (File.Exists(path)) File.Delete(path);
        }
        var scripts = Path.Combine(stageRoot, "scripts");
        if (!Policy.AnySwitch && Directory.Exists(scripts)) Directory.Delete(scripts, recursive: true);
    }

    /// <summary>
    /// Finalizes, writes and verifies the manifest, then seals the build record.
    /// This is the one step every stage path runs in place of a bare manifest write.
    /// </summary>
    public void FinalizeAndWrite(ManifestService manifestService, string manifestPath, StageManifest manifest, Action<string>? onLog = null)
    {
        var stageRoot = Path.GetDirectoryName(Path.GetFullPath(manifestPath))!;
        Apply(stageRoot, manifest, onLog);
        manifestService.Write(manifestPath, manifest);
        var record = Seal(stageRoot, manifest);
        if (record is not null) onLog?.Invoke($"Build record sealed          : {WorkbenchJson.Get(record, "Path").Text}");
    }

    /// <summary>
    /// Applies the snapshot's profile in a fixed order: install and uninstall script
    /// modes and hooks, commands, detection, requirement operations, variant
    /// overrides, timing and execution, icon, title mode, source files (after every
    /// generated file exists, so a collision is real), the setup file, and signing
    /// last so nothing edits a signed file afterwards. Without a snapshot only the
    /// signing record is added.
    /// </summary>
    public void Apply(string stageRoot, StageManifest manifest, Action<string>? onLog = null)
    {
        var snapshot = Snapshot;
        if (snapshot is null)
        {
            manifest.ScriptSigning = Sign(stageRoot, manifest);
            return;
        }

        var profile = Profile(snapshot);
        var applicationId = snapshot.ApplicationId;
        var profileId = string.IsNullOrWhiteSpace(snapshot.ProfileId) ? ApplicationIds.DefaultProfileId : snapshot.ProfileId;
        var dataRoot = string.IsNullOrWhiteSpace(snapshot.DataRoot) ? WorkbenchPaths.ResolveDataRoot() : snapshot.DataRoot;
        var store = new WorkbenchStore(dataRoot);
        var customAssets = new List<StageCustomAsset>();

        manifest.BuildId = snapshot.BuildId;
        manifest.ApplicationId = applicationId;
        manifest.ProfileId = profileId;
        manifest.ProfileRevision = snapshot.ProfileRevision;
        manifest.RunOverrides = WorkbenchJson.CloneObject(snapshot.RunOverrides ?? []);

        // The stage folder is reused for the same vendor version, so everything the
        // previous build of this profile owned goes before the new content lands.
        foreach (var section in Sections)
        {
            if (string.Equals(WorkbenchJson.Get(profile, section + ".Mode").Text, "Extend", StringComparison.OrdinalIgnoreCase)) continue;
            var stale = Path.Combine(stageRoot, section.ToLowerInvariant() + "-generated.ps1");
            if (File.Exists(stale)) File.Delete(stale);
        }
        StaleStageArtifacts.Prune(stageRoot, dataRoot, applicationId, profileId, snapshot.BuildId, onLog);

        string ResolveAsset(string assetId)
        {
            var match = snapshot.AssetPaths.FirstOrDefault(p => string.Equals(p.Key, assetId, StringComparison.OrdinalIgnoreCase));
            return !string.IsNullOrWhiteSpace(match.Value) ? match.Value : store.ResolveAssetPath(applicationId, profileId, assetId);
        }

        var settings = EffectiveSettingsResolver.Resolve(null, JsonSerializer.SerializeToNode(manifest), profile, null, null, snapshot.RunOverrides);

        // Script modes run before commands resolve: Extend renames the generated
        // script, and the orchestrator it writes calls the renamed file by name.
        foreach (var section in Sections)
        {
            var block = WorkbenchJson.Get(profile, section).Value;
            if (!WorkbenchJson.IsTruthy(block)) continue;
            var mode = WorkbenchJson.Get(block, "Mode").Text;
            var lower = section.ToLowerInvariant();
            var target = lower + ".ps1";

            // A script step is a managed asset id (Script, Before, After) or text
            // stored in the profile (ScriptText, BeforeText, AfterText); the asset wins.
            (string? AssetId, string Text)? StepSource(string field)
            {
                var assetId = WorkbenchJson.Get(block, field).Text;
                if (!string.IsNullOrWhiteSpace(assetId)) return (assetId, File.ReadAllText(ResolveAsset(assetId)));
                var inline = WorkbenchJson.Get(block, field + "Text").Text;
                if (string.IsNullOrWhiteSpace(inline)) return null;
                return (null, inline);
            }

            if (string.Equals(mode, "Custom", StringComparison.OrdinalIgnoreCase))
            {
                var source = StepSource("Script") ?? throw new InvalidOperationException($"{section}.Mode Custom requires a script.");
                var text = WorkbenchTokens.Resolve(source.Text, manifest, TokenContext.Script);
                File.WriteAllText(Path.Combine(stageRoot, target), text, Utf8NoBom);
                customAssets.Add(new StageCustomAsset { Category = section, RelativePath = target, Asset = source.AssetId });
            }
            else if (string.Equals(mode, "Extend", StringComparison.OrdinalIgnoreCase))
            {
                var generatedPath = Path.Combine(stageRoot, target);
                if (!File.Exists(generatedPath))
                    throw new InvalidOperationException($"{section}.Mode Extend needs the packager's generated {target}, which the stage did not produce.");
                var generatedName = lower + "-generated.ps1";
                File.Move(generatedPath, Path.Combine(stageRoot, generatedName), overwrite: true);

                var steps = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var hook in new[] { "Before", "After" })
                {
                    if (StepSource(hook) is not { } source) continue;
                    var name = $"{lower}-{hook.ToLowerInvariant()}.ps1";
                    var text = WorkbenchTokens.Resolve(source.Text, manifest, TokenContext.Script);
                    File.WriteAllText(Path.Combine(stageRoot, name), text, Utf8NoBom);
                    steps[hook] = name;
                    customAssets.Add(new StageCustomAsset { Category = section + hook, RelativePath = name, Asset = source.AssetId });
                }

                var orchestrator = ExtendOrchestrator.Build(
                    steps.GetValueOrDefault("Before"), generatedName, steps.GetValueOrDefault("After"),
                    WorkbenchJson.IsTruthy(WorkbenchJson.Get(block, "AfterRunsOnFailure").Value),
                    Policy.SignDeployment, ScriptHost);
                File.WriteAllText(Path.Combine(stageRoot, target), orchestrator, Encoding.ASCII);
                customAssets.Add(new StageCustomAsset { Category = section + "Orchestrator", RelativePath = target, Asset = null });
            }

            foreach (var field in new[] { "ReturnCodes", "RebootPolicy", "WorkingDirectory" })
            {
                var member = WorkbenchJson.Get(block, field);
                if (!member.Found) continue;
                var value = WorkbenchJson.Clone(member.Value);
                switch (section + field)
                {
                    case "InstallReturnCodes": manifest.InstallReturnCodes = value; break;
                    case "InstallRebootPolicy": manifest.InstallRebootPolicy = value; break;
                    case "InstallWorkingDirectory": manifest.InstallWorkingDirectory = value; break;
                    case "UninstallReturnCodes": manifest.UninstallReturnCodes = value; break;
                    case "UninstallRebootPolicy": manifest.UninstallRebootPolicy = value; break;
                    case "UninstallWorkingDirectory": manifest.UninstallWorkingDirectory = value; break;
                }
            }
        }

        // Commands bind tokens against the packager's own InstallerFile, version and ProductCode.
        foreach (var (field, install) in new[] { ("InstallCommand", true), ("UninstallCommand", false) })
        {
            var resolved = settings[field];
            var text = WorkbenchJson.AsText(resolved.Value);
            if (!OverrideSources.Contains(resolved.Source) || string.IsNullOrWhiteSpace(text)) continue;
            manifest.CommandOverrides ??= new StageCommandOverrides();
            var bound = WorkbenchTokens.Resolve(text, manifest, TokenContext.Command);
            if (install) manifest.CommandOverrides.Install = bound;
            else manifest.CommandOverrides.Uninstall = bound;
        }

        if (string.Equals(WorkbenchJson.Get(profile, "Detection.Mode").Text, "Custom", StringComparison.OrdinalIgnoreCase))
        {
            var rule = WorkbenchJson.Get(profile, "Detection.Rule").Value;
            if (rule is not JsonObject) throw new InvalidOperationException("Detection.Mode Custom requires a Detection.Rule.");
            manifest.Detection = rule.Deserialize<StageDetection>(ReadOptions)!;
            manifest.DetectionSource = "Custom";
        }
        var conversion = WorkbenchJson.Get(profile, "Detection.IntuneScriptConversion");
        if (conversion.Found && conversion.Value is not null)
            manifest.Detection.IntuneScriptConversion = WorkbenchJson.IsTruthy(conversion.Value);
        else
            manifest.Detection.IntuneScriptConversion ??= false;

        var operations = WorkbenchJson.Get(profile, "Requirements.Operations").Value;
        if (WorkbenchProfileValidator.Items(operations).Any())
            RequirementOperations.Apply(manifest, operations);

        // Variant overrides, after requirements, so a requirement pass never clobbers them.
        if (WorkbenchJson.Get(profile, "Variants.Overrides").Value is JsonObject variantOverrides && manifest.DeploymentTypes is { Length: > 0 })
        {
            foreach (var entry in manifest.DeploymentTypes)
            {
                if (!variantOverrides.TryGetPropertyValue(entry.NameSuffix, out var variant) || !WorkbenchJson.IsTruthy(variant)) continue;
                ApplyVariant(entry, variant!, manifest);
            }
        }

        int? estimated = null, maximum = null;
        foreach (var field in new[] { "EstimatedMinutes", "MaximumMinutes" })
        {
            var resolved = settings[field];
            if (resolved.Source == SettingSource.NeedsStaging || resolved.Source == SettingSource.Packager || resolved.Value is null) continue;
            var minutes = WorkbenchJson.AsInt(resolved.Value)
                ?? throw new InvalidOperationException($"{field} must be a positive whole number of minutes (got '{WorkbenchJson.AsText(resolved.Value)}').");
            if (field == "EstimatedMinutes") estimated = minutes; else maximum = minutes;
        }
        if (estimated is not null && maximum is not null && estimated > maximum)
            throw new InvalidOperationException($"Estimated runtime ({estimated}) cannot exceed maximum runtime ({maximum}).");
        manifest.Timing = new StageTiming { EstimatedMinutes = estimated, MaximumMinutes = maximum, IntuneApplied = false };

        var context = WorkbenchJson.AsText(settings["Context"].Value);
        var logon = WorkbenchJson.AsText(settings["LogonRequirement"].Value);
        var interaction = WorkbenchJson.Clone(settings["UserInteraction"].Value);
        manifest.Execution = new StageExecution
        {
            Context = context,
            LogonRequirement = logon,
            UserInteraction = interaction,
            ScriptHost = WorkbenchJson.AsText(settings["ScriptHost"].Value)
        };
        if (context is not null) manifest.InstallationBehaviorType = context;
        if (logon is not null) manifest.LogonRequirementType = logon;
        if (interaction is not null) manifest.RequireUserInteraction = WorkbenchJson.IsTruthy(interaction);

        // Every staged app-icon.* goes first: an icon with another extension would otherwise ship beside it.
        var iconAsset = WorkbenchJson.Get(profile, "Application.Icon.Asset").Text;
        var iconMember = WorkbenchJson.Get(profile, "Application.Icon");
        if (!string.IsNullOrWhiteSpace(iconAsset))
        {
            var source = ResolveAsset(iconAsset);
            var extension = Path.GetExtension(source).ToLowerInvariant();
            if (!IconExtensions.Contains(extension))
                throw new InvalidOperationException($"Custom icon '{source}' is not an .ico, .png or .jpg file.");
            RemoveStagedIcons(stageRoot);
            var name = "app-icon" + extension;
            File.Copy(source, Path.Combine(stageRoot, name), overwrite: true);
            manifest.Icon = name;
            customAssets.Add(new StageCustomAsset { Category = "Icon", RelativePath = name, Asset = iconAsset });
        }
        else if (iconMember.Found && iconMember.Value is null)
        {
            RemoveStagedIcons(stageRoot);
            manifest.Icon = null;
        }

        // A stored TitleMode can hold the title combo display text instead of the mode name.
        var titleMode = PackagerChildBridge.NormalizeTitleMode(WorkbenchJson.AsText(settings["TitleMode"].Value));
        var canonicalTitle = new[] { ApplicationTitle.IncludeVersionToken, ApplicationTitle.NoVersionToken }
            .FirstOrDefault(t => string.Equals(t, titleMode, StringComparison.OrdinalIgnoreCase));
        if (canonicalTitle is not null)
        {
            manifest.TitleMode = canonicalTitle;
            manifest.AppName = ApplicationTitle.Resolve(manifest.AppName, manifest.SoftwareVersion, ApplicationTitle.Parse(canonicalTitle));
        }

        foreach (var entry in WorkbenchProfileValidator.Items(profile["SourceFiles"]))
        {
            if (entry is null) continue;
            var destination = WorkbenchJson.Get(entry, "Destination").Text ?? "";
            var linked = WorkbenchJson.IsTruthy(WorkbenchJson.Get(entry, "Linked").Value);
            var assetId = WorkbenchJson.Get(entry, "Asset").Text;
            var key = linked ? "linked:" + WorkbenchJson.Get(entry, "Provenance").Text : assetId ?? "";
            var frozen = snapshot.AssetPaths.FirstOrDefault(p => string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase)).Value;
            var source = !string.IsNullOrWhiteSpace(frozen) ? frozen : store.ResolveAssetPath(applicationId, profileId, entry);
            var written = StageFileCopier.Copy(stageRoot, source, destination, WorkbenchJson.FileSha256(source));
            customAssets.Add(new StageCustomAsset
            {
                Category = "SourceFile",
                RelativePath = destination,
                Asset = assetId,
                Sha256 = WorkbenchJson.FileSha256(written),
                Size = new FileInfo(written).Length,
                Linked = linked
            });
        }

        manifest.CustomAssets = [.. customAssets];
        manifest.SetupFile = SetupFileResolver.Resolve(stageRoot, manifest);

        manifest.ScriptSigning = Sign(stageRoot, manifest);
        onLog?.Invoke($"Workbench profile applied    : {applicationId} / {profileId} rev {manifest.ProfileRevision}");
    }

    /// <summary>Seals the build record of a build that applied a run snapshot; null otherwise.</summary>
    public JsonObject? Seal(string stageRoot, StageManifest manifest)
    {
        if (Snapshot is null) return null;
        var dataRoot = string.IsNullOrWhiteSpace(Snapshot.DataRoot) ? WorkbenchPaths.ResolveDataRoot() : Snapshot.DataRoot;
        return BuildRecords.Write(dataRoot, stageRoot, manifest);
    }

    /// <summary>
    /// Signs the stage under a policy with any switch on, otherwise records it
    /// unsigned without touching it, and adds the launcher findings.
    /// </summary>
    private JsonNode Sign(string stageRoot, StageManifest manifest)
    {
        var templates = ConditionTemplates ?? new ConditionTemplateService().Load();
        var input = ScriptSigningInput.FromManifest(manifest, templates, manifest.Requirements);
        var signer = _signer ?? new ScriptSigningService();
        var record = Policy.AnySwitch
            ? ScriptSigningOrchestrator.SignStage(stageRoot, input, Policy, signer)
            : ScriptSigningOrchestrator.DescribeStage(stageRoot, input, Policy, signer);
        var chain = DeploymentLauncher.AuditChain(stageRoot, input.Commands, Policy, input.ThirdPartyScripts);

        var rootFull = Path.GetFullPath(stageRoot).TrimEnd('\\');
        var findings = new JsonArray();
        foreach (var finding in chain.Findings)
        {
            var file = finding.File.StartsWith(rootFull + "\\", StringComparison.OrdinalIgnoreCase)
                ? finding.File[(rootFull.Length + 1)..]
                : finding.File;
            findings.Add(new JsonObject
            {
                ["Code"] = finding.Code,
                ["File"] = file,
                ["LineNumber"] = finding.LineNumber,
                ["Line"] = finding.Line,
                ["Message"] = finding.Message
            });
        }
        var node = record.ToJsonNode().AsObject();
        node["LauncherFindings"] = findings;
        node["LaunchersBypassFree"] = chain.BypassFree;
        return node;
    }

    private static void ApplyVariant(StageDeploymentType entry, JsonNode variant, StageManifest manifest)
    {
        string? Command(JsonNode? value)
        {
            var text = WorkbenchJson.AsText(value);
            return string.IsNullOrWhiteSpace(text) ? text : WorkbenchTokens.Resolve(text, manifest, TokenContext.Command);
        }

        var member = WorkbenchJson.Get(variant, "InstallCommand");
        if (member.Found) entry.InstallCommandLine = Command(member.Value);
        member = WorkbenchJson.Get(variant, "UninstallCommand");
        if (member.Found) entry.UninstallCommandLine = Command(member.Value);
        member = WorkbenchJson.Get(variant, "Detection");
        if (member.Found) entry.Detection = member.Value is JsonObject rule ? rule.Deserialize<StageDetection>(ReadOptions) : null;
        member = WorkbenchJson.Get(variant, "Context");
        if (member.Found) entry.InstallationBehaviorType = WorkbenchJson.AsText(member.Value);
        member = WorkbenchJson.Get(variant, "LogonRequirement");
        if (member.Found) entry.LogonRequirementType = WorkbenchJson.AsText(member.Value);
    }

    private static void RemoveStagedIcons(string stageRoot)
    {
        foreach (var stale in Directory.GetFiles(stageRoot, "app-icon.*"))
        {
            try { File.Delete(stale); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>The snapshot's profile with case-insensitive keys, as the store reads every profile.</summary>
    private static JsonObject Profile(RunSnapshot snapshot) =>
        WorkbenchJson.Clone(snapshot.Profile) as JsonObject ?? WorkbenchJson.NewObject();

    private static JsonNode? ReadNode(string path)
    {
        try { return WorkbenchJson.Read(path); }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { return null; }
    }
}
