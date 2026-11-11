using System.Globalization;
using System.Text.Json.Nodes;
using ApplicationPackagerEngine.Models;

namespace ApplicationPackagerEngine.Services.Wsus;

public enum WsusPublishOutcome
{
    /// <summary>A new update with a new package id.</summary>
    Published,
    /// <summary>The update of this identity, type and version was already on the server.</summary>
    AlreadyPublished
}

/// <summary>
/// The application cannot be carried by WSUS; nothing reached the server.
/// Codes are the blocking finding codes, in the order found.
/// </summary>
public sealed class WsusRefusedException(IReadOnlyList<WsusFinding> findings)
    : InvalidOperationException("This application cannot be published to WSUS: "
        + string.Join(" | ", findings.Where(f => f.Severity == WsusSeverity.Blocking).Select(f => $"{f.Code}: {f.Message}")))
{
    public IReadOnlyList<WsusFinding> Findings { get; } = findings;
    public IReadOnlyList<string> Codes { get; } = WsusCompatibility.BlockingCodes(findings);
}

public sealed record WsusPublishResult(
    Guid PackageId,
    string Title,
    WsusPublishOutcome Outcome,
    string Approved,
    IReadOnlyList<Guid> Superseded,
    IReadOnlyList<Guid> Declined,
    IReadOnlyList<string> Warnings,
    string Message,
    WsusRuleSource RulesSource,
    IReadOnlyList<WsusFinding> Findings)
{
    public WsusStepResult ToStepResult() => new(
        Outcome == WsusPublishOutcome.Published ? WsusStepStatus.Published : WsusStepStatus.AlreadyPublished,
        Message, [], Warnings, PackageId.ToString());
}

/// <summary>
/// Publishes a staged installer to WSUS as a locally published update.
/// Compatibility, payload integrity and the rules are decided before the
/// first server call. The update applies only where the detection finds the
/// product with an older version. Each new update gets a new package id. A
/// repeat publish of the same version finds the update by its identity line,
/// type and version, skips an expired one, and only reapplies the approval
/// and the decline of earlier versions, unless the update is declined on the
/// server. Lower versions of the same identity and type are superseded, and
/// declined when the settings ask for it. After a successful publish a
/// failed approval or decline is a warning, not an exception.
/// </summary>
public sealed class WsusPublishService(IWsusAdapterRunner? runner = null, WsusRuleBuilder? rules = null, Func<DateTimeOffset>? clock = null) : IWsusPublisher
{
    private readonly IWsusAdapterRunner _runner = runner ?? new WsusAdapterRunner();
    private readonly WsusRuleBuilder _rules = rules ?? new WsusRuleBuilder(new MsiInspectionService());
    private readonly Func<DateTimeOffset> _clock = clock ?? (() => DateTimeOffset.Now);

    /// <summary>
    /// What a WSUS update does and does not do. The update applies only where the detection finds the
    /// product with an older version, so WSUS cannot give a first install of the application.
    /// </summary>
    public const string UpdatesOnlyNote = "applies only where the detection finds an older version of the application, so WSUS cannot give a first install of it";

    /// <summary>The same note as a sentence for a result detail or a report.</summary>
    public const string UpdatesOnlySentence = "This update " + UpdatesOnlyNote + ".";

    /// <summary>Exit codes an EXE reports; any other code fails the install.</summary>
    public static readonly IReadOnlyList<(int Code, string Result, bool Reboot)> ExeReturnCodes =
    [
        (0, "Succeeded", false),
        (1707, "Succeeded", false),
        (3010, "Succeeded", true),
        (1641, "Succeeded", true)
    ];

    /// <summary>The command line of the update: the MSI properties of the arguments, or the EXE arguments as they are.</summary>
    private static string CommandLineFor(StageManifest manifest, string installerType) =>
        installerType == "MSI" ? WsusMsiCommandLine.Convert(manifest.InstallArgs) : manifest.InstallArgs ?? "";

    /// <summary>
    /// The return codes of the update. WSUS runs the installer itself, so the exit codes of the definition that the ConfigMgr and
    /// Intune wrappers turn into 3010 and 0 must be in the update: a reboot code is a success with a restart, a success code is a
    /// success. A code that the fixed table has keeps the meaning of the table. An MSI has the table only.
    /// </summary>
    public static IReadOnlyList<(int Code, string Result, bool Reboot)> ReturnCodesFor(StageManifest manifest)
    {
        var codes = ExeReturnCodes.ToList();
        if (!string.Equals(manifest.InstallerType, "EXE", StringComparison.OrdinalIgnoreCase)) return codes;
        foreach (var code in manifest.RebootExitCodes ?? [])
            if (codes.All(c => c.Code != code)) codes.Add((code, "Succeeded", true));
        foreach (var code in manifest.SuccessExitCodes ?? [])
            if (codes.All(c => c.Code != code)) codes.Add((code, "Succeeded", false));
        return codes;
    }

