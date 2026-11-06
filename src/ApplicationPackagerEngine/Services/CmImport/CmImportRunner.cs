namespace ApplicationPackagerEngine.Services.CmImport;

/// <summary>What the operator asks for.</summary>
/// <param name="ApplicationName">The exact name of the ConfigMgr application.</param>
/// <param name="OutputRoot">The folder that receives one folder per application.</param>
/// <param name="Options">Variant strategy and the two refusal switches.</param>
/// <param name="SkipContent">Plan and write the reports without copying content.</param>
/// <param name="Package">Also build the .intunewin files.</param>
/// <param name="Publish">Also publish each app; needs <paramref name="Package"/>. Null publishes nothing.</param>
public sealed record CmImportRequest(
    string ApplicationName,
    string OutputRoot,
    CmImportOptions Options,
    bool SkipContent = false,
    bool Package = false,
    IntunePublishOptions? Publish = null);

public sealed record CmImportOutcome(
    CmImportPlan Plan,
    string Report,
    CmStageResult? Stage,
    IReadOnlyList<CmPackagedApp> Packages);

public interface ICmImportRunner
{
    /// <summary>Reads the application and plans the conversion. Nothing is written.</summary>
    Task<CmImportOutcome> PreviewAsync(CmImportRequest request, Action<string>? log = null, CancellationToken ct = default);

    /// <summary>Plans, stages and, when asked, packages and publishes.</summary>
    Task<CmImportOutcome> ConvertAsync(CmImportRequest request, Action<string>? log = null, CancellationToken ct = default);
}

/// <summary>
/// The conversion from one request: read the application from the site (read-only), plan one Intune
/// app per deployment type, copy the content, write the reports, and optionally build the .intunewin
/// files and publish them. The tenant values are checked before anything is copied.
/// </summary>
public sealed class CmImportRunner(
    CmApplicationSiteReader reader,
    CmImportPublisher publisher,
    Func<CancellationToken, Task<string>> resolveToolPath) : ICmImportRunner
{
    private static void Validate(CmImportRequest request, bool stage)
    {
        if (string.IsNullOrWhiteSpace(request.ApplicationName)) throw new ArgumentException("Enter the name of the ConfigMgr application.");
        if (!stage) return;
        if (string.IsNullOrWhiteSpace(request.OutputRoot)) throw new ArgumentException("Choose an output folder.");
        var publish = request.Publish;
        if (publish is not null)
        {
            if (!request.Package) throw new ArgumentException("Publishing needs the .intunewin files. Turn on Build the .intunewin files.");
            if (string.IsNullOrWhiteSpace(publish.TenantId) || string.IsNullOrWhiteSpace(publish.ClientId) || string.IsNullOrWhiteSpace(publish.ClientSecret))
                throw new ArgumentException("Publishing needs the tenant ID, the client ID and the client secret. Enter them in Settings, Intune.");
        }
        if (request.Package && request.SkipContent) throw new ArgumentException("Building the .intunewin files needs the content. Turn off Skip content.");
    }

    private async Task<(CmApplication Model, CmImportPlan Plan)> PlanAsync(CmImportRequest request, Action<string>? log, CancellationToken ct)
    {
        log?.Invoke($"Reading {request.ApplicationName} from the site...");
        var xml = await reader.ReadDigestAsync(request.ApplicationName.Trim(), ct).ConfigureAwait(false);
        var model = CmApplicationDigestReader.Read(xml);
        var plan = CmImportPlanner.Plan(model, request.Options);
        await reader.ResolveRelationshipNamesAsync(plan, w => log?.Invoke(w), ct).ConfigureAwait(false);
        return (model, plan);
    }

    public async Task<CmImportOutcome> PreviewAsync(CmImportRequest request, Action<string>? log = null, CancellationToken ct = default)
    {
        Validate(request, stage: false);
        var (_, plan) = await PlanAsync(request, log, ct).ConfigureAwait(false);
        return new CmImportOutcome(plan, CmImportPlanner.Report(plan), null, []);
    }

    public async Task<CmImportOutcome> ConvertAsync(CmImportRequest request, Action<string>? log = null, CancellationToken ct = default)
    {
        Validate(request, stage: true);
        var (model, plan) = await PlanAsync(request, log, ct).ConfigureAwait(false);
        // The tool may need a download. A failure must leave the output folder untouched.
        var toolPath = request.Package ? await resolveToolPath(ct).ConfigureAwait(false) : null;
        var stage = CmImportStage.Export(plan, request.OutputRoot.Trim(), request.SkipContent, model.IconBase64);
        log?.Invoke($"Staged {stage.Staged.Count} app(s) in {stage.ApplicationFolder}");

        IReadOnlyList<CmPackagedApp> packages = [];
        if (toolPath is not null)
        {
            packages = await publisher.PackageAsync(plan, stage, toolPath, request.Publish, log, ct).ConfigureAwait(false);
        }
        return new CmImportOutcome(plan, CmImportPlanner.Report(plan), stage, packages);
    }
}
