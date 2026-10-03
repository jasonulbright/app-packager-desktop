using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ApplicationPackagerEngine.Models.Workbench;

namespace ApplicationPackagerEngine.Services.Workbench;

public sealed partial class WorkbenchStore
{
    [GeneratedRegex("^[0-9a-fA-F]{16}$")]
    private static partial Regex AssetIdPattern();

    /// <summary>
    /// The profile paths that name a managed asset id. SourceFiles entries add their
    /// own Asset values unless they are linked.
    /// </summary>
    public static readonly IReadOnlyList<string> AssetReferencePaths =
    [
        "Application.Icon.Asset", "Install.Script", "Install.Before", "Install.After",
        "Uninstall.Script", "Uninstall.Before", "Uninstall.After"
    ];

    /// <summary>
    /// Copies a file into managed profile storage, keyed by the first 16 hex digits
    /// of its SHA-256. A linked asset is recorded by provenance only and re-read at
    /// build time; a missing linked file then fails the build.
    /// </summary>
    public ProfileAsset AddAsset(string applicationId, string profileId, string path, bool linked = false)
    {
        if (ApplicationIds.IsDefaultProfile(profileId))
            throw new InvalidOperationException("Assets cannot be added to the packager default profile.");
        if (!File.Exists(path)) throw new FileNotFoundException($"Asset source not found: {path}", path);
        if (WorkbenchJson.IsReparsePoint(path))
            throw new InvalidOperationException($"Asset source '{path}' is a reparse point; copy the real file instead.");

        var full = Path.GetFullPath(path);
        var sha = WorkbenchJson.FileSha256(full);
        var assetId = sha[..16];
        var name = Path.GetFileName(full);
        var size = new FileInfo(full).Length;

        var stored = full;
        if (!linked)
        {
            var folder = Path.Combine(GetProfileFolder(applicationId, profileId, create: true), "assets", assetId);
            Directory.CreateDirectory(folder);
            stored = Path.Combine(folder, name);
            File.Copy(full, stored, overwrite: true);
        }
        return new ProfileAsset(assetId, name, sha, size, full, linked, stored, WorkbenchJson.NowRoundTrip());
    }

    /// <summary>Deletes one managed asset folder. False when it was already gone.</summary>
    public bool RemoveAsset(string applicationId, string profileId, string assetId)
    {
        if (!AssetIdPattern().IsMatch(assetId ?? "")) throw new ArgumentException($"Invalid AssetId '{assetId}'.");
        var folder = Path.Combine(GetProfileFolder(applicationId, profileId), "assets", assetId!);
        if (!Directory.Exists(folder)) return false;
        Directory.Delete(folder, recursive: true);
        return true;
    }

    /// <summary>
    /// Resolves an asset reference to a readable file for this build. The reference is
    /// an asset id string, or an object with Asset (or AssetId), Linked and Provenance.
    /// </summary>
    public string ResolveAssetPath(string applicationId, string profileId, JsonNode? asset)
    {
        if (asset is JsonValue value && value.GetValueKind() == System.Text.Json.JsonValueKind.String)
        {
            var wrapped = WorkbenchJson.NewObject();
            wrapped["Asset"] = value.GetValue<string>();
            asset = wrapped;
        }
        var assetId = WorkbenchJson.Get(asset, "Asset").Text;
        if (string.IsNullOrWhiteSpace(assetId)) assetId = WorkbenchJson.Get(asset, "AssetId").Text;
        var linked = WorkbenchJson.IsTruthy(WorkbenchJson.Get(asset, "Linked").Value);
        var provenance = WorkbenchJson.Get(asset, "Provenance").Text;

        if (linked)
        {
            if (string.IsNullOrWhiteSpace(provenance) || !File.Exists(provenance))
                throw new FileNotFoundException($"Linked source file '{provenance}' is missing; the build cannot snapshot it.", provenance);
            return provenance;
        }
        if (string.IsNullOrWhiteSpace(assetId))
            throw new InvalidOperationException("Asset reference carries neither an asset id nor a linked path.");
        var folder = Path.Combine(GetProfileFolder(applicationId, profileId), "assets", assetId);
        var file = Directory.Exists(folder) ? Directory.GetFiles(folder).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).FirstOrDefault() : null;
        return file ?? throw new FileNotFoundException($"Managed asset '{assetId}' is missing from profile '{profileId}'.");
    }

    public string ResolveAssetPath(string applicationId, string profileId, string assetId) =>
        ResolveAssetPath(applicationId, profileId, JsonValue.Create(assetId));

    public string ResolveAssetPath(string applicationId, string profileId, ProfileAsset asset)
    {
        var reference = WorkbenchJson.NewObject();
        reference["Asset"] = asset.AssetId;
        reference["Linked"] = asset.Linked;
        reference["Provenance"] = asset.Provenance;
        return ResolveAssetPath(applicationId, profileId, reference);
    }

    /// <summary>
    /// Copies an icon and source files that a profile references only by a picked file
    /// path into managed storage, filling in their asset ids. Entries that already carry
    /// an asset id, linked entries and a removed icon are left unchanged. The profile
    /// is updated in place and must already have a stored id.
    /// </summary>
    public void ImportProfileAssets(WorkbenchProfile profile)
    {
        var applicationId = profile.ApplicationId;
        var profileId = profile.ProfileId;

        if (profile.Data["Application"] is JsonObject application
            && application.TryGetPropertyValue("Icon", out var iconNode) && iconNode is JsonObject icon
            && string.IsNullOrWhiteSpace(WorkbenchJson.AsText(icon["Asset"]))
            && !string.IsNullOrWhiteSpace(WorkbenchJson.AsText(icon["Path"])))
        {
            var asset = AddAsset(applicationId, profileId, WorkbenchJson.AsText(icon["Path"])!);
            icon["Asset"] = asset.AssetId;
            icon["Path"] = asset.Path;
        }

        foreach (var node in WorkbenchProfileValidator.Items(profile.Data["SourceFiles"]))
        {
            if (node is not JsonObject entry) continue;
            if (WorkbenchJson.IsTruthy(entry["Linked"]) || !string.IsNullOrWhiteSpace(WorkbenchJson.AsText(entry["Asset"]))) continue;
            var provenance = WorkbenchJson.AsText(entry["Provenance"]);
            if (string.IsNullOrWhiteSpace(provenance)) continue;
            var asset = AddAsset(applicationId, profileId, provenance);
            entry["Asset"] = asset.AssetId;
            entry["Sha256"] = asset.Sha256;
            entry["Size"] = asset.Size;
        }
    }

    /// <summary>Every managed asset id a profile references, without duplicates, in reference order.</summary>
    public static IReadOnlyList<string> GetAssetReferences(WorkbenchProfile profile)
    {
        var references = new List<string>();
        foreach (var path in AssetReferencePaths)
        {
            var member = profile.Get(path);
            if (member.Found && !string.IsNullOrWhiteSpace(member.Text)) references.Add(member.Text!);
        }
        foreach (var entry in WorkbenchProfileValidator.Items(profile.Data["SourceFiles"]))
        {
            if (entry is null) continue;
            if (WorkbenchJson.IsTruthy(WorkbenchJson.Get(entry, "Linked").Value)) continue;
            var assetId = WorkbenchJson.Get(entry, "Asset").Text;
            if (!string.IsNullOrWhiteSpace(assetId)) references.Add(assetId);
        }
        return references.Distinct(StringComparer.Ordinal).ToList();
    }
}
