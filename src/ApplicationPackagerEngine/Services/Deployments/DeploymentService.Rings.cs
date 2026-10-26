using ApplicationPackagerEngine.Services.Logging;
using ApplicationPackagerEngine.Services.ConfigMgrSession;

namespace ApplicationPackagerEngine.Services;

/// <summary>A deployment read by its ID: Read is false when the read failed or the ID has the wrong form, so a missing deployment is known only when Read is true.</summary>
public sealed record DeploymentLookup(bool Read, DeploymentRecord? Found);

/// <summary>The live state of one deployment.</summary>
/// <param name="Exists">True or false when the object read ran; null when it failed or the ID has the wrong form.</param>
/// <param name="SummaryRead">True when the summary read ran; a missing summary then means "not summarized yet".</param>
public sealed record DeploymentLiveRow(string DeploymentId, bool? Exists, bool SummaryRead, int? Targeted, int Success, int Errors, int InProgress, int Unknown, int Other, DateTime? SummarizationTime);

/// <summary>A deployment of an object to a collection with the creator and creation time ConfigMgr holds for it.</summary>
/// <param name="Differences">The planned settings the deployment does not hold; empty when it holds all of them.</param>
public sealed record DeploymentCandidate(DeploymentRecord Deployment, DateTime? CreatedUtc, string CreatedBy, IReadOnlyList<string> Differences);

/// <summary>The site reads the ring service composes over the deployment service.</summary>
public interface IDeploymentSiteReader
{
    Task<DeploymentLookup> LookupAsync(DeploymentObjectType type, string deploymentId, CancellationToken ct);

    /// <summary>The object and summary state of each ID; one script run with two provider queries.</summary>
    Task<IReadOnlyList<DeploymentLiveRow>> LiveAsync(DeploymentObjectType type, IReadOnlyList<string> deploymentIds, CancellationToken ct);

    /// <summary>
    /// The deployments of the object to the collection (a package: of its program) with their
    /// creation proof and the planned settings they do not hold. Throws a plain
    /// <see cref="DeploymentServiceException"/> when ConfigMgr cannot be read.
    /// </summary>
    Task<IReadOnlyList<DeploymentCandidate>> CandidatesAsync(DeployableObject obj, string collectionId, DeploymentOptions planned, CancellationToken ct);
}

public sealed partial class DeploymentService : IDeploymentSiteReader
{
    internal sealed record LiveExistsRow(string? Kind, string? DeploymentId, string? AssignmentId);

    internal sealed record LiveSummaryRow(string? Kind, string? DeploymentId, string? AssignmentId, int Targeted, int Success, int Errors, int InProgress, int Unknown, int Other, string? Summarized);

    public async Task<DeploymentLookup> LookupAsync(DeploymentObjectType type, string deploymentId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var (read, site) = await ReadDeploymentOrNullAsync(type, deploymentId).ConfigureAwait(false);
        return new DeploymentLookup(read, site is null ? null : ToRecord(type, site.Row));
    }