    /// <summary>
    /// A manifest that was staged before the manifest carried the exit codes has none. The publish takes them from the definition
    /// of the application, so such a stage does not publish with the fixed table. A manifest that has codes is left as it is.
    /// </summary>
    public static void CarryExitCodes(StageManifest manifest, AppDefinition? definition)
    {
        if (definition is null || manifest.RebootExitCodes is not null || manifest.SuccessExitCodes is not null) return;
        manifest.RebootExitCodes = definition.RebootExitCodes;
        manifest.SuccessExitCodes = definition.SuccessExitCodes;
    }

    /// <exception cref="WsusRefusedException">A blocking finding; nothing reached the server.</exception>
    /// <exception cref="WsusPayloadException">The staged bytes do not match the manifest.</exception>
    /// <exception cref="WsusRuleException">The detection cannot be mapped.</exception>
    /// <exception cref="WsusOperationException">The settings or a server call failed, or the update of this version on the server differs from the stage.</exception>
    /// <param name="supportUrl">The vendor page for the update's support link, such as the definition's VendorUrl; a value that is not an absolute URI is left out.</param>
    public async Task<WsusPublishResult> PublishAsync(StageManifest manifest, string contentFolder, WsusSettings settings,
        Action<string>? onProgress = null, CancellationToken ct = default, string? supportUrl = null)
    {
        var normalized = WsusAdminClient.Configured(settings);

        var findings = WsusCompatibility.Evaluate(manifest, contentFolder, _rules);
        foreach (var finding in findings)
            onProgress?.Invoke($"WSUS compatibility            : [{finding.Severity}] {finding.Code} - {finding.Message}");
        if (findings.Any(f => f.Severity == WsusSeverity.Blocking)) throw new WsusRefusedException(findings);

        var payload = WsusPayload.Files(manifest);
        WsusPayload.AssertIntegrity(manifest, contentFolder, payload);
        var rules = _rules.Build(manifest, contentFolder);

        var version = manifest.SoftwareVersion ?? "";
        var identity = WsusIdentity.IdentityTag(manifest);
        var formerIdentities = WsusIdentity.FormerIdentityTags(manifest);
        var title = WsusIdentity.Title(manifest);
        var installerType = (manifest.InstallerType ?? "").Equals("MSI", StringComparison.OrdinalIgnoreCase) ? "MSI" : "EXE";
        var planDigest = WsusPlanDigest.Compute(CommandLineFor(manifest, installerType), installerType, payload[0], rules.IsInstalled, rules.IsInstallable,
            ReturnCodesFor(manifest), payload.Zip(WsusPayload.RecordedHashes(manifest, payload),
                (file, hash) => (file, WsusPlanDigest.IsSignedScript(file) ? WsusPlanDigest.ScriptHash(File.ReadAllText(WsusPayload.ResolvePath(contentFolder, file))) : hash)));

        var prepareRequest = WsusAdminClient.Request(normalized);
        prepareRequest["IdentityTag"] = identity;
        prepareRequest["FormerIdentityTags"] = new JsonArray(formerIdentities.Select(f => (JsonNode)JsonValue.Create(f)!).ToArray());
        var prepared = WsusAdminClient.Data(await _runner.RunAsync("PublishPrepare", prepareRequest, null, onProgress, ct), normalized);
        var serverName = WsusAdminClient.Str(prepared, "ServerName");

        // A certificate that cannot be read (DCOM rights on the server's
        // certificate service) is not proof that none exists; the publish call
        // itself refuses when the server truly has none.
        if (!WsusAdminClient.Bool(prepared, "CertificateReadable"))
        {
            onProgress?.Invoke($"WSUS signing certificate      : not readable ({WsusAdminClient.Str(prepared, "CertificateError")}); publishing continues");
        }
        else
        {
            var certificate = WsusAdminClient.ReadCertificate(prepared["SigningCertificate"] as JsonObject)
                ?? throw new WsusOperationException(WsusErrorKinds.NoCertificate,
                    $"WSUS server {serverName} has no signing certificate. Create or import one in Options, WSUS Publishing.");
            if (certificate.IsExpired(_clock()))
                throw new WsusOperationException(WsusErrorKinds.NoCertificate,
                    $"The WSUS signing certificate {certificate.Thumbprint} expired on {certificate.NotAfter.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}. Import a new one, then clients must trust it.");
        }

        var payloadMegabytes = WsusPayload.Megabytes(contentFolder, payload);
        var cabLimit = prepared["CabLimitMegabytes"]?.GetValue<int>() ?? 0;
        if (cabLimit > 0 && payloadMegabytes > cabLimit)
            onProgress?.Invoke($"WSUS cab size                 : payload {payloadMegabytes:N0} MB exceeds LocalPublishingMaxCabSize {cabLimit} MB on the server");

        var decision = WsusSupersedence.Decide(WsusAdminClient.ReadCandidates(prepared["Candidates"] as JsonArray), identity, version, formerIdentities);
        foreach (var (update, reason) in decision.LeftAlone)
            onProgress?.Invoke($"WSUS supersedence             : {update.Title} left as is; {reason}");

        // A repeat publish of this version finds the update that is not expired.
        // Otherwise the update gets a new id: WSUS restarts a removed id at
        // revision 1, and Configuration Manager keeps the content it recorded for
        // that id and revision, so a reused id installs with a digest mismatch.
        var existing = decision.Existing;
        // An update cannot change on the server. A repeat publish of a stage that differs from the update must not report the old
        // update as current: it is refused, and the update is expired by hand. An update from an earlier release has no digest.
        var existingFromEarlierRelease = false;
        if (existing is not null)
        {
            var recorded = WsusPlanDigest.Parse(existing.Description);
            if (recorded is null) existingFromEarlierRelease = true;
            else if (!string.Equals(recorded, planDigest, StringComparison.Ordinal))
                throw new WsusOperationException(WsusErrorKinds.UpdateDiffers,
                    $"Version {version} is already on the server ({existing.PackageId}{(existing.Declined ? ", declined" : "")}), and this stage differs from it: the install command, the rules, the return codes or the files are not the same. WSUS cannot change a published update, and the update on the server was left as it is. Expire it in WSUS Updates, then publish again.");
        }
        var packageId = existing is not null ? Guid.Parse(existing.PackageId) : Guid.NewGuid();
        var skipFollowUp = existing?.Declined == true;
        var declineTargets = normalized.DeclineSuperseded && !skipFollowUp
            ? decision.Earlier.Where(u => !u.Declined).ToList()
            : [];

        var commit = WsusAdminClient.Request(normalized);
        commit["SkipFollowUp"] = skipFollowUp;
        commit["ApprovalGroup"] = skipFollowUp ? "" : normalized.ApprovalGroup;
        commit["DeclinePackageIds"] = new JsonArray(declineTargets.Select(u => (JsonNode)JsonValue.Create(u.PackageId)!).ToArray());
        if (existing is not null)
        {
            commit["ExistingPackageId"] = existing.PackageId;
            onProgress?.Invoke($"WSUS update exists            : {title} ({packageId})");
        }
        else
        {
            commit["ContentFolder"] = Path.GetFullPath(contentFolder);
            commit["PayloadFiles"] = new JsonArray(payload.Select(f => (JsonNode)JsonValue.Create(f)!).ToArray());
            // The adapter copies the live stage files later; it checks its private copy
            // against these, so a file changed after AssertIntegrity is never published.
            commit["PayloadSha256"] = new JsonArray(WsusPayload.RecordedHashes(manifest, payload).Select(h => (JsonNode)JsonValue.Create(h)!).ToArray());
            commit["Plan"] = BuildPlan(manifest, packageId, title, normalized.Classification, rules, decision.Earlier, installerType, payload[0], supportUrl, planDigest);
            onProgress?.Invoke($"Publishing to WSUS            : {title} ({packageId}) on {serverName}");
        }

        var committed = await _runner.RunAsync("Publish", commit, null, onProgress, ct);
        if (!committed.Ok)
        {
            if (committed.ErrorKind == WsusErrorKinds.PublishRejected)
                throw new WsusOperationException(WsusErrorKinds.PublishRejected, WsusMessages.PublishRejected(committed.Error, payloadMegabytes, cabLimit));
            throw new WsusOperationException(committed.ErrorKind, WsusMessages.Compose(committed, normalized));
        }
        var data = committed.Data ?? new JsonObject();

        var warnings = new List<string>();
        var approved = "";
        var declined = new List<Guid>();
        if (skipFollowUp)
        {
            warnings.Add("the update is declined on the server, so its approval and the decline of earlier versions were skipped");
        }
        else
        {
            var visibilityError = WsusAdminClient.Str(data, "UpdateVisibilityError");
            if (visibilityError.Length > 0)
                warnings.Add($"the update was published but its visibility could not be checked: {visibilityError}");
            if (data["Approval"] is JsonObject approval)
            {
                if (WsusAdminClient.Bool(approval, "Ok")) approved = normalized.ApprovalGroup;
                else warnings.Add($"approval for {normalized.ApprovalGroup} failed: {WsusAdminClient.Str(approval, "Message")}");
            }
            // The adapter declines earlier versions only after the replacement is approved.
            if (WsusAdminClient.Bool(data, "DeclinesSkipped"))
                warnings.Add("earlier versions were not declined because the approval failed");
            var titles = declineTargets.ToDictionary(u => u.PackageId, u => u.Title, StringComparer.OrdinalIgnoreCase);
            foreach (var entry in (data["Declines"] as JsonArray ?? []).OfType<JsonObject>())
            {
                var id = WsusAdminClient.Str(entry, "PackageId");
                if (WsusAdminClient.Bool(entry, "Ok")) declined.Add(Guid.Parse(id));
                else warnings.Add($"declining {titles.GetValueOrDefault(id, id)} failed: {WsusAdminClient.Str(entry, "Message")}");
            }
        }

        if (existingFromEarlierRelease)
            onProgress?.Invoke("WSUS update comparison       : the update on the server was published by an earlier release, so its install command, rules, return codes and files were not compared with this stage");
        var outcome = existing is null ? WsusPublishOutcome.Published : WsusPublishOutcome.AlreadyPublished;
        IReadOnlyList<Guid> superseded = outcome == WsusPublishOutcome.Published ? decision.Earlier.Select(u => Guid.Parse(u.PackageId)).ToList() : [];
        var message = ComposeMessage(outcome, packageId, rules, approved, superseded.Count, declined.Count, decision.RetiredCount, warnings);
        onProgress?.Invoke($"WSUS publish                  : {title} - {message}");
        return new WsusPublishResult(packageId, title, outcome, approved, superseded, declined, warnings, message, rules.Source, findings);
    }

