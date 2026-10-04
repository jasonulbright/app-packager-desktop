using System.Diagnostics;
using System.Text.Json.Nodes;
using ApplicationPackagerEngine.Models.Workbench;

namespace ApplicationPackagerEngine.Services.Workbench;

/// <summary>The environment and staging root one PS1 packager child runs with.</summary>
public sealed record PackagerChildLaunch(
    string ApplicationId,
    string ProfileId,
    string DownloadRoot,
    RunSnapshot Snapshot,
    IReadOnlyDictionary<string, string> Environment);

/// <summary>
/// Hands the workbench state to a PS1 packager child process. The run snapshot file
/// and data root travel as APP_PACKAGER_RUN_SNAPSHOT and APP_PACKAGER_WORKBENCH_ROOT;
/// the legacy variables carry the active profile's requirements, variant split,
/// commands, install mode and title mode to packagers that read them individually.
/// Each variable is set on the child's own environment block, never on this process,
/// so a later unrelated build never inherits it.
/// </summary>
public static class PackagerChildBridge
{
    public const string RunSnapshotVariable = WorkbenchStore.RunSnapshotVariable;
    public const string WorkbenchRootVariable = WorkbenchPaths.DataRootVariable;
    public const string DownloadRootVariable = "APP_PACKAGER_DOWNLOAD_ROOT";
    public const string RequirementsVariable = "APP_PACKAGER_REQUIREMENTS";
    public const string VariantsVariable = "APP_PACKAGER_VARIANTS";
    public const string CommandsVariable = "APP_PACKAGER_COMMANDS";
    public const string InstallModeVariable = "APP_PACKAGER_INSTALL_MODE";
    public const string TitleModeVariable = "APP_PACKAGER_TITLE_MODE";

    /// <summary>
    /// Resolves the packager script's catalog application and its active profile,
    /// writes the run snapshot, and returns the child environment. A non-default
    /// profile stages under &lt;downloadRoot&gt;\profiles\&lt;ProfileId&gt;; the default profile
    /// keeps the download root unchanged. A signing refusal throws before any file is written.
    /// </summary>
    public static PackagerChildLaunch Prepare(
        WorkbenchStore store,
        string packagerScriptPath,
        string downloadRoot,
        string target = RunTargets.Mecm,
        JsonNode? signingPolicy = null,
        ISigningCertificateGate? signingGate = null,
        bool includeVersionInTitle = false,
        bool removeVersionFromTitle = false)
    {
        var applicationId = ApplicationIds.New(ApplicationOrigin.Catalog, path: packagerScriptPath);
        var profileId = store.GetApplicationDefinition(applicationId).ActiveProfileId;
        if (string.IsNullOrWhiteSpace(profileId)) profileId = ApplicationIds.DefaultProfileId;
        var effectiveDownloadRoot = WorkbenchPaths.GetProfileDownloadRoot(downloadRoot, profileId);

        var snapshot = store.CreateRunSnapshot(new RunSnapshotRequest
        {
            ApplicationId = applicationId,
            ProfileId = profileId,
            Target = target,
            SigningPolicy = signingPolicy,
            PackagerScriptPath = packagerScriptPath,
            DownloadRoot = effectiveDownloadRoot
        }, signingGate);

        return FromSnapshot(store, snapshot, downloadRoot, includeVersionInTitle, removeVersionFromTitle);
    }

    /// <summary>
    /// The child environment for a run snapshot that already exists, such as one a
    /// caller created to stage and then package the same build. The download root
    /// is scoped to the snapshot's profile the same way <see cref="Prepare"/> scopes it.
    /// </summary>
    public static PackagerChildLaunch FromSnapshot(WorkbenchStore store, RunSnapshot snapshot, string downloadRoot,
        bool includeVersionInTitle = false, bool removeVersionFromTitle = false)
    {
        if (string.IsNullOrWhiteSpace(snapshot.Path))
            throw new InvalidOperationException("The run snapshot has no file; a packager child reads it from APP_PACKAGER_RUN_SNAPSHOT.");
        var profileId = string.IsNullOrWhiteSpace(snapshot.ProfileId) ? ApplicationIds.DefaultProfileId : snapshot.ProfileId;
        var effectiveDownloadRoot = WorkbenchPaths.GetProfileDownloadRoot(downloadRoot, profileId);

        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [RunSnapshotVariable] = snapshot.Path!,
            [WorkbenchRootVariable] = store.DataRoot
        };
        if (!string.IsNullOrWhiteSpace(effectiveDownloadRoot)) environment[DownloadRootVariable] = effectiveDownloadRoot;
        var profile = new WorkbenchProfile(WorkbenchJson.CloneObject(snapshot.Profile), ApplicationIds.IsDefaultProfile(profileId));
        foreach (var (name, value) in BuildLegacyVariables(profile, includeVersionInTitle, removeVersionFromTitle))
            environment[name] = value;

