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
            AssertSafeValue(name, value);
            return value;
        });

        if (result.Contains("{{", StringComparison.Ordinal))
            throw new InvalidOperationException($"Unresolved build token in '{text}'.");
        return result;
    }

    [GeneratedRegex(@"^[A-Za-z0-9.+_-]+$")]
    private static partial Regex SafeVersion();

    [GeneratedRegex(@"^\{[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}$")]
    private static partial Regex BracedGuid();

    // A caret escapes the next character on a cmd line; U+201F is the last character
    // of the quotation-mark block that PowerShell reads string delimiters from.
    private const string InstallerFileRefused = "%;&|<>`$\\/:*?^‟";

    /// <summary>
    /// A bound value is pasted into an operator-written script or command line as text,
    /// so each token admits only values that cannot end a quoted string, start a command
    /// or expand a variable: a version is letters, digits and . + - _; a product code
    /// is a GUID in braces; an installer file name carries no quote of any kind, no
    /// %, ;, &amp;, |, &lt;, &gt;, backtick, $, path separator or control character.
    /// </summary>
    public static void AssertSafeValue(string name, string value)
    {
        var safe = name.ToLowerInvariant() switch
        {
            "version" => SafeVersion().IsMatch(value),
            "productcode" => BracedGuid().IsMatch(value),
            "installerfile" => value.Trim().Length > 0
                && !value.Any(c => char.IsControl(c) || InstallerFileRefused.Contains(c)
                    || PowerShellLiteral.SingleQuoteCharacters.Contains(c) || PowerShellLiteral.DoubleQuoteCharacters.Contains(c)),
            _ => true
        };
        if (!safe)
            throw new InvalidOperationException(
                $"Build token '{{{{{name}}}}}' value '{value}' cannot be placed in a command or script: it carries characters outside what the token allows.");
    }

    public static string Resolve(string text, StageManifest manifest, TokenContext context = TokenContext.Command) =>
        Resolve(text, JsonSerializer.SerializeToNode(manifest), context);
}