    /// <summary>
    /// The result message: what happened to the package id, the MSI entry the
    /// rules read, the approval, the supersedence and decline counts, the
    /// retired-type note, then each warning; joined with '; '. The second part says that the update
    /// applies only to a computer that already has an older version.
    /// </summary>
    public static string ComposeMessage(WsusPublishOutcome outcome, Guid packageId, WsusApplicabilityRules rules, string approved,
        int supersededCount, int declinedCount, int retiredCount, IEnumerable<string> warnings)
    {
        var parts = new List<string> { $"{(outcome == WsusPublishOutcome.Published ? "published" : "already on the server")} {packageId}", UpdatesOnlyNote };
        if (rules.Source == WsusRuleSource.MsiEntry && rules.Entry is not null)
            parts.Add($"rules read the Add/Remove Programs entry that the MSI registers: {rules.Entry.Describe()}");
        if (approved.Length > 0) parts.Add($"approved for {approved}");
        if (supersededCount > 0) parts.Add($"supersedes {supersededCount} earlier version(s)");
        if (declinedCount > 0) parts.Add($"declined {declinedCount} earlier version(s)");
        if (retiredCount > 0) parts.Add($"{retiredCount} update(s) of the retired type Application stay on the server; expire or remove them in Published updates");
        parts.AddRange(warnings);
        return string.Join("; ", parts);
    }

