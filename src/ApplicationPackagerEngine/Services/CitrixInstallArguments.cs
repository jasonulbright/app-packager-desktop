using ApplicationPackagerEngine.Models;

namespace ApplicationPackagerEngine.Services;

/// <summary>
/// Builds the CitrixWorkspaceApp.exe argument list for one release stream.
/// Every value is validated against the vendor's documented set; a value
/// outside it, or a setting the stream documents no switch for, is skipped
/// with a warning rather than passed to the installer.
/// </summary>
public static class CitrixInstallArguments
{
    public const string Current = "Current";
    public const string Ltsr = "LTSR";

    private static readonly string[] ComponentNames =
        ["ReceiverInside", "ICA_Client", "AM", "SelfService", "DesktopViewer", "WebHelper", "BCR_Client", "USB", "SSON"];

    public sealed record Result(IReadOnlyList<string> Arguments, IReadOnlyList<string> Warnings);

    /// <summary>
    /// Normalizes a stream name to Current or LTSR; anything else throws.
    /// </summary>
    public static string NormalizeStream(string? stream) =>
        stream?.Trim() switch
        {
            { } s when s.Equals(Current, StringComparison.OrdinalIgnoreCase) => Current,
            { } s when s.Equals(Ltsr, StringComparison.OrdinalIgnoreCase) => Ltsr,
            _ => throw new InvalidOperationException($"Citrix switch stream '{stream}' is not Current or LTSR.")
        };

