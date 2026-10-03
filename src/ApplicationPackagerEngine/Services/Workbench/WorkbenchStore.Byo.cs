using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using ApplicationPackagerEngine.Models.Workbench;

namespace ApplicationPackagerEngine.Services.Workbench;

/// <summary>A BYO application's definition, the revision a build uses, and its verified installer.</summary>
public sealed record ByoResolvedSource(
    WorkbenchApplicationDefinition Definition,
    ByoSourceRevision Source,
    string InstallerPath);

public sealed partial class WorkbenchStore
{
    /// <summary>
    /// Persists a dropped installer as a durable BYO application: a manual-update
    /// definition on the default profile, with the installer as source revision 1.
    /// </summary>
    public ByoSourceUpdate SaveByoApplication(
        string installerPath, ByoInstallerIdentity? identity, string? displayName = null, string? publisher = null,
        string? softwareVersion = null, ByoStageValues? stageValues = null, string? applicationId = null)
    {
        if (!File.Exists(installerPath)) throw new FileNotFoundException($"Installer not found: {installerPath}", installerPath);
        if (string.IsNullOrWhiteSpace(applicationId)) applicationId = ApplicationIds.New(ApplicationOrigin.Byo);
        if (!ApplicationIds.IsByo(applicationId))
            throw new ArgumentException($"Source revisions apply to BYO applications only (got '{applicationId}').");
        if (GetApplicationDefinition(applicationId).Persisted)
            throw new InvalidOperationException($"Application '{applicationId}' already exists in this data root.");

        if (string.IsNullOrWhiteSpace(displayName)) displayName = identity?.AppName;
        if (string.IsNullOrWhiteSpace(publisher)) publisher = identity?.Publisher;
        if (string.IsNullOrWhiteSpace(softwareVersion)) softwareVersion = identity?.SoftwareVersion;
        if (string.IsNullOrWhiteSpace(displayName)) throw new InvalidOperationException("A BYO application requires a display name.");

        SaveApplicationDefinition(new WorkbenchApplicationDefinition
        {
            SchemaVersion = WorkbenchApplicationDefinition.CurrentSchemaVersion,
            ApplicationId = applicationId,
            Origin = ApplicationIds.ByoPrefix,
            DisplayName = displayName.Trim(),
            Publisher = string.IsNullOrWhiteSpace(publisher) ? null : publisher.Trim(),
            UpdatePolicy = "Manual",
            ActiveProfileId = ApplicationIds.DefaultProfileId,
            Sources = []
        });
        try
        {
            return UpdateByoSource(applicationId, installerPath, identity, softwareVersion, stageValues);
        }
        catch
        {
            // A definition with no revision cannot stage, so a failed first copy leaves no application behind.
            var folder = GetApplicationFolder(applicationId);
            if (Directory.Exists(folder) && !WorkbenchJson.IsReparsePoint(folder)) Directory.Delete(folder, recursive: true);
            throw;
        }
    }

    /// <summary>The stored source revisions of a BYO application, oldest first.</summary>
    public IReadOnlyList<ByoSourceRevision> GetByoSources(string applicationId) =>
        ReadSources(GetApplicationDefinition(applicationId));

    /// <summary>
    /// Compares a replacement installer's identity with the newest stored revision
    /// without writing anything, so the differences can be shown before the
    /// replacement is accepted.
    /// </summary>
    public IReadOnlyList<ByoIdentityChange> PreviewByoSource(string applicationId, ByoInstallerIdentity? identity)
    {
        AssertByo(applicationId);
        var previous = GetByoSources(applicationId).LastOrDefault();
        return previous is null ? [] : CompareIdentity(previous, identity);
    }

    /// <summary>
    /// Adds a new numbered revision. The installer is copied into the revision's own
    /// folder and hash-verified; an existing revision is never overwritten. Identity
    /// is compared with the previous revision: InstallerType and Architecture changes
    /// are Blocking and recorded as incompatible overrides, ProductCode, AppName and
    /// Publisher changes are Review. The application lock is held from the read of the
    /// stored revisions to the save, so two replacements never take one revision number.
    /// </summary>
    public ByoSourceUpdate UpdateByoSource(
        string applicationId, string installerPath, ByoInstallerIdentity? identity,
        string? softwareVersion = null, ByoStageValues? stageValues = null)
    {
        AssertByo(applicationId);
        if (!File.Exists(installerPath)) throw new FileNotFoundException($"Installer not found: {installerPath}", installerPath);
        if (WorkbenchJson.IsReparsePoint(installerPath))
            throw new InvalidOperationException($"Installer '{installerPath}' is a reparse point; copy the real file instead.");
        var applicationFolder = GetApplicationFolder(applicationId);
        if (!Directory.Exists(applicationFolder))
            throw new InvalidOperationException($"BYO application '{applicationId}' is not stored in this data root.");
        using (AcquireApplicationLock(applicationFolder, applicationId))
            return UpdateByoSourceLocked(applicationId, applicationFolder, installerPath, identity, softwareVersion, stageValues);
    }

