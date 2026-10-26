using System.Text.Json.Nodes;
using ApplicationPackagerEngine.Models.Inventory;
using ApplicationPackagerEngine.Models.Signing;
using ApplicationPackagerEngine.Services.ConfigMgrSession;
using ApplicationPackagerEngine.Services.Logging;

namespace ApplicationPackagerEngine.Services;

/// <summary>An application as the connected site holds it now.</summary>
/// <param name="CiId">The CI_ID of the latest revision.</param>
/// <param name="ModelName">The ModelName, the same for every revision and kept on a rename; empty when not read.</param>
/// <param name="RevisionCiIds">The CI_IDs of every revision of the application; empty when not read.</param>
public sealed record SiteApplication(string Name, string CiId, string ModelName, IReadOnlyList<string> RevisionCiIds);

/// <summary>The product's script-signing rule for Required deployments of applications it packaged.</summary>
public interface IDeploymentSigningCheck
{
    /// <summary>The names of the applications this product published to the connected site.</summary>
    IReadOnlySet<string> PackagedApplications();

    /// <summary>
    /// Null when a Required deployment of the application may run; a plain refusal when the
    /// product packaged the application and its signing policy refuses the build. Throws
    /// <see cref="DeploymentServiceException"/> when that cannot be told.
    /// </summary>
    string? RequiredDeploymentRefusal(string applicationName);

    /// <summary>True when this product packaged the application. Throws <see cref="DeploymentServiceException"/> when that cannot be told.</summary>
    bool IsPackaged(SiteApplication application) => PackagedApplications().Contains(application.Name);

    /// <summary>Whether this product packaged each application, in input order.</summary>
    IReadOnlyList<bool> ArePackaged(IReadOnlyList<SiteApplication> applications)
    {
        Link(applications);
        return applications.Select(IsPackaged).ToArray();
    }

    /// <summary>
    /// <see cref="RequiredDeploymentRefusal(string)"/> for the application the site holds: also a
    /// refusal when the product cannot tell whether it packaged the application.
    /// </summary>
    string? RequiredDeploymentRefusal(SiteApplication application) => RequiredDeploymentRefusal(application.Name);

    /// <summary>Links the records of packaged applications that hold no ConfigMgr identity yet to the listed applications.</summary>
    void Link(IReadOnlyList<SiteApplication> applications) { }
}

