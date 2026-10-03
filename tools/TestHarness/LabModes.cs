using System.Text.Json;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Models.Signing;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.Signing;
using ApplicationPackagerEngine.Services.Wsus;
using ApplicationPackagerEngine.Services.Workbench;

/// <summary>
/// Headless lab modes: --wsus drives the WSUS engine services against a named
/// server, --package-mecm drives ConfigMgr packaging of an already staged
/// folder with the settings the UI passes. Each returns the process exit code.
/// </summary>
internal static class LabModes
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public static bool Handles(string[] args) => args.Contains("--wsus") || args.Contains("--package-mecm");

    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            return args.Contains("--wsus") ? await RunWsusAsync(args) : await RunPackageMecmAsync(args);
        }
        catch (Exception ex) when (ex is WsusOperationException or WsusRefusedException or WsusPayloadException or WsusRuleException
                                       or ArgumentException or InvalidOperationException or IOException)
        {
            Console.Error.WriteLine(ex.Message);
            if (ex is WsusRefusedException refused) Console.Error.WriteLine("Refusal codes: " + string.Join(",", refused.Codes));
            return 1;
        }
    }

    private static async Task<int> RunWsusAsync(string[] args)
    {
        var operation = Value(args, "--wsus") ?? throw new ArgumentException("--wsus needs an operation.");
        var settings = new WsusSettings
        {
            ServerName = Value(args, "--server") ?? throw new ArgumentException("--server is required."),
            UseSsl = args.Contains("--ssl"),
            PortNumber = int.TryParse(Value(args, "--port"), out var port) ? port : 0,
            Classification = Value(args, "--classification") ?? WsusClassifications.Updates,
            ApprovalGroup = Value(args, "--approval-group") ?? "",
            DeclineSuperseded = args.Contains("--decline-superseded")
        };
        var client = new WsusAdminClient();
        void Log(string line) => Console.WriteLine(line);

        switch (operation.ToLowerInvariant())
        {
            case "status":
                Print(await client.GetStatusAsync(settings, Log));
                return 0;
            case "groups":
                foreach (var name in await client.GetComputerGroupNamesAsync(settings, Log)) Console.WriteLine(name);
                return 0;
            case "list":
                Print(await client.ListAsync(settings, args.Contains("--all"), Log));
                return 0;
            case "publish":
            {
                var folder = Value(args, "--content") ?? throw new ArgumentException("publish needs --content <stagedContentFolder>.");
                var manifest = new ManifestService().Read(Path.Combine(folder, "stage-manifest.json"));
                var result = await new WsusPublishService().PublishAsync(manifest, folder, settings, Log, default, Value(args, "--support-url"));
                Print(result);
                return result.Warnings.Count > 0 ? 1 : 0;
            }
            case "setstate":
            {
                var ids = (Value(args, "--package") ?? throw new ArgumentException("setstate needs --package <id>[,<id>].")).Split(',').Select(Guid.Parse);
                var action = Value(args, "--action") ?? throw new ArgumentException("setstate needs --action Approve|Decline|Expire|Remove.");
                var results = await client.SetStateAsync(settings, ids, action, Value(args, "--group"), Log);
                Print(results);
                return results.All(r => r.Ok) ? 0 : 1;
            }
            case "catalog-import":
            {
                var ids = WsusIdentity.CatalogIds(Value(args, "--ids") ?? throw new ArgumentException("catalog-import needs --ids <ids or catalog links>."));
                var results = await client.ImportCatalogUpdatesAsync(settings, ids, Log);
                Print(results);
                return results.All(r => r.Ok) ? 0 : 1;
            }
            case "create-selfsigned":
                Print(await client.CreateSelfSignedCertificateAsync(settings, Log));
                return 0;
            case "export-cer":
                Console.WriteLine(await client.ExportCertificateAsync(settings, Value(args, "--out") ?? throw new ArgumentException("export-cer needs --out <file.cer>."), Log));
                return 0;
            case "import-pfx":
            {
                var pfx = Value(args, "--pfx") ?? throw new ArgumentException("import-pfx needs --pfx <file.pfx>.");
                Console.Write("PFX password: ");
                var password = ReadHidden();
                Print(await client.ImportPfxAsync(settings, pfx, password, Log));
                return 0;
            }
            default:
                throw new ArgumentException($"Unknown --wsus operation '{operation}'.");
        }
    }

    /// <summary>
    /// The ConfigMgr Package phase for a staged folder, as the UI runs it for a
    /// staged manifest: title from the manifest's title mode, the manifest's own
    /// command overrides, the default condition templates, and the signing policy of the
    /// --stage-inputs file when one is given (off otherwise).
    /// </summary>
    private static async Task<int> RunPackageMecmAsync(string[] args)
    {
        var folder = Value(args, "--package-mecm") ?? throw new ArgumentException("--package-mecm needs <stagedContentFolder>.");
        var siteCode = Value(args, "--site") ?? throw new ArgumentException("--site is required.");
        var provider = Value(args, "--provider") ?? throw new ArgumentException("--provider is required.");
        var share = Value(args, "--share") ?? throw new ArgumentException("--share is required.");
        var onExisting = Enum.Parse<OnExistingBehavior>(Value(args, "--on-existing") ?? nameof(OnExistingBehavior.Skip), ignoreCase: true);
        var comment = Value(args, "--comment") ?? "";

        var manifest = new ManifestService().Read(Path.Combine(folder, "stage-manifest.json"));
        var commandOverrides = DeploymentTypeSpecResolver.ResolveCommandOverrides(manifest, null);
        var applicationTitle = ApplicationTitle.Resolve(manifest.AppName, manifest.SoftwareVersion, ApplicationTitle.Parse(manifest.TitleMode));
        if (applicationTitle != manifest.AppName) Console.WriteLine($"Application title            : {applicationTitle}");
        var stageInputs = Value(args, "--stage-inputs") is { } inputsPath ? HarnessStageInputs.Load(inputsPath) : null;
        var signingPolicy = StageFinalizer.EffectivePolicy(null, SigningPolicy.FromSettings(stageInputs?.SigningPolicy));

        var result = await new MecmTarget().PackageAsync(
            manifest, folder, siteCode, share, comment, 15, 30,
            Console.WriteLine,
            commandOverrides: commandOverrides,
            conditionTemplates: new ConditionTemplateService().Load(Console.WriteLine),
            requirements: [],
            providerMachine: provider,
            onExisting: onExisting,
            contentLayout: ContentLayouts.Nested,
            applicationTitle: applicationTitle,
            scriptSigning: MecmScriptSigningContext.FromManifest(manifest, signingPolicy),
            contentScope: ProfileContentScope.ForManifest(manifest));
        Print(result);
        return result.Success ? 0 : 1;
    }

    private static string? Value(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length && !args[index + 1].StartsWith("--", StringComparison.Ordinal) ? args[index + 1] : null;
    }

    private static void Print(object value) => Console.WriteLine(JsonSerializer.Serialize(value, value.GetType(), Indented));

    private static string ReadHidden()
    {
        if (Console.IsInputRedirected) return Console.ReadLine() ?? "";
        var buffer = new System.Text.StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) break;
            if (key.Key == ConsoleKey.Backspace) { if (buffer.Length > 0) buffer.Length--; continue; }
            buffer.Append(key.KeyChar);
        }
        Console.WriteLine();
        return buffer.ToString();
    }
}
