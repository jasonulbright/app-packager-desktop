namespace ApplicationPackagerEngine.Models;

/// <summary>
/// The content options every ConfigMgr deployment type is created with: fallback to
/// the site default boundary group, and the behavior on a neighbor or default boundary
/// group. Allow and Download produce the arguments the deployment type always carried.
/// </summary>
public sealed record DeploymentTypeContentOptions(bool ContentFallback, string SlowNetworkDeploymentMode)
{
    public const string Allow = "Allow";
    public const string Deny = "Deny";
    public const string Download = "Download";
    public const string DoNothing = "DoNothing";

    /// <summary>The variables a PS1 packager child reads for the same two options.</summary>
    public const string FallbackVariable = "APP_PACKAGER_DT_CONTENT_FALLBACK";
    public const string SlowNetworkVariable = "APP_PACKAGER_DT_SLOW_NETWORK_MODE";

    public static DeploymentTypeContentOptions Default { get; } = new(true, Download);

    /// <summary>Stored values to options. A missing value is Allow and Download; an unknown one warns and takes the same default.</summary>
    public static DeploymentTypeContentOptions Resolve(string? contentFallback, string? slowNetworkMode, Action<string>? onWarning = null)
    {
        var fallback = true;
        var fallbackRaw = (contentFallback ?? "").Trim();
        if (fallbackRaw.Equals(Deny, StringComparison.OrdinalIgnoreCase)) fallback = false;
        else if (fallbackRaw.Length > 0 && !fallbackRaw.Equals(Allow, StringComparison.OrdinalIgnoreCase))
            onWarning?.Invoke($"Content fallback '{fallbackRaw}' is not Allow or Deny; using Allow.");

        var mode = Download;
        var modeRaw = (slowNetworkMode ?? "").Trim();
        if (modeRaw.Equals(DoNothing, StringComparison.OrdinalIgnoreCase)) mode = DoNothing;
        else if (modeRaw.Length > 0 && !modeRaw.Equals(Download, StringComparison.OrdinalIgnoreCase))
            onWarning?.Invoke($"Slow network deployment mode '{modeRaw}' is not Download or DoNothing; using Download.");

        return new DeploymentTypeContentOptions(fallback, mode);
    }

    /// <summary>The cmdlet arguments, each with its leading space.</summary>
    public string CmdletArguments =>
        (ContentFallback ? " -ContentFallback" : " -ContentFallback:$false") + $" -SlowNetworkDeploymentMode {SlowNetworkDeploymentMode}";

    public string LogLine =>
        $"Deployment type content      : fallback={(ContentFallback ? Allow : Deny)}, slow network={SlowNetworkDeploymentMode}";

    /// <summary>The child environment values a PS1 packager reads.</summary>
    public IReadOnlyDictionary<string, string> EnvironmentValues => new Dictionary<string, string>
    {
        [FallbackVariable] = ContentFallback ? Allow : Deny,
        [SlowNetworkVariable] = SlowNetworkDeploymentMode
    };
}
