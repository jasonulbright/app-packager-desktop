namespace ApplicationPackagerEngine.Services.Supersedence;

/// <summary>What the Intune half of a run needs to apply supersedence: the title key, the mode, and the confirmation of the run.</summary>
/// <param name="AlreadyAnswered">The answer the ConfigMgr half already got in this run (Yes or Skip), so the administrator is asked once; null asks here.</param>
public sealed record IntuneSupersedenceRequest(
    SupersedenceKey Key, string Title, string Version, SupersedenceMode Mode, SupersedenceConfirmer Confirmer,
    SupersedenceAnswer? AlreadyAnswered = null);

/// <summary>The older app that the new app will supersede, found and confirmed before the publish.</summary>
public sealed record IntuneSupersedencePlan(LineApplication Old, bool Uninstall);

/// <summary>
/// The Intune side of supersedence, as the ConfigMgr side does it: with the mode on, n-1 is found by display name and publisher; none found is one
/// log line; found asks Yes, Skip or No (or reuses the answer the ConfigMgr half got in the same run); after the publish the relation is created.
/// A relation that fails leaves the published app as it is and returns the sentence.
/// </summary>
public static class IntuneSupersedenceFlow
{
    /// <summary>Returns the plan to apply after the publish, or null when no relation is to be made. Throws <see cref="OperationCanceledException"/> on No.</summary>
    public static async Task<IntuneSupersedencePlan?> PrepareAsync(
        IntunePublishService service, IntunePublishOptions options, IntuneSupersedenceRequest request, Action<string>? log, CancellationToken ct)
    {
        if (request.Mode == SupersedenceMode.Off) return null;
        if (request.AlreadyAnswered == SupersedenceAnswer.Skip)
        {
            log?.Invoke("Supersedence (Intune)     : skipped at the confirmation; the app is published without it.");
            return null;
        }
        LookupResult found;
        try { found = await service.FindOlderVersionAsync(options, request.Key, request.Title, request.Version, log, ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log?.Invoke($"Supersedence not applied   : the lookup of the older Intune app failed: {ex.Message}");
            return null;
        }
        foreach (var note in found.Notes) log?.Invoke($"Supersedence (Intune)     : {note}");
        if (found.Old is null)
        {
            log?.Invoke("Supersedence (Intune)     : no older version of this app was found in the tenant, so nothing is superseded.");
            return null;
        }
        var uninstall = request.Mode == SupersedenceMode.ReplaceAndUninstall;
        var answer = request.AlreadyAnswered ?? await request.Confirmer.ConfirmAsync(
            new SupersedencePrompt(request.Title, request.Version, found.Old.Title, found.Old.SoftwareVersion, uninstall)).ConfigureAwait(false);
        switch (answer)
        {
            case SupersedenceAnswer.Yes:
                log?.Invoke($"Supersedence (Intune)     : confirmed, {request.Title} will supersede {found.Old.Title} {found.Old.SoftwareVersion}.");
                return new IntuneSupersedencePlan(found.Old, uninstall);
            case SupersedenceAnswer.Skip:
                log?.Invoke("Supersedence (Intune)     : skipped at the confirmation; the app is published without it.");
                return null;
            default:
                log?.Invoke("Supersedence (Intune)     : declined at the confirmation; the run stops and nothing was published.");
                throw new OperationCanceledException("The supersedence was declined at the confirmation.");
        }
    }

    /// <summary>Creates the relation for the published app. Returns null on success, else the sentence of the failure (the app stays published).</summary>
    public static async Task<string?> ApplyRelationAsync(
        IntunePublishService service, IntunePublishOptions options, IntuneSupersedencePlan plan, string newAppId, Action<string>? log, CancellationToken ct)
    {
        var result = await service.ApplySupersedenceAsync(
            options, newAppId, plan.Old.CiId, plan.Old.Title, plan.Old.SoftwareVersion, plan.Uninstall, log, ct).ConfigureAwait(false);
        if (result.Status == SupersedenceStepStatus.NotApplied)
        {
            log?.Invoke($"Supersedence not applied   : the app is published, but the Intune relation was not created: {result.Message}");
            return result.Message;
        }
        log?.Invoke($"Supersedence (Intune)     : {result.Message}");
        return null;
    }
}