        return new PackagerChildLaunch(snapshot.ApplicationId, profileId, effectiveDownloadRoot, snapshot, environment);
    }

    /// <summary>
    /// The legacy variables for one profile. Only a variable with a value is returned:
    /// requirements from Add and Replace operations that apply to every deployment type,
    /// the variant split document, the install and uninstall commands, the install mode,
    /// and the title mode (falling back to the global IncludeVersion, then NoVersion,
    /// preference when the profile sets none).
    /// </summary>
    public static IReadOnlyDictionary<string, string> BuildLegacyVariables(WorkbenchProfile profile, bool includeVersionInTitle = false,
        bool removeVersionFromTitle = false)
    {
        var variables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var rules = new JsonArray();
        foreach (var operation in WorkbenchProfileValidator.Items(profile.Get("Requirements.Operations").Value))
        {
            if (operation is null) continue;
            if (string.Equals(WorkbenchJson.Get(operation, "Op").Text, "Remove", StringComparison.OrdinalIgnoreCase)) continue;
            var appliesTo = WorkbenchProfileValidator.Items(WorkbenchJson.Get(operation, "AppliesTo").Value)
                .Select(WorkbenchJson.AsText).ToList();
            if (appliesTo.Count > 0 && !appliesTo.Contains("*")) continue;
            var rule = WorkbenchJson.Get(operation, "Rule").Value;
            if (WorkbenchJson.IsTruthy(rule)) rules.Add(rule!.DeepClone());
        }
        if (rules.Count > 0)
            variables[RequirementsVariable] = new JsonObject { ["SchemaVersion"] = 1, ["Rules"] = rules }.ToJsonString();

        var split = profile.Get("Variants.Split").Value;
        if (WorkbenchJson.IsTruthy(split)) variables[VariantsVariable] = split!.ToJsonString();

        var commands = new JsonObject { ["SchemaVersion"] = 1 };
        var install = profile.Get("Install.Command").Text;
        var uninstall = profile.Get("Uninstall.Command").Text;
        if (!string.IsNullOrEmpty(install)) commands["Install"] = install;
        if (!string.IsNullOrEmpty(uninstall)) commands["Uninstall"] = uninstall;
        if (commands.Count > 1) variables[CommandsVariable] = commands.ToJsonString();

        var installMode = profile.Get("InstallMode").Text;
        if (!string.IsNullOrEmpty(installMode)) variables[InstallModeVariable] = installMode;

        var titleMode = NormalizeTitleMode(profile.Get("Application.TitleMode").Text);
        if (string.IsNullOrEmpty(titleMode)) titleMode = ApplicationTitle.DefaultModeToken(includeVersionInTitle, removeVersionFromTitle);
        if (!string.IsNullOrEmpty(titleMode)) variables[TitleModeVariable] = titleMode;

        return variables;
    }

    /// <summary>Maps a stored title mode to its token; a profile can store the title combo text instead of the token.</summary>
    public static string? NormalizeTitleMode(string? stored)
    {
        if (string.Equals(stored, "Include version", StringComparison.OrdinalIgnoreCase)) return "IncludeVersion";
        if (string.Equals(stored, "No version", StringComparison.OrdinalIgnoreCase)) return "NoVersion";
        return stored;
    }

    /// <summary>Sets each variable with a value on the child's environment block.</summary>
    public static void Apply(ProcessStartInfo startInfo, IReadOnlyDictionary<string, string>? variables)
    {
        if (variables is null) return;
        foreach (var (name, value) in variables)
            if (!string.IsNullOrWhiteSpace(value)) startInfo.Environment[name] = value;
    }
}
