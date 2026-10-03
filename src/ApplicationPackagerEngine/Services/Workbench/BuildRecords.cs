using System.Text.Json;
using System.Text.Json.Nodes;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Models.Workbench;

namespace ApplicationPackagerEngine.Services.Workbench;

/// <summary>One staged manifest found by its exact BuildId.</summary>
public sealed record StagedBuild(string ManifestPath, string StageRoot, StageManifest Manifest);

/// <summary>
/// Sealed build records: builds\&lt;application key&gt;\&lt;ProfileId&gt;\&lt;BuildId&gt;\build.json
/// under the workbench data root, plus a copy of the stage's scripts\ folder, which
/// is the only copy of the signed detection and requirement scripts that survives a
/// later rebuild of the same stage root.
/// </summary>
public static class BuildRecords
{
    public const string FileName = "build.json";

    private static readonly JsonSerializerOptions NodeOptions = new() { PropertyNameCaseInsensitive = true };

    public static string GetBuildFolder(string dataRoot, string applicationId, string profileId, string? buildId = null)
    {
        var folder = WorkbenchPaths.CombineUnder(Path.Combine(dataRoot, "builds"), ApplicationIds.ToKey(applicationId));
        ApplicationIds.AssertProfileId(profileId);
        folder = WorkbenchPaths.CombineUnder(folder, profileId);
        if (!string.IsNullOrEmpty(buildId))
        {
            if (!ApplicationIds.IsPlainFolderName(buildId)) throw new ArgumentException($"Invalid BuildId '{buildId}'.");
            folder = WorkbenchPaths.CombineUnder(folder, buildId);
        }
        return folder;
    }

    /// <summary>
    /// Seals one build after its manifest is written and verified. A manifest with
    /// no ApplicationId or BuildId is a build without a run snapshot and gets no
    /// record; the result is then null.
    /// </summary>
    public static JsonObject? Write(string dataRoot, string stageRoot, StageManifest manifest)
    {
        if (string.IsNullOrWhiteSpace(manifest.ApplicationId) || string.IsNullOrWhiteSpace(manifest.BuildId)) return null;
        var profileId = string.IsNullOrWhiteSpace(manifest.ProfileId) ? ApplicationIds.DefaultProfileId : manifest.ProfileId;

        var folder = GetBuildFolder(dataRoot, manifest.ApplicationId, profileId, manifest.BuildId);
        Directory.CreateDirectory(folder);
        var scriptsSource = Path.Combine(stageRoot, "scripts");
        if (Directory.Exists(scriptsSource)) CopyTree(scriptsSource, Path.Combine(folder, "scripts"));

        var record = WorkbenchJson.NewObject();
        record["SchemaVersion"] = StageManifest.CurrentSchemaVersion;
        record["BuildId"] = manifest.BuildId;
        record["ApplicationId"] = manifest.ApplicationId;
        record["ProfileId"] = profileId;
        record["ProfileRevision"] = manifest.ProfileRevision;
        record["SoftwareVersion"] = manifest.SoftwareVersion;
        record["AppName"] = manifest.AppName;
        record["PlanDigest"] = manifest.PlanDigest ?? "";
        record["DetectionSource"] = manifest.DetectionSource;
        record["SetupFile"] = manifest.SetupFile ?? "";
        record["RunOverrides"] = WorkbenchJson.Clone(manifest.RunOverrides);
        record["ScriptSigning"] = WorkbenchJson.Clone(manifest.ScriptSigning);
        record["Timing"] = JsonSerializer.SerializeToNode(manifest.Timing);
        record["CustomAssets"] = JsonSerializer.SerializeToNode(manifest.CustomAssets);
        record["StageRoot"] = stageRoot;
        record["SealedAt"] = WorkbenchJson.NowRoundTrip();
        record["PolicyDigest"] = WorkbenchJson.Get(manifest.ScriptSigning, "PolicyDigest").Text ?? "";

        var path = Path.Combine(folder, FileName);
        WorkbenchJson.Write(path, record);
        record["Path"] = path;
        return record;
    }

