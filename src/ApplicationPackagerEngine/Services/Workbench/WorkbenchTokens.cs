using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ApplicationPackagerEngine.Models;

namespace ApplicationPackagerEngine.Services.Workbench;

/// <summary>Where a bound text runs, which decides how {{ContentRoot}} names the content folder.</summary>
public enum TokenContext
{
    /// <summary>A BAT command line: the content root is %~dp0.</summary>
    Command,

    /// <summary>A PowerShell script body: the content root is $PSScriptRoot.</summary>
    Script
}

/// <summary>
/// Binds the build tokens a profile-authored command or script body may use:
/// {{InstallerFile}}, {{Version}}, {{ProductCode}} and {{ContentRoot}}.
/// </summary>
public static partial class WorkbenchTokens
{
    public static readonly IReadOnlyList<string> Names = ["InstallerFile", "Version", "ProductCode", "ContentRoot"];

    [GeneratedRegex(@"\{\{\s*([A-Za-z]+)\s*\}\}")]
    private static partial Regex TokenPattern();

    /// <summary>
    /// Binds every token against the staged manifest. An unknown token, a token
    /// with no value at build time, and any brace pair left after binding throw:
    /// a literal brace pair reaching a deployed command is a defect, never a default.
    /// </summary>
    public static string Resolve(string text, JsonNode? manifestData, TokenContext context = TokenContext.Command)
    {
        if (string.IsNullOrEmpty(text)) return text;

        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["InstallerFile"] = WorkbenchJson.Get(manifestData, "InstallerFile").Text,
            ["Version"] = WorkbenchJson.Get(manifestData, "SoftwareVersion").Text,
            ["ProductCode"] = WorkbenchJson.Get(manifestData, "ProductCode").Text,
            ["ContentRoot"] = context == TokenContext.Script ? "$PSScriptRoot" : "%~dp0"
        };

        var result = TokenPattern().Replace(text, match =>
        {
            var name = match.Groups[1].Value;
            if (!values.TryGetValue(name, out var value))
                throw new InvalidOperationException(
                    $"Unknown build token '{{{{{name}}}}}'; supported tokens are {string.Join(", ", Names)}.");
            if (string.IsNullOrWhiteSpace(value))
                throw new InvalidOperationException(
                    $"Build token '{{{{{name}}}}}' has no value at build time; the stage did not resolve it.");
            return value;
        });

        if (result.Contains("{{", StringComparison.Ordinal))
            throw new InvalidOperationException($"Unresolved build token in '{text}'.");
        return result;
    }

    public static string Resolve(string text, StageManifest manifest, TokenContext context = TokenContext.Command) =>
        Resolve(text, JsonSerializer.SerializeToNode(manifest), context);
}