    private ByoSourceUpdate UpdateByoSourceLocked(
        string applicationId, string applicationFolder, string installerPath, ByoInstallerIdentity? identity,
        string? softwareVersion, ByoStageValues? stageValues)
    {
        var definition = GetApplicationDefinition(applicationId);
        if (!definition.Persisted) throw new InvalidOperationException($"BYO application '{applicationId}' is not stored in this data root.");
        var sources = ReadSources(definition).ToList();
        var previous = sources.LastOrDefault();
        var revision = sources.Count == 0 ? 1 : sources.Max(s => s.Revision) + 1;
        if (string.IsNullOrWhiteSpace(softwareVersion)) softwareVersion = identity?.SoftwareVersion;

        var fileName = Path.GetFileName(Path.GetFullPath(installerPath));
        AssertSafeSegment(fileName, "Installer file name");
        var sourcesRoot = WorkbenchPaths.CombineUnder(applicationFolder, "sources");
        var revisionName = revision.ToString(CultureInfo.InvariantCulture);
        var revisionFolder = WorkbenchPaths.CombineUnder(sourcesRoot, revisionName);
        if (Directory.Exists(revisionFolder) || File.Exists(revisionFolder))
            throw new InvalidOperationException(
                $"Source revision folder '{revisionFolder}' already exists; a stored revision is never overwritten.");

        var expected = WorkbenchJson.FileSha256(installerPath);
        var claim = ClaimRevisionFolder(revisionFolder);
        string stored;
        try
        {
            stored = StageFileCopier.Copy(applicationFolder, installerPath, Path.Combine("sources", revisionName, fileName));
            var actual = WorkbenchJson.FileSha256(stored);
            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"Installer '{installerPath}' changed while it was copied into source revision {revision}; drop it again.");
        }
        catch
        {
            RemoveClaimedRevision(revisionFolder, claim);
            throw;
        }

        var source = new ByoSourceRevision
        {
            Revision = revision,
            FileName = fileName,
            Path = stored,
            Sha256 = expected,
            Size = new FileInfo(stored).Length,
            SoftwareVersion = Blank(softwareVersion),
            InstallerType = identity?.InstallerType ?? "",
            ProductCode = identity?.ProductCode ?? "",
            Architecture = identity?.Architecture ?? "",
            AppName = identity?.AppName ?? "",
            Publisher = identity?.Publisher ?? "",
            AddedAt = WorkbenchJson.NowRoundTrip(),
            Engine = identity?.Engine,
            StageValues = stageValues
        };
        var changes = previous is null ? [] : CompareIdentity(previous, identity);
        source.IncompatibleOverrides = changes.Where(c => c.Severity == ByoChangeSeverity.Blocking).Select(c => c.Field).ToList();