    public static Result Build(CitrixWorkspaceSwitches? config, string stream)
    {
        stream = NormalizeStream(stream);
        var arguments = new List<string> { "/silent" };
        var warnings = new List<string>();
        if (config is null) return new Result(arguments, warnings);

        var install = config.Installation;
        if (install is not null)
        {
            if (install.CleanInstall == true) arguments.Add("/CleanInstall");
            // ENABLE_SSON takes effect only together with /includeSSON.
            if (install.IncludeSSON == true)
            {
                arguments.Add("/includeSSON");
                if (install.EnableSSON is { } sson) arguments.Add("ENABLE_SSON=" + (sson ? "Yes" : "No"));
            }
            if (install.AppProtection == true) arguments.Add("startAppProtection");
            if (install.SessionPreLaunch is { } preLaunch) arguments.Add("ENABLEPRELAUNCH=" + (preLaunch ? "True" : "False"));
            if (install.SelfServiceMode is { } selfService) arguments.Add("SELFSERVICEMODE=" + (selfService ? "True" : "False"));
        }

        var plugins = config.Plugins;
        if (plugins is not null)
        {
            var addons = new List<string>();
            if (stream == Current)
            {
                if (plugins.MSTeamsPlugin is { } teams) arguments.Add("/InstallMSTeamsPlugin=" + (teams ? "Y" : "N"));
                if (plugins.ZoomPlugin == false) arguments.Add("Installzoomplugin=N");
            }
            else
            {
                if (plugins.MSTeamsPlugin is not null)
                    warnings.Add("Plugins.MSTeamsPlugin has no documented LTSR installer switch; ignored.");
                if (plugins.ZoomPlugin == true) addons.Add("ZoomVDIPlugin");
            }
            if (plugins.EPAClient == false) arguments.Add("InstallEPAClient=N");
            if (plugins.WebExPlugin == true) addons.Add("WebexVDIPlugin");
            if (addons.Count > 0) arguments.Add("ADDONS=" + string.Join(",", addons));

            if (plugins.UberAgent == true)
            {
                arguments.Add("/InstallUberAgent");
                if (plugins.UberAgentSkipUpgrade == true) arguments.Add("/SkipUberAgentUpgrade");
            }
            else if (plugins.UberAgentSkipUpgrade == true)
            {
                warnings.Add("Plugins.UberAgentSkipUpgrade applies only with Plugins.UberAgent; ignored.");
            }

            if (plugins.SessionRecording == true)
            {
                if (stream == Current) arguments.Add("/InstallSRAgent");
                else warnings.Add("Plugins.SessionRecording has no documented LTSR installer switch; ignored.");
            }
        }

        var update = config.UpdateAndTelemetry;
        if (update is not null)
        {
            var autoUpdate = (update.AutoUpdateCheck ?? "").Trim().ToLowerInvariant();
            if (autoUpdate.Length > 0)
            {
                if (autoUpdate is "auto" or "manual" or "disabled") arguments.Add("AutoUpdateCheck=" + autoUpdate);
                else warnings.Add($"UpdateAndTelemetry.AutoUpdateCheck '{update.AutoUpdateCheck}' is not auto, manual or disabled; ignored.");
            }
            if (update.EnableCEIP is { } ceip) arguments.Add("EnableCEIP=" + (ceip ? "True" : "False"));
            if (update.EnableTracing is { } tracing) arguments.Add("EnableTracing=" + (tracing ? "true" : "false"));
        }

        var policy = config.StorePolicy;
        if (policy is not null)
        {
            foreach (var (name, value, @switch) in new[]
                     {
                         ("AllowAddStore", policy.AllowAddStore, "ALLOWADDSTORE"),
                         ("AllowSavePwd", policy.AllowSavePwd, "ALLOWSAVEPWD")
                     })
            {
                var normalized = (value ?? "").Trim().ToUpperInvariant();
                if (normalized.Length == 0) continue;
                if (normalized is "S" or "A" or "N") arguments.Add($"{@switch}={normalized}");
                else warnings.Add($"StorePolicy.{name} '{value}' is not S, A or N; ignored.");
            }
        }

        var store = config.Store;
        if (store is not null)
        {
            var storeName = (store.Name ?? "").Trim();
            var storeUrl = (store.Url ?? "").Trim();
            if (storeName.Length > 0 || storeUrl.Length > 0)
            {
                if (storeName.Length == 0 || storeUrl.Length == 0)
                    warnings.Add("Store needs both Name and Url; STORE0 omitted.");
                else if (storeName.IndexOfAny([';', '"']) >= 0)
                    warnings.Add("Store.Name contains a semicolon or double quote; STORE0 omitted.");
                else if (!Uri.TryCreate(storeUrl, UriKind.Absolute, out var parsed)
                         || (parsed.Scheme != Uri.UriSchemeHttps && parsed.Scheme != Uri.UriSchemeHttp)
                         || storeUrl.Any(c => c is ';' or '"' || char.IsWhiteSpace(c)))
                    warnings.Add($"Store.Url '{storeUrl}' is not an absolute http or https URL; STORE0 omitted.");
                else
                {
                    var storeValue = $"{storeName};{storeUrl};On;{storeName}";
                    // The wrapper hands the argument string to Start-Process, which adds no quotes.
                    if (storeValue.Any(char.IsWhiteSpace)) storeValue = "\"" + storeValue + "\"";
                    arguments.Add("STORE0=" + storeValue);
                }
            }
        }

        // ADDLOCAL restricts the install to the listed components, so it is
        // emitted only when the operator opted in.
        var components = config.Components;
        if (components?.Customize == true)
        {
            var selected = ComponentNames.Where(n => ComponentEnabled(components, n)).ToList();
            if (selected.Count > 0) arguments.Add("ADDLOCAL=" + string.Join(",", selected));
            else warnings.Add("Components.Customize is set but no component is enabled; ADDLOCAL omitted.");
        }

        return new Result(arguments, warnings);
    }

    private static bool ComponentEnabled(CwaComponents c, string name) => name switch
    {
        "ReceiverInside" => c.ReceiverInside == true,
        "ICA_Client" => c.ICA_Client == true,
        "AM" => c.AM == true,
        "SelfService" => c.SelfService == true,
        "DesktopViewer" => c.DesktopViewer == true,
        "WebHelper" => c.WebHelper == true,
        "BCR_Client" => c.BCR_Client == true,
        "USB" => c.USB == true,
        "SSON" => c.SSON == true,
        _ => false
    };
}