    /// <summary>
    /// The package plan the adapter turns into a Software Distribution Package:
    /// one vendor and product for every update, the title and description with
    /// the identity line, the rules, the superseded ids, and the command line
    /// (MSI properties only, or the EXE arguments with its return codes).
    /// </summary>
    public static JsonObject BuildPlan(StageManifest manifest, Guid packageId, string title, string classification,
        WsusApplicabilityRules rules, IEnumerable<WsusUpdateCandidate> superseded, string installerType, string installerFile,
        string? supportUrl = null, string? planDigest = null) => new()
    {
        ["PackageId"] = packageId.ToString(),
        ["Title"] = title,
        ["Description"] = planDigest is null ? WsusIdentity.Description(manifest) : WsusIdentity.Description(manifest) + "\r\n" + WsusPlanDigest.Line(planDigest),
        ["VendorName"] = WsusIdentity.VendorName,
        ["ProductName"] = WsusIdentity.ProductName,
        ["Classification"] = classification,
        ["SupportUrl"] = Uri.TryCreate(supportUrl, UriKind.Absolute, out _) ? supportUrl : "",
        ["IsInstallable"] = rules.IsInstallable,
        ["IsInstalled"] = rules.IsInstalled,
        ["SupersededPackageIds"] = new JsonArray(superseded.Select(u => (JsonNode)JsonValue.Create(u.PackageId)!).ToArray()),
        ["InstallerType"] = installerType,
        ["InstallerFile"] = installerFile,
        ["CommandLine"] = CommandLineFor(manifest, installerType),
        ["ReturnCodes"] = new JsonArray(ReturnCodesFor(manifest).Select(c => (JsonNode)new JsonObject
        {
            ["Code"] = c.Code,
            ["Result"] = c.Result,
            ["Reboot"] = c.Reboot
        }).ToArray())
    };
}