        sources.Add(source);
        definition.Sources = new JsonArray(sources.Select(s => (JsonNode?)JsonSerializer.SerializeToNode(s, WorkbenchJson.FileOptions)).ToArray());
        if (!string.IsNullOrWhiteSpace(softwareVersion))
        {
            definition.Extra ??= [];
            definition.Extra["SoftwareVersion"] = JsonSerializer.SerializeToElement(softwareVersion);
        }
        try
        {
            SaveApplicationDefinition(definition);
        }
        catch
        {
            // An unrecorded revision folder would block every later replacement as an existing revision.
            RemoveClaimedRevision(revisionFolder, claim);
            throw;
        }
        File.Delete(claim);
        return new ByoSourceUpdate(applicationId, revision, source, changes);
    }

    private const string RevisionClaimName = ".revision.claim";

    /// <summary>
    /// Creates the revision folder's claim marker with create-new semantics and returns its
    /// path. A marker that already exists means another writer took this revision, and
    /// nothing of its folder is touched.
    /// </summary>
    private static string ClaimRevisionFolder(string revisionFolder)
    {
        Directory.CreateDirectory(revisionFolder);
        var claim = Path.Combine(revisionFolder, RevisionClaimName);
        try
        {
            using (new FileStream(claim, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
        }
        catch (IOException ex) when (File.Exists(claim))
        {
            throw new InvalidOperationException(
                $"Source revision folder '{revisionFolder}' is being written by another replacement; a stored revision is never overwritten.", ex);
        }
        return claim;
    }

    /// <summary>Removes a revision folder only while it still carries the claim this call created.</summary>
    private static void RemoveClaimedRevision(string revisionFolder, string claim)
    {
        if (!File.Exists(claim) || WorkbenchJson.IsReparsePoint(revisionFolder)) return;
        Directory.Delete(revisionFolder, recursive: true);
    }

    /// <summary>
    /// An exclusive lock in the application folder, held across a read-modify-write of
    /// application.json: without it two writers read the same revisions and the later
    /// save drops the earlier one. Another holder is waited for, up to a bound.
    /// </summary>
    private static FileStream AcquireApplicationLock(string applicationFolder, string applicationId)
    {
        var lockPath = Path.Combine(applicationFolder, "application.json.lock");
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            try
            {
                return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && DateTime.UtcNow < deadline)
            {
                Thread.Sleep(25);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new InvalidOperationException($"Application '{applicationId}' is being changed by another writer; try again.", ex);
            }
        }
    }

    /// <summary>
    /// The revision a BYO build stages from: the newest, or the newest whose
    /// SoftwareVersion equals <paramref name="version"/>. The installer is read from
    /// the revision folder under this data root and must still hash to the recorded value.
    /// </summary>
    public ByoResolvedSource ResolveByoSource(string applicationId, string? version = null)
    {
        AssertByo(applicationId);
        var definition = GetApplicationDefinition(applicationId);
        if (!definition.Persisted) throw new InvalidOperationException($"BYO application '{applicationId}' is not stored in this data root.");
        var revisions = ReadSources(definition);
        if (revisions.Count == 0) throw new InvalidOperationException($"BYO application '{applicationId}' has no stored source revision.");
        ByoSourceRevision source;
        if (!string.IsNullOrWhiteSpace(version))
        {
            source = revisions.LastOrDefault(r => string.Equals(r.SoftwareVersion, version, StringComparison.Ordinal))
                     ?? throw new InvalidOperationException($"BYO application '{applicationId}' has no source revision for version '{version}'.");
        }
        else
        {
            source = revisions[^1];
        }

        var installer = GetByoInstallerPath(applicationId, source);
        if (!File.Exists(installer))
            throw new FileNotFoundException($"BYO source revision {source.Revision} is missing its installer: {installer}", installer);
        StageFileCopier.AssertNoReparsePoint(GetApplicationFolder(applicationId), installer);
        if (!string.Equals(WorkbenchJson.FileSha256(installer), source.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"BYO source revision {source.Revision} no longer matches its recorded SHA-256: {installer}. Replace the installer to add a new revision.");
        return new ByoResolvedSource(definition, source, installer);
    }

    /// <summary>The managed location of one revision's installer under this data root.</summary>
    public string GetByoInstallerPath(string applicationId, ByoSourceRevision source)
    {
        AssertSafeSegment(source.FileName, "Installer file name");
        if (source.Revision < 1) throw new InvalidOperationException($"Invalid BYO source revision {source.Revision}.");
        return WorkbenchPaths.CombineUnder(GetApplicationFolder(applicationId), "sources",
            source.Revision.ToString(CultureInfo.InvariantCulture), source.FileName);
    }

    private static IReadOnlyList<ByoIdentityChange> CompareIdentity(ByoSourceRevision previous, ByoInstallerIdentity? identity)
    {
        var changes = new List<ByoIdentityChange>();
        foreach (var field in ByoIdentityChange.ComparedFields)
        {
            var (old, current) = field switch
            {
                "InstallerType" => (previous.InstallerType, identity?.InstallerType),
                "Architecture" => (previous.Architecture, identity?.Architecture),
                "ProductCode" => (previous.ProductCode, identity?.ProductCode),
                "AppName" => (previous.AppName, identity?.AppName),
                _ => (previous.Publisher, identity?.Publisher)
            };
            old ??= "";
            current ??= "";
            // Identity values compare without regard to case, so a GUID or a name that differs only in case is not a change.
            if (string.Equals(old, current, StringComparison.OrdinalIgnoreCase)) continue;
            changes.Add(new ByoIdentityChange(field, old, current,
                ByoIdentityChange.BlockingFields.Contains(field) ? ByoChangeSeverity.Blocking : ByoChangeSeverity.Review));
        }
        return changes;
    }

    private static List<ByoSourceRevision> ReadSources(WorkbenchApplicationDefinition definition)
    {
        var results = new List<ByoSourceRevision>();
        foreach (var node in definition.Sources ?? [])
        {
            if (node is not JsonObject) continue;
            var source = node.Deserialize<ByoSourceRevision>(WorkbenchJson.FileOptions);
            if (source is not null) results.Add(source);
        }
        return results;
    }

    private static void AssertByo(string applicationId)
    {
        if (!ApplicationIds.IsByo(applicationId))
            throw new ArgumentException($"Source revisions apply to BYO applications only (got '{applicationId}').");
        ApplicationIds.ToKey(applicationId);
    }

    /// <summary>
    /// Throws unless <paramref name="name"/> is one plain path segment: no separator,
    /// no dot-only name, no trailing dot or space, no stream colon and no device name.
    /// </summary>
    internal static void AssertSafeSegment(string? name, string what)
    {
        if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(['\\', '/']) >= 0 || !ApplicationIds.IsPlainFolderName(name)
            || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new InvalidOperationException($"{what} '{name}' is not a single plain file name.");
        StageFileCopier.AssertPlainDestination(name);
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
