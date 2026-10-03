using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ApplicationPackagerEngine.Models.Workbench;

namespace ApplicationPackagerEngine.Services.Workbench;

public sealed partial class WorkbenchStore
{
    /// <summary>Names the run snapshot file for a child packager process.</summary>
    public const string RunSnapshotVariable = "APP_PACKAGER_RUN_SNAPSHOT";

    [GeneratedRegex("^[A-Za-z0-9._-]+$")]
    private static partial Regex BuildIdPattern();

    /// <summary>A sortable build id: yyyyMMdd-HHmmss plus eight random hex digits.</summary>
    public static string NewBuildId() =>
        $"{DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}-{Guid.NewGuid().ToString("N")[..8]}";

    /// <summary>
    /// Writes the immutable snapshot for one build. Run overrides are validated, the
    /// signing certificate is resolved for any enabled switch, and every asset the
    /// profile references is resolved to a file path, all before the file is written:
    /// a refusal leaves nothing in the runs folder. The stored profile is never changed.
    /// </summary>
    public RunSnapshot CreateRunSnapshot(RunSnapshotRequest request, ISigningCertificateGate? signingGate = null)
    {
        var applicationId = request.ApplicationId;
        var profileId = string.IsNullOrWhiteSpace(request.ProfileId) ? ApplicationIds.DefaultProfileId : request.ProfileId;
        var buildId = string.IsNullOrWhiteSpace(request.BuildId) ? NewBuildId() : request.BuildId;
        if (!BuildIdPattern().IsMatch(buildId) || !ApplicationIds.IsPlainFolderName(buildId)) throw new ArgumentException($"Invalid BuildId '{buildId}'.");
        var path = WorkbenchPaths.CombineUnder(RunsRoot, buildId + ".json");
        if (File.Exists(path)) throw new InvalidOperationException(BuildIdTaken(buildId));
        var target = RunTargets.Normalize(request.Target)
            ?? throw new ArgumentException($"Unknown run target '{request.Target}'; expected {string.Join(", ", RunTargets.All)}.");

        var profile = GetProfile(applicationId, profileId);
        var overrides = request.RunOverrides is null ? WorkbenchJson.NewObject() : WorkbenchJson.CloneObject(request.RunOverrides);
        ValidateRunOverrides(overrides);
        if (SigningPolicySwitches.IsSigningRequested(request.SigningPolicy))
            (signingGate ?? NoSigningServiceGate.Instance).ResolveCertificate(request.SigningPolicy!);

        var assets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!ApplicationIds.IsDefaultProfile(profileId))
        {
            foreach (var reference in GetAssetReferences(profile))
                assets[reference] = ResolveAssetPath(applicationId, profileId, reference);
            foreach (var entry in WorkbenchProfileValidator.Items(profile.Data["SourceFiles"]))
            {
                if (entry is null || !WorkbenchJson.IsTruthy(WorkbenchJson.Get(entry, "Linked").Value)) continue;
                var provenance = WorkbenchJson.Get(entry, "Provenance").Text;
                assets["linked:" + provenance] = ResolveAssetPath(applicationId, profileId, entry);
            }
        }

        var frozenProfile = WorkbenchJson.CloneObject(profile.Data);
        frozenProfile["IsDefault"] = profile.IsDefault;

        var snapshot = new RunSnapshot
        {
            BuildId = buildId,
            CreatedAt = WorkbenchJson.NowRoundTrip(),
            ApplicationId = applicationId,
            ProfileId = profileId,
            ProfileRevision = profile.Revision,
            Profile = frozenProfile,
            AssetPaths = assets,
            RunOverrides = overrides,
            SigningPolicy = WorkbenchJson.Clone(request.SigningPolicy),
            Target = target,
            DataRoot = DataRoot,
            DownloadRoot = request.DownloadRoot,
            PackagerScriptPath = request.PackagerScriptPath
        };