    public async Task<IReadOnlyList<DeploymentLiveRow>> LiveAsync(DeploymentObjectType type, IReadOnlyList<string> deploymentIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(deploymentIds);
        var valid = deploymentIds.Where(i => IsDeploymentId(type, i)).Select(i => i.ToUpperInvariant()).Distinct().ToArray();
        var unread = deploymentIds.Select(i => new DeploymentLiveRow(i, null, false, null, 0, 0, 0, 0, 0, null)).ToList();
        if (valid.Length == 0) return unread;
        ConfigMgrCommandResult output;
        try
        {
            output = await ReadAsync(new ConfigMgrParameters { { "Operation", "Live" }, { "Type", type.ToString() }, { "DeploymentIds", valid } }, ct).ConfigureAwait(false);
        }
        catch (DeploymentServiceException)
        {
            return unread;
        }
        var read = Rows<ReadFlag>(output, "LiveRead").FirstOrDefault()?.Read == true;
        var summaryRead = Rows<ReadFlag>(output, "SummaryRead").FirstOrDefault()?.Read == true;
        var exists = Rows<LiveExistsRow>(output, "Exists");
        var summaries = Rows<LiveSummaryRow>(output, "Summary");
        _log?.Info(LogArea, $"Read the live state of {valid.Length} deployments: {exists.Count} exist, {summaries.Count} summaries.");
        return deploymentIds.Select(id =>
        {
            if (!read || !IsDeploymentId(type, id)) return new DeploymentLiveRow(id, null, false, null, 0, 0, 0, 0, 0, null);
            var found = exists.FirstOrDefault(e => string.Equals(e.DeploymentId, id, StringComparison.OrdinalIgnoreCase));
            var summary = summaries.FirstOrDefault(s => string.Equals(s.DeploymentId, id, StringComparison.OrdinalIgnoreCase))
                ?? (found?.AssignmentId is { Length: > 0 } number ? summaries.FirstOrDefault(s => string.Equals(s.AssignmentId, number, StringComparison.Ordinal)) : null);
            return summary is null
                ? new DeploymentLiveRow(id, found is not null, summaryRead, null, 0, 0, 0, 0, 0, null)
                : new DeploymentLiveRow(id, found is not null, summaryRead, summary.Targeted, summary.Success, summary.Errors, summary.InProgress, summary.Unknown, summary.Other, ParseTime(summary.Summarized));
        }).ToList();
    }

    public async Task<IReadOnlyList<DeploymentCandidate>> CandidatesAsync(DeployableObject obj, string collectionId, DeploymentOptions planned, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(obj);
        ArgumentNullException.ThrowIfNull(planned);
        var collection = (collectionId ?? "").Trim().ToUpperInvariant();
        if (!PackageIdRegex().IsMatch(collection)) throw new DeploymentServiceException("The collection ID is not valid.");
        var parameters = ObjectParameters("Deployments", obj);
        parameters["CollectionId"] = collection;
        var output = await ReadAsync(parameters, ct).ConfigureAwait(false);
        if (Rows<ResultRow>(output, "Result").FirstOrDefault()?.Status == "object-missing")
            throw new DeploymentServiceException($"{Noun(obj.Type)} {Shown(obj.Name, obj.Id)} does not exist any more.", DeploymentFailureKind.Stale);
        var rows = Rows<DeploymentRow>(output, "Deployment")
            .Where(d => string.Equals(d.CollectionId, collection, StringComparison.OrdinalIgnoreCase)
                && (obj.Type != DeploymentObjectType.Package || string.Equals(d.Program, obj.Program, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        var seconds = Seconds(planned);
        var result = new List<DeploymentCandidate>();
        foreach (var row in rows.Where(r => IsDeploymentId(obj.Type, r.DeploymentId)))
        {
            var differences = StoredDifferences(obj.Type, row, seconds);
            DateTime? createdUtc = null;
            var createdBy = "";
            if (differences.Count == 0)
            {
                var proofParameters = ObjectParameters("Proof", obj);
                proofParameters["DeploymentId"] = row.DeploymentId;
                // The status message 30006 that proves a package or task sequence deployment's creator can
                // reach the site many seconds after the create, so the read waits for it.
                proofParameters["ProofReads"] = 15;
                var proof = Rows<ProofRow>(await ReadAsync(proofParameters, ct).ConfigureAwait(false), "Proof").FirstOrDefault();
                createdUtc = ParseUtc(proof?.Created);
                createdBy = proof?.CreatedBy ?? "";
            }
            result.Add(new DeploymentCandidate(ToRecord(obj.Type, row) with { ObjectName = obj.Name }, createdUtc, createdBy, differences));
        }
        _log?.Info(LogArea, $"Read {result.Count} deployments of one {obj.Type} to one collection with their creation proof.");
        return result;
    }
}
