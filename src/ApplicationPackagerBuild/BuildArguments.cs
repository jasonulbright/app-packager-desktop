using System.Globalization;
using ApplicationPackagerEngine.Models.Workbench;
using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerBuild;

/// <summary>One command-line build as the arguments ask for it. A null value inherits the saved setting or the profile.</summary>
public sealed record BuildOptions
{
    public string Application { get; init; } = "";
    public string Profile { get; init; } = ApplicationIds.DefaultProfileId;
    public string? Version { get; init; }
    public string Target { get; init; } = RunTargets.Mecm;
    public bool Stage { get; init; }
    public bool Package { get; init; }
    public string? DownloadRoot { get; init; }
    public int? EstimatedMinutes { get; init; }
    public int? MaximumMinutes { get; init; }
    public string? PackagersRoot { get; init; }
    public string? LogFolder { get; init; }
    public string? SiteCode { get; init; }
    public string? ProviderMachineName { get; init; }
    public string? FileServerPath { get; init; }
    public string Comment { get; init; } = "";
    public string? WsusServer { get; init; }
    public int? WsusPort { get; init; }
    public bool? WsusUseSsl { get; init; }
    public string? WsusClassification { get; init; }
    public string? WsusApprovalGroup { get; init; }
    public bool? WsusDeclineSuperseded { get; init; }
    public OnExistingBehavior OnExisting { get; init; } = OnExistingBehavior.Skip;
    /// <summary>--version-rule: exact or minimum. Outranks the title override and the site default.</summary>
    public string? VersionRule { get; init; }
    /// <summary>--supersedence: off, replace or replace-uninstall. Outranks the title override and the site default.</summary>
    public string? Supersedence { get; init; }
    /// <summary>--rewrite-old-versions: plan, apply or restore. It changes the version clause of the old applications of the line.</summary>
    public string? RewriteOldVersions { get; init; }
    /// <summary>--yes: the confirmation of apply and restore.</summary>
    public bool Yes { get; init; }
    /// <summary>--record-installed-value: the value read from the installed product of --application and --version.</summary>
    public string? RecordInstalledValue { get; init; }
    /// <summary>--accept-exact-block: accepts the block of the Exact guard for --application and --version.</summary>
    public bool AcceptExactBlock { get; init; }
    /// <summary>True when the run only writes a supersedence setting and neither stages, packages nor rewrites.</summary>
    public bool IsSupersedenceRecord => RecordInstalledValue is not null || AcceptExactBlock;
    public string? LocalSourceFolder { get; init; }
    public string? DataRoot { get; init; }
    public bool Help { get; init; }
    /// <summary>The CVE report run; null for a build.</summary>
    public CveReportOptions? CveReport { get; init; }
}

/// <summary>The parsed options, or the reason the arguments were refused.</summary>
public sealed record ParsedArguments(BuildOptions? Options, string? Error)
{
    public bool Ok => Options is not null;
}

/// <summary>
/// Reads <c>--name value</c>, <c>--name=value</c> and bare switches. Names compare
/// without regard to case. A switch takes an optional <c>=true</c> or <c>=false</c>,
/// so a stored WSUS choice can be turned off for one run.
/// </summary>
public static class BuildArguments
{
    public const string HelpHint = "Run AppPackagerBuild --help for usage.";

    private static readonly HashSet<string> Switches = new(StringComparer.OrdinalIgnoreCase)
    {
        "stage", "package", "yes", "accept-exact-block", "wsus-use-ssl", "wsus-decline-superseded", "help",
        "cve-report", "skip-latest", "skip-mecm", "skip-cve", "notify-always"
    };

    private static readonly HashSet<string> Valued = new(StringComparer.OrdinalIgnoreCase)
    {
        "application", "profile", "version", "target", "download-root", "estimated-minutes", "maximum-minutes",
        "packagers-root", "log-folder", "site-code", "provider-machine-name", "file-server-path", "comment",
        "wsus-server", "wsus-port", "wsus-classification", "wsus-approval-group", "on-existing",
        "local-source-folder", "data-root", "version-rule", "supersedence", "rewrite-old-versions", "record-installed-value",
        "apps", "report-folder", "report-formats", "webhook-url", "drop-folder", "keep-report-days", "config"
    };

    /// <summary>
    /// Options that name a path, a root, a server or a site. An empty value, such as an unset
    /// variable in a scheduled task, would otherwise fall back to the saved setting and run
    /// against the real profile and site.
    /// </summary>
    private static readonly string[] NonEmpty =
    [
        "data-root", "download-root", "packagers-root", "log-folder", "local-source-folder", "file-server-path",
        "site-code", "provider-machine-name", "wsus-server", "record-installed-value",
        "report-folder", "drop-folder", "webhook-url", "config", "apps", "report-formats"
    ];