/// <summary>
/// Finds the applications the product published to the connected site in the inventory, and
/// applies <see cref="MecmTarget.VerifyRequiredDeploymentSigning"/> to the signing record of the
/// published build. A build whose stage manifest cannot be read is refused when the policy
/// requires signed deployment scripts.
/// <para>
/// An inventory record matches a site application by its ModelName, which survives renames
/// and new revisions; a record with a CI_ID and no ModelName matches when the CI_ID is one of
/// the application's revisions; only a record with neither matches by name. A record that
/// matches by CI_ID or by a unique name is linked to the ModelName. A record that was renamed
/// on the site before any lookup linked it matches nothing and the application reads as not
/// packaged: the inventory holds no identity that could tell otherwise.
/// </para>
/// </summary>
public sealed class ProductSigningCheck(
    ApplicationInventoryService inventory, Func<ConfigMgrConnection> connection, Func<SigningPolicy?> policy, IAppLog? log = null) : IDeploymentSigningCheck
{
    private const string PublishAction = "Publish";

    private (InventoryEnvironmentIdentity? Environment, IReadOnlyList<InventoryDestinationState> Rows) Publications()
    {
        var site = connection();
        if (string.IsNullOrWhiteSpace(site.SiteCode)) return (null, []);
        try
        {
            var environment = InventoryEnvironmentIdentity.ConfigMgr(site.SiteCode, site.ProviderMachine);
            return (environment, inventory.GetSucceededDestinations(environment, PublishAction));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            log?.Warning(DeploymentService.LogArea, $"The inventory could not be read ({ex.GetType().Name}).");
            throw new DeploymentServiceException("The list of applications this product packaged could not be read, so the signing rule cannot be checked.");
        }
    }

    public IReadOnlySet<string> PackagedApplications() =>
        Publications().Rows.Select(p => p.LastSuccessObjectId!).ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static bool HasIdentity(InventoryDestinationState row) => !string.IsNullOrWhiteSpace(row.ConfigMgrModelName) || !string.IsNullOrWhiteSpace(row.ConfigMgrCiId);

    /// <summary>The records of the application: by ModelName or a revision CI_ID first, by name only for records without either.</summary>
    private static List<InventoryDestinationState> Matches(IReadOnlyList<InventoryDestinationState> rows, SiteApplication app)
    {
        var revisions = app.RevisionCiIds.Append(app.CiId).Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => c.Trim()).ToHashSet(StringComparer.Ordinal);
        var byId = rows.Where(r => !string.IsNullOrWhiteSpace(r.ConfigMgrModelName)
            ? string.Equals(r.ConfigMgrModelName, app.ModelName, StringComparison.OrdinalIgnoreCase)
            : !string.IsNullOrWhiteSpace(r.ConfigMgrCiId) && revisions.Contains(r.ConfigMgrCiId.Trim())).ToList();
        if (byId.Count > 0) return byId;
        return rows.Where(r => !HasIdentity(r) && string.Equals(r.LastSuccessObjectId, app.Name, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    public bool IsPackaged(SiteApplication application)
    {
        var (environment, rows) = Publications();
        var matched = Matches(rows, application);
        if (environment is not null && !string.IsNullOrWhiteSpace(application.ModelName))
            foreach (var row in matched.Where(r => string.IsNullOrWhiteSpace(r.ConfigMgrModelName) && !string.IsNullOrWhiteSpace(r.ConfigMgrCiId)))
                TryLink(row, environment, application);
        return matched.Count > 0;
    }

    public string? RequiredDeploymentRefusal(string applicationName) =>
        RequiredDeploymentRefusal(new SiteApplication(applicationName, "", "", []));

    public string? RequiredDeploymentRefusal(SiteApplication application)
    {
        var normalized = SigningPolicy.Normalize(policy());
        if (!normalized.RequireDeployment) return null;
        var (environment, rows) = Publications();
        var published = Matches(rows, application);
        if (published.Count == 0)
        {
            var identityRead = !string.IsNullOrWhiteSpace(application.ModelName) && application.RevisionCiIds.Count > 0;
            return !identityRead && rows.Any(HasIdentity)
                ? $"The ConfigMgr identity of application {application.Name} could not be read, so this product cannot tell whether it packaged the application. A Required deployment is refused. Try again."
                : null;
        }
        if (environment is not null && !string.IsNullOrWhiteSpace(application.ModelName))
            foreach (var row in published.Where(r => string.IsNullOrWhiteSpace(r.ConfigMgrModelName)))
                TryLink(row, environment, application);
        foreach (var publication in published)
        {
            var record = SigningRecord(publication);
            try
            {
                MecmTarget.VerifyRequiredDeploymentSigning(normalized, record, null);
            }
            catch (InvalidOperationException)
            {
                var original = string.Equals(publication.LastSuccessObjectId, application.Name, StringComparison.OrdinalIgnoreCase) ? "" : $" (published as {publication.LastSuccessObjectId})";
                return $"This product packaged application {application.Name}{original}, and the script-signing policy requires signed deployment scripts, but the published build has no signed deployment scripts. A Required deployment is refused. Deploy it as Available, or package a signed build.";
            }
        }
        return null;
    }

    public void Link(IReadOnlyList<SiteApplication> applications)
    {
        var (environment, rows) = Publications();
        if (environment is null) return;
        Link(rows, environment, applications);
    }

    public IReadOnlyList<bool> ArePackaged(IReadOnlyList<SiteApplication> applications)
    {
        var (environment, rows) = Publications();
        if (environment is not null) Link(rows, environment, applications);
        return applications.Select(application => Matches(rows, application).Count > 0).ToArray();
    }

    private void Link(IReadOnlyList<InventoryDestinationState> rows, InventoryEnvironmentIdentity environment, IReadOnlyList<SiteApplication> applications)
    {
        foreach (var row in rows.Where(r => string.IsNullOrWhiteSpace(r.ConfigMgrModelName)))
        {
            var candidates = !string.IsNullOrWhiteSpace(row.ConfigMgrCiId)
                ? applications.Where(a => string.Equals(a.CiId, row.ConfigMgrCiId!.Trim(), StringComparison.Ordinal) || a.RevisionCiIds.Contains(row.ConfigMgrCiId!.Trim())).ToList()
                : applications.Where(a => string.Equals(a.Name, row.LastSuccessObjectId, StringComparison.OrdinalIgnoreCase)).ToList();
            if (candidates.Count == 1 && !string.IsNullOrWhiteSpace(candidates[0].ModelName)) TryLink(row, environment, candidates[0]);
        }
    }

    private void TryLink(InventoryDestinationState row, InventoryEnvironmentIdentity environment, SiteApplication application)
    {
        try
        {
            if (inventory.LinkConfigMgrApplication(row.PackagerUid, environment, PublishAction, row.LastSuccessObjectId!, application.CiId, application.ModelName))
                log?.Info(DeploymentService.LogArea, "Linked one packaged application to its ConfigMgr identity.");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            log?.Warning(DeploymentService.LogArea, $"A packaged application could not be linked to its ConfigMgr identity ({ex.GetType().Name}).");
        }
    }

    private ScriptSigningRecord? SigningRecord(InventoryDestinationState publication)
    {
        if (publication.ApplicationId is not { Length: > 0 } applicationId || publication.ProfileId is not { Length: > 0 } profileId) return null;
        var package = inventory.GetPackageState(applicationId, profileId);
        if (package is null || !string.Equals(package.BuildId, publication.LastSuccessBuildId, StringComparison.OrdinalIgnoreCase)) return null;
        try
        {
            if (!File.Exists(package.ManifestPath) || new FileInfo(package.ManifestPath).Length > 32 * 1024 * 1024) return null;
            if (JsonNode.Parse(File.ReadAllText(package.ManifestPath)) is not JsonObject manifest) return null;
            var node = manifest.FirstOrDefault(p => string.Equals(p.Key, "ScriptSigning", StringComparison.OrdinalIgnoreCase)).Value;
            return ScriptSigningRecord.FromJsonNode(node);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            log?.Warning(DeploymentService.LogArea, $"A stage manifest could not be read ({ex.GetType().Name}).");
            return null;
        }
    }
}
