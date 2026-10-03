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
    public string? LocalSourceFolder { get; init; }
    public string? DataRoot { get; init; }
    public bool Help { get; init; }
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
        "stage", "package", "wsus-use-ssl", "wsus-decline-superseded", "help"
    };

    private static readonly HashSet<string> Valued = new(StringComparer.OrdinalIgnoreCase)
    {
        "application", "profile", "version", "target", "download-root", "estimated-minutes", "maximum-minutes",
        "packagers-root", "log-folder", "site-code", "provider-machine-name", "file-server-path", "comment",
        "wsus-server", "wsus-port", "wsus-classification", "wsus-approval-group", "on-existing",
        "local-source-folder", "data-root"
    };

    /// <summary>
    /// Options that name a path, a root, a server or a site. An empty value, such as an unset
    /// variable in a scheduled task, would otherwise fall back to the saved setting and run
    /// against the real profile and site.
    /// </summary>
    private static readonly string[] NonEmpty =
    [
        "data-root", "download-root", "packagers-root", "log-folder", "local-source-folder", "file-server-path",
        "site-code", "provider-machine-name", "wsus-server"
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
            LocalSourceFolder = Text(values, "local-source-folder"),
            DataRoot = Text(values, "data-root")
        };

        if (!options.Stage && !options.Package) return Fail("Specify --stage, --package, or both.");
        if (options.Target == RunTargets.MecmAndWsus && options.Package && !options.Stage)
            return Fail("A MECMAndWSUS package publishes the build staged in the same run; pass --stage together with --package.");
        return new ParsedArguments(options, null);
    }

    private static ParsedArguments Fail(string error) => new(null, error);

    private static string? Text(Dictionary<string, string> values, string name) =>
        values.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;

    private static bool? Flag(Dictionary<string, string> values, string name) =>
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

        Locations:
          --data-root <folder>        Settings and workbench data root (sets APP_PACKAGER_DATA_ROOT for this run)
          --log-folder <folder>       Folder of this run's log file

          --help                      Show this text

        Exit codes:
          0  Success
          1  Failure or refusal, or a WSUS approval or decline that failed after a successful publish
          n  A PS1 packager's own non-zero exit code
        """;
}