    public static ParsedArguments Parse(IReadOnlyList<string> args)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (arg is "-h" or "-?" or "/?") arg = "--help";
            if (!arg.StartsWith("--", StringComparison.Ordinal) || arg.Length == 2)
                return Fail($"Unexpected argument '{arg}'. {HelpHint}");

            var name = arg[2..];
            string? inline = null;
            var equals = name.IndexOf('=');
            if (equals >= 0)
            {
                inline = name[(equals + 1)..];
                name = name[..equals];
            }

            if (Switches.Contains(name))
            {
                if (inline is not null && !bool.TryParse(inline, out _))
                    return Fail($"Option '--{name}' takes no value, or =true or =false.");
                if (!values.TryAdd(name, inline ?? "true")) return Fail($"Option '--{name}' is given more than once.");
                continue;
            }
            if (!Valued.Contains(name)) return Fail($"Unknown option '--{name}'. {HelpHint}");

            string value;
            if (inline is not null) value = inline;
            else if (i + 1 < args.Count && !args[i + 1].StartsWith("--", StringComparison.Ordinal)) value = args[++i];
            else return Fail($"Option '--{name}' needs a value.");
            if (!values.TryAdd(name, value)) return Fail($"Option '--{name}' is given more than once.");
        }

        if (Flag(values, "help") == true) return new ParsedArguments(new BuildOptions { Help = true }, null);

        foreach (var name in NonEmpty)
        {
            if (values.TryGetValue(name, out var given) && string.IsNullOrWhiteSpace(given))
                return Fail($"Option '--{name}' was given an empty value; pass a value or leave the option out.");
        }

        if (Flag(values, "cve-report") == true) return CveReportArguments.Parse(values);
        if (CveReportArguments.OnlyOptions.FirstOrDefault(values.ContainsKey) is { } cveOnly)
            return Fail($"Option '--{cveOnly}' applies to --cve-report only.");

        if (!values.TryGetValue("application", out var application) || string.IsNullOrWhiteSpace(application))
            return Fail($"--application is required. {HelpHint}");

        var target = RunTargets.Mecm;
        if (values.TryGetValue("target", out var targetText))
        {
            if (RunTargets.Normalize(targetText.Trim()) is not { } known)
                return Fail($"Unknown target '{targetText}'; expected {string.Join(", ", RunTargets.All)}.");
            target = known;
        }

        var onExisting = OnExistingBehavior.Skip;
        if (values.TryGetValue("on-existing", out var onExistingText))
        {
            var named = Enum.GetValues<OnExistingBehavior>()
                .Where(b => string.Equals(b.ToString(), onExistingText.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
            if (named.Count != 1)
                return Fail($"Unknown --on-existing value '{onExistingText}'; expected Skip, Overwrite or Fail.");
            onExisting = named[0];
        }

        if (values.TryGetValue("version-rule", out var ruleText)
            && !ApplicationPackagerEngine.Services.Supersedence.SupersedenceNames.TryParseRule(ruleText, out _))
            return Fail($"Unknown --version-rule value '{ruleText}'; expected exact or minimum.");
        if (values.TryGetValue("supersedence", out var modeText)
            && !ApplicationPackagerEngine.Services.Supersedence.SupersedenceNames.TryParseMode(modeText, out _))
            return Fail($"Unknown --supersedence value '{modeText}'; expected off, replace or replace-uninstall.");

        if (values.TryGetValue("version-rule", out var ruleGiven) && values.TryGetValue("supersedence", out var modeGiven)
            && ApplicationPackagerEngine.Services.Supersedence.SupersedenceNames.TryParseRule(ruleGiven, out var givenRule)
            && ApplicationPackagerEngine.Services.Supersedence.SupersedenceNames.TryParseMode(modeGiven, out var givenMode)
            && ApplicationPackagerEngine.Services.Supersedence.SupersedenceResolver.BlockedCombination(givenRule, givenMode) is { } blocked)
            return Fail(blocked);

        string? rewrite = null;
        if (values.TryGetValue("rewrite-old-versions", out var rewriteText))
        {
            rewrite = new[] { "plan", "apply", "restore" }.FirstOrDefault(w => w.Equals(rewriteText.Trim(), StringComparison.OrdinalIgnoreCase));
            if (rewrite is null) return Fail($"Unknown --rewrite-old-versions value '{rewriteText}'; expected plan, apply or restore.");
            if (Flag(values, "stage") == true || Flag(values, "package") == true)
                return Fail("--rewrite-old-versions runs alone: it does not stage or package.");
        }
        if (Flag(values, "yes") == true && rewrite is null) return Fail("Option '--yes' applies to --rewrite-old-versions only.");

        var record = Text(values, "record-installed-value");
        var accept = Flag(values, "accept-exact-block") == true;
        if (record is not null || accept)
        {
            if (record is not null && accept) return Fail("--record-installed-value and --accept-exact-block are separate runs; give one.");
            if (rewrite is not null || Flag(values, "stage") == true || Flag(values, "package") == true)
                return Fail("--record-installed-value and --accept-exact-block run alone: they do not stage, package or rewrite.");
            if (Text(values, "version") is not { } recordVersion)
                return Fail("--record-installed-value and --accept-exact-block need --version: the value belongs to one version.");
            if (!ApplicationPackagerEngine.Services.Supersedence.InstalledValueRecorder.IsValidVersionText(recordVersion))
                return Fail($"--version '{recordVersion}' is empty, longer than 64 characters, or holds a control character, a space at an end, or '|'.");
            if (Text(values, "profile") is not null)
                return Fail("--profile does not apply to --record-installed-value and --accept-exact-block: the value belongs to the title and the version.");
            if (record is not null && !ApplicationPackagerEngine.Services.Supersedence.InstalledValueRecorder.IsValidValue(record))
                return Fail($"--record-installed-value '{record}' is not a version (it starts with a digit and holds letters, digits and . + _ - only).");
        }

        if (!TryMinutes(values, "estimated-minutes", out var estimated, out var error)) return Fail(error!);
        if (!TryMinutes(values, "maximum-minutes", out var maximum, out error)) return Fail(error!);

        int? port = null;
        if (values.TryGetValue("wsus-port", out var portText))
        {
            if (!int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) || parsed is < 1 or > 65535)
                return Fail("Option '--wsus-port' takes a port number from 1 to 65535.");
            port = parsed;
        }

        var options = new BuildOptions
        {
            Application = application.Trim(),
            Profile = Text(values, "profile") ?? ApplicationIds.DefaultProfileId,
            Version = Text(values, "version"),
            Target = target,
            Stage = Flag(values, "stage") == true,
            Package = Flag(values, "package") == true,
            DownloadRoot = Text(values, "download-root"),
            EstimatedMinutes = estimated,
            MaximumMinutes = maximum,
            PackagersRoot = Text(values, "packagers-root"),
            LogFolder = Text(values, "log-folder"),
            SiteCode = Text(values, "site-code"),
            ProviderMachineName = Text(values, "provider-machine-name"),
            FileServerPath = Text(values, "file-server-path"),
            Comment = values.TryGetValue("comment", out var comment) ? comment : "",
            WsusServer = values.TryGetValue("wsus-server", out var server) ? server.Trim() : null,
            WsusPort = port,
            WsusUseSsl = Flag(values, "wsus-use-ssl"),
            WsusClassification = Text(values, "wsus-classification"),
            WsusApprovalGroup = values.TryGetValue("wsus-approval-group", out var group) ? group.Trim() : null,
            WsusDeclineSuperseded = Flag(values, "wsus-decline-superseded"),
            OnExisting = onExisting,
            VersionRule = Text(values, "version-rule"),
            Supersedence = Text(values, "supersedence"),
            RewriteOldVersions = rewrite,
            Yes = Flag(values, "yes") == true,
            RecordInstalledValue = record,
            AcceptExactBlock = accept,
            LocalSourceFolder = Text(values, "local-source-folder"),
            DataRoot = Text(values, "data-root")
        };

        if (!options.Stage && !options.Package && options.RewriteOldVersions is null && !options.IsSupersedenceRecord) return Fail("Specify --stage, --package, or both.");
        if (options.Target == RunTargets.MecmAndWsus && options.Package && !options.Stage)
            return Fail("A MECMAndWSUS package publishes the build staged in the same run; pass --stage together with --package.");
        return new ParsedArguments(options, null);
    }

    internal static ParsedArguments Fail(string error) => new(null, error);

    internal static string? Text(Dictionary<string, string> values, string name) =>
        values.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;

    internal static bool? Flag(Dictionary<string, string> values, string name) =>
        values.TryGetValue(name, out var value) ? bool.Parse(value) : null;

    private static bool TryMinutes(Dictionary<string, string> values, string name, out int? minutes, out string? error)
    {
        minutes = null;
        error = null;
        if (!values.TryGetValue(name, out var text)) return true;
        if (!int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var parsed))
        {
            error = $"Option '--{name}' takes a whole number of minutes.";
            return false;
        }
        minutes = parsed;
        return true;
    }

    public const string Usage = """
        AppPackagerBuild - stage or package one application through a workbench profile, without the window.

        Usage:
          AppPackagerBuild --application <id> [--profile <name or id>] (--stage | --package | --stage --package) [options]
          AppPackagerBuild --cve-report [CVE report options]

        Application and profile:
          --application <id>          catalog:<id>, custom:<id>, byo:<id>, or a bare definition id, definition
                                      file name or packager file name
          --profile <name or id>      Profile id, then profile name; default (the definition as shipped) when omitted
          --version <version>         BYO: stage this source revision's version. PS1 packager: pinned version.
                                      Package without --stage: the newest sealed build of this version

        Phases and target:
          --stage                     Stage under a new run snapshot
          --package                   Publish to the target. Without --stage, publishes the newest sealed build
                                      of the profile; a WSUS target stages in the same run
          --target <target>           ContentOnly, MECM (default), MECMAndIntune, IntuneOnly, MECMAndWSUS, WSUSOnly
          --on-existing <behavior>    Existing ConfigMgr application: Skip (default), Overwrite, Fail
          --version-rule <rule>       Version clause of the detection: minimum (this version or newer) or exact (this version only).
                                      Outranks the title override and the site default.
          --supersedence <mode>       off, replace or replace-uninstall (needs --version-rule exact). Outranks the title override
                                      and the site default.
          --rewrite-old-versions <w>  plan, apply or restore: changes the version clause of the OLD applications of the line from
                                      GreaterEquals to IsEquals (and back). Runs alone. plan only reads the site. apply and restore
                                      need --yes after the plan was read. Writes the log supersedence-rewrite.jsonl.
          --yes                       Confirms apply and restore of --rewrite-old-versions.
          --record-installed-value <v> Records the version that the installed product of --application and --version shows
                                      (the Exact guard compares it with the expected value). Needs --version. Runs alone.
          --accept-exact-block        Accepts the block of the Exact guard for --application and --version (the clause is then
                                      written as minimum). Needs --version. Runs alone.
          --comment <text>            Administrative comment stored on the application

        Run overrides (this build only, never written to the profile or the settings):
          --estimated-minutes <n>     Estimated installation time
          --maximum-minutes <n>       Maximum allowed run time
          --download-root <folder>    Local staging root; a non-default profile stages under profiles\<ProfileId>
          --site-code <code>          ConfigMgr site code
          --provider-machine-name <n> ConfigMgr SMS Provider
          --file-server-path <unc>    Content share root
          --local-source-folder <f>   Installer folder for a definition whose download requires a sign-in
          --packagers-root <folder>   Folder of PS1 packagers
          --wsus-server <name>        WSUS server for the WSUS targets
          --wsus-port <port>          WSUS port; without it, an SSL change takes the default port of the new scheme
          --wsus-use-ssl[=false]      Connect to WSUS with SSL
          --wsus-classification <c>   Updates, SecurityUpdates, CriticalUpdates, FeaturePacks, ServicePacks, Tools,
                                      UpdateRollups
          --wsus-approval-group <g>   Approve each new update for this computer group
          --wsus-decline-superseded[=false]
                                      Decline the AppPackager updates of the same application with a lower version

        CVE report (--cve-report): the latest and the ConfigMgr version of every application, the CVEs that the
        update of each stale application fixes, the report files and the notification. A value given here
        replaces the value of monitor-config.json for this run:
          --apps <a,b,...>            Application ids, definition file names or packager file names; all when omitted
          --skip-latest               Do not query the vendors; use the latest versions of the application inventory
          --skip-mecm                 Do not query ConfigMgr; use the ConfigMgr versions of the application inventory
          --skip-cve                  Do not look CVEs up. The report shows the results of the CVE cache
                                      and marks each as not looked up in this run
          --report-folder <folder>    Folder of the report files; Reports under the data root when omitted
          --report-formats <f,...>    Html, Csv, Json, Xlsx, Markdown; all when omitted
          --drop-folder <folder>      Copy the report files to this folder
          --webhook-url <https url>   Post the run summary as JSON to this URL
          --notify-always[=false]     Notify also when no application has an update
          --keep-report-days <n>      Delete report files older than n days; 0 keeps every file
          --config <file>             Monitor configuration; monitor-config.json under the data root when omitted.
                                      A missing file is written with default values
          --site-code, --provider-machine-name, --packagers-root, --data-root and --log-folder apply as above

        Locations:
          --data-root <folder>        Settings and workbench data root (sets APP_PACKAGER_DATA_ROOT for this run)
          --log-folder <folder>       Folder of this run's log file

          --help                      Show this text

        Exit codes:
          0  Success
          1  Failure or refusal, or a WSUS approval or decline that failed after a successful publish;
             for --cve-report, a vendor query, a ConfigMgr query or a CVE lookup that failed
          n  A PS1 packager's own non-zero exit code
        """;
}
