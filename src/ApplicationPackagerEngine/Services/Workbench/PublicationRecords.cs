using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Models.Workbench;

namespace ApplicationPackagerEngine.Services.Workbench;

/// <summary>
/// Append-only publication history: one JSON line per publish attempt in
/// publications\&lt;application key&gt;\&lt;yyyyMM&gt;.jsonl under the workbench data root.
/// </summary>
public static class PublicationRecords
{
    public const string ContentOnly = "ContentOnly";
    public const string Mecm = "MECM";
    public const string Intune = "Intune";

    public const string Succeeded = "Succeeded";
    public const string Failed = "Failed";
    public const string Skipped = "Skipped";

    private static readonly string[] Targets = [ContentOnly, Mecm, Intune];
    private static readonly string[] Results = [Succeeded, Failed, Skipped];
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    /// <summary>Appends one record and returns the file path and the record written.</summary>
    public static (string Path, JsonObject Record) Write(string dataRoot, string applicationId, string buildId, string target,
        string profileId = ApplicationIds.DefaultProfileId, string? remoteAppId = null, string? siteOrTenant = null,
        string result = Succeeded, string? message = null, DateTimeOffset? timestamp = null)
    {
        if (string.IsNullOrWhiteSpace(applicationId)) throw new ArgumentException("A publication record requires an ApplicationId.", nameof(applicationId));
        if (string.IsNullOrWhiteSpace(buildId)) throw new ArgumentException("A publication record requires a BuildId.", nameof(buildId));
        var canonicalTarget = Targets.FirstOrDefault(t => string.Equals(t, target, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException($"Unknown publication target '{target}'; expected {string.Join(", ", Targets)}.", nameof(target));
        var canonicalResult = Results.FirstOrDefault(r => string.Equals(r, result, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException($"Unknown publication result '{result}'; expected {string.Join(", ", Results)}.", nameof(result));

        var when = timestamp ?? DateTimeOffset.Now;
        var folder = WorkbenchPaths.CombineUnder(Path.Combine(dataRoot, "publications"), ApplicationIds.ToKey(applicationId));
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, when.ToString("yyyyMM", CultureInfo.InvariantCulture) + ".jsonl");

        var record = new JsonObject
        {
            ["Timestamp"] = when.ToString("o", CultureInfo.InvariantCulture),
            ["ApplicationId"] = applicationId,
            ["ProfileId"] = profileId,
            ["BuildId"] = buildId,
            ["Target"] = canonicalTarget,
            ["RemoteAppId"] = remoteAppId,
            ["SiteOrTenant"] = siteOrTenant,
            ["Result"] = canonicalResult,
            ["Message"] = message
        };
        File.AppendAllText(path, record.ToJsonString() + "\r\n", Utf8NoBom);
        return (path, record);
    }

    /// <summary>
    /// Appends the record for a staged build's publish. A build without a run snapshot
    /// carries no BuildId and records nothing. A failure to write is reported through
    /// <paramref name="onLog"/> and never fails the publish it describes.
    /// </summary>
    public static string? TryAppend(StageManifest manifest, string target, string result,
        string? remoteAppId = null, string? siteOrTenant = null, string? message = null,
        Action<string>? onLog = null, string? dataRoot = null)
    {
        if (string.IsNullOrWhiteSpace(manifest.ApplicationId) || string.IsNullOrWhiteSpace(manifest.BuildId)) return null;
        try
        {
            var root = dataRoot ?? WorkbenchPaths.ResolveDataRoot();
            var profileId = string.IsNullOrWhiteSpace(manifest.ProfileId) ? ApplicationIds.DefaultProfileId : manifest.ProfileId;
            return Write(root, manifest.ApplicationId, manifest.BuildId, target, profileId, remoteAppId, siteOrTenant, result, message).Path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            onLog?.Invoke($"Publication record not written: {ex.Message}");
            return null;
        }
    }
}