        if (!WorkbenchJson.TryWriteNew(path, snapshot)) throw new InvalidOperationException(BuildIdTaken(buildId));
        snapshot.Path = path;
        return snapshot;
    }

    private static string BuildIdTaken(string buildId) =>
        $"A run snapshot for BuildId '{buildId}' already exists; a snapshot is immutable once written.";

    /// <summary>
    /// Reads a run snapshot. With no path, APP_PACKAGER_RUN_SNAPSHOT names it; with
    /// neither, there is no snapshot and the result is null.
    /// </summary>
    public static RunSnapshot? ReadRunSnapshot(string? path = null)
    {
        if (string.IsNullOrWhiteSpace(path)) path = Environment.GetEnvironmentVariable(RunSnapshotVariable);
        if (string.IsNullOrWhiteSpace(path)) return null;
        if (!File.Exists(path)) throw new FileNotFoundException($"Run snapshot not found: {path}", path);

        var text = File.ReadAllText(path);
        if (string.IsNullOrWhiteSpace(text)) throw new InvalidOperationException($"Run snapshot is empty: {path}");
        var node = WorkbenchJson.Parse(text);
        if (node is not JsonObject) throw new InvalidOperationException($"Run snapshot is empty: {path}");
        var schema = WorkbenchJson.AsInt(node["SchemaVersion"]) ?? 0;
        if (schema > RunSnapshot.CurrentSchemaVersion)
            throw new InvalidOperationException(
                $"Run snapshot schema {schema} is newer than this build supports ({RunSnapshot.CurrentSchemaVersion}): {path}");

        var snapshot = node.Deserialize<RunSnapshot>(WorkbenchJson.FileOptions)
            ?? throw new InvalidOperationException($"Run snapshot is empty: {path}");
        snapshot.Path = path;
        return snapshot;
    }

    /// <summary>
    /// Run-level timing overrides: each present value is a positive whole number of
    /// minutes, and the estimate does not exceed the maximum.
    /// </summary>
    public static void ValidateRunOverrides(JsonNode? runOverrides)
    {
        if (!WorkbenchJson.IsTruthy(runOverrides)) return;
        WorkbenchProfileValidator.ValidateMinutes(
            WorkbenchJson.Get(runOverrides, "EstimatedMinutes").Value,
            WorkbenchJson.Get(runOverrides, "MaximumMinutes").Value,
            name => $"Run override {name}",
            (estimated, maximum) => $"Run override EstimatedMinutes ({estimated}) cannot exceed MaximumMinutes ({maximum}).");
    }

    // ── Drafts ──────────────────────────────────────────────────

    public string GetDraftPath(string applicationId, string profileId)
    {
        ApplicationIds.AssertProfileId(profileId);
        return WorkbenchPaths.CombineUnder(DraftsRoot, ApplicationIds.ToKey(applicationId), profileId + ".draft.json");
    }

    /// <summary>
    /// Stores unsaved editor state for one application and profile so it survives a
    /// crash. A draft is separate from the saved profile and never changes it.
    /// </summary>
    public JsonObject SaveDraft(string applicationId, string profileId, JsonObject draft)
    {
        var data = WorkbenchJson.CloneObject(draft);
        data["ApplicationId"] = applicationId;
        data["ProfileId"] = profileId;
        data["SavedAt"] = WorkbenchJson.NowRoundTrip();
        WorkbenchJson.Write(GetDraftPath(applicationId, profileId), data);
        return data;
    }

    /// <summary>The stored draft, or null when there is none.</summary>
    public JsonObject? GetDraft(string applicationId, string profileId) =>
        WorkbenchJson.Read(GetDraftPath(applicationId, profileId)) as JsonObject;

    /// <summary>Deletes the draft. False when there was none.</summary>
    public bool RemoveDraft(string applicationId, string profileId)
    {
        var path = GetDraftPath(applicationId, profileId);
        if (!File.Exists(path)) return false;
        File.Delete(path);
        return true;
    }
}