    /// <summary>Every sealed record for one application, optionally one profile, newest BuildId first.</summary>
    public static IReadOnlyList<JsonObject> List(string dataRoot, string applicationId, string? profileId = null)
    {
        var root = WorkbenchPaths.CombineUnder(Path.Combine(dataRoot, "builds"), ApplicationIds.ToKey(applicationId));
        if (!Directory.Exists(root)) return [];
        var records = new List<JsonObject>();
        foreach (var profileFolder in Directory.GetDirectories(root))
        {
            if (!string.IsNullOrEmpty(profileId)
                && !string.Equals(Path.GetFileName(profileFolder), profileId, StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var buildFolder in Directory.GetDirectories(profileFolder))
            {
                var path = Path.Combine(buildFolder, FileName);
                if (WorkbenchJson.Read(path) is not JsonObject record) continue;
                record["Path"] = path;
                records.Add(record);
            }
        }
        return records
            .OrderByDescending(r => WorkbenchJson.Get(r, "BuildId").Text ?? "", StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>The newest sealed record, or null when no build was ever sealed.</summary>
    public static JsonObject? Latest(string dataRoot, string applicationId, string? profileId = null) =>
        List(dataRoot, applicationId, profileId).FirstOrDefault();

    /// <summary>
    /// One Click freshness: true when the newest sealed build of the profile carries this
    /// vendor version, this profile revision and this signing-policy digest; false when any
    /// differs or no build was sealed; null when the records cannot be read, which leaves
    /// the caller's version-only decision in place. Versions compare as versions when both
    /// parse, so "1.2.3.0" equals "1.2.3"; otherwise as text, ignoring case.
    /// </summary>
    public static bool? IsCurrent(string dataRoot, string applicationId, string profileId,
        string version, int profileRevision, string policyDigest)
    {
        JsonObject? record;
        try { record = Latest(dataRoot, applicationId, profileId); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                                   or InvalidOperationException or JsonException)
        {
            return null;
        }
        if (record is null) return false;

        var recorded = WorkbenchJson.Get(record, "SoftwareVersion").Text ?? "";
        var sameVersion = VersionCompare.TryParse(recorded, out _) && VersionCompare.TryParse(version, out _)
            ? VersionCompare.Compare(recorded, version) == 0
            : string.Equals(recorded, version, StringComparison.OrdinalIgnoreCase);
        if (!sameVersion) return false;
        if ((WorkbenchJson.AsInt(record["ProfileRevision"]) ?? 0) != profileRevision) return false;
        return string.Equals(WorkbenchJson.Get(record, "PolicyDigest").Text ?? "", policyDigest ?? "", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The staged manifest that carries exactly <paramref name="buildId"/>, newest file
    /// first. A manifest with another BuildId is never taken in its place: Package
    /// consumes the build the operator chose or refuses it as stale.
    /// </summary>
    public static StagedBuild ResolveStageManifestForBuild(string buildId, string searchRoot)
    {
        if (!Directory.Exists(searchRoot)) throw new DirectoryNotFoundException($"Stage search root not found: {searchRoot}");
        var manifests = Directory.EnumerateFiles(searchRoot, "stage-manifest.json", SearchOption.AllDirectories)
            .Select(p => new FileInfo(p))
            .OrderByDescending(f => f.LastWriteTimeUtc);
        foreach (var file in manifests)
        {
            JsonNode? data;
            try { data = WorkbenchJson.Read(file.FullName); }
            catch (JsonException) { continue; }
            if (data is not JsonObject) continue;
            if (string.Equals(WorkbenchJson.Get(data, "BuildId").Text, buildId, StringComparison.OrdinalIgnoreCase))
                return new StagedBuild(file.FullName, file.DirectoryName!, new ManifestService().Read(file.FullName));
        }
        throw new InvalidOperationException($"stale build: no staged manifest under '{searchRoot}' carries BuildId '{buildId}'.");
    }

    private static void CopyTree(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.GetFiles(source))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
        foreach (var folder in Directory.GetDirectories(source))
            CopyTree(folder, Path.Combine(destination, Path.GetFileName(folder)));
    }
}
