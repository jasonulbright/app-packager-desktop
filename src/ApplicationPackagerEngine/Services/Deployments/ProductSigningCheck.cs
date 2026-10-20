using System.Text.Json.Nodes;
using ApplicationPackagerEngine.Models.Inventory;
using ApplicationPackagerEngine.Models.Signing;
using ApplicationPackagerEngine.Services.ConfigMgrSession;
using ApplicationPackagerEngine.Services.Logging;

namespace ApplicationPackagerEngine.Services;

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
}

/// <summary>
/// Finds the applications the product published to the connected site in the inventory, and
/// applies <see cref="MecmTarget.VerifyRequiredDeploymentSigning"/> to the signing record of the
/// published build. A build whose stage manifest cannot be read is refused when the policy
/// requires signed deployment scripts.
/// </summary>
public sealed class ProductSigningCheck(
    ApplicationInventoryService inventory, Func<ConfigMgrConnection> connection, Func<SigningPolicy?> policy, IAppLog? log = null) : IDeploymentSigningCheck
{
    private const string PublishAction = "Publish";

    private IReadOnlyList<InventoryDestinationState> Publications()
    {
        var site = connection();
        if (string.IsNullOrWhiteSpace(site.SiteCode)) return [];
        try
        {
            return inventory.GetSucceededDestinations(InventoryEnvironmentIdentity.ConfigMgr(site.SiteCode, site.ProviderMachine), PublishAction);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            log?.Warning(DeploymentService.LogArea, $"The inventory could not be read ({ex.GetType().Name}).");
            throw new DeploymentServiceException("The list of applications this product packaged could not be read, so the signing rule cannot be checked.");
        }
    }

    public IReadOnlySet<string> PackagedApplications() =>
        Publications().Select(p => p.LastSuccessObjectId!).ToHashSet(StringComparer.OrdinalIgnoreCase);

    public string? RequiredDeploymentRefusal(string applicationName)
    {
        var normalized = SigningPolicy.Normalize(policy());
        if (!normalized.RequireDeployment) return null;
        var published = Publications().Where(p => string.Equals(p.LastSuccessObjectId, applicationName, StringComparison.OrdinalIgnoreCase)).ToList();
        if (published.Count == 0) return null;
        foreach (var publication in published)
        {
            var record = SigningRecord(publication);
            try
            {
                MecmTarget.VerifyRequiredDeploymentSigning(normalized, record, null);
            }
            catch (InvalidOperationException)
            {
                return $"This product packaged application {applicationName}, and the script-signing policy requires signed deployment scripts, but the published build has no signed deployment scripts. A Required deployment is refused. Deploy it as Available, or package a signed build.";
            }
        }
        return null;
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
