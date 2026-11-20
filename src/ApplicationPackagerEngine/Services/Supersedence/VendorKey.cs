using System.Text.RegularExpressions;

namespace ApplicationPackagerEngine.Services.Supersedence;

/// <summary>
/// The vendor text for the compare of the older-version lookup, the same rule as the PowerShell app: letter case, punctuation and a
/// trailing legal suffix (Inc, LLC, Ltd, Corp, Corporation, GmbH, Co, AG, SA, BV and a few more) do not count. "Bitwarden, Inc." is the
/// vendor "bitwarden". A vendor that is only a suffix word keeps it ("Inc" is "inc").
/// </summary>
public static partial class VendorKey
{
    [GeneratedRegex(@"[^\p{L}\p{N}]+", RegexOptions.CultureInvariant)]
    private static partial Regex NotLetterOrDigit();

    [GeneratedRegex(@"^(.+?) (inc|incorporated|llc|ltd|limited|corp|corporation|gmbh|co|company|ag|sa|bv|plc)$", RegexOptions.CultureInvariant)]
    private static partial Regex TrailingSuffix();

    public static string Of(string? vendor)
    {
        var text = NotLetterOrDigit().Replace((vendor ?? "").ToLowerInvariant(), " ").Trim();
        while (TrailingSuffix().Match(text) is { Success: true } match) text = match.Groups[1].Value;
        return text;
    }

    /// <summary>True when both name the same vendor. A new application that names no vendor compares with every candidate (as the PowerShell app does).</summary>
    public static bool Same(string? newVendor, string? candidateVendor) =>
        string.IsNullOrWhiteSpace(newVendor) || Of(newVendor) == Of(candidateVendor);
}

/// <summary>The architecture tag of an application title ("(x64)", "(x86)", "(arm64)") in one spelling, so two titles can be compared.</summary>
public static partial class TitleArchitecture
{
    [GeneratedRegex(@"\(\s*(x64|x86|arm64|amd64|32-bit|64-bit)\s*\)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Tag();

    /// <summary>The architecture that the last tag of the title names (x64, x86 or arm64), or null when the title has none.</summary>
    public static string? Of(string? title)
    {
        var matches = Tag().Matches(title ?? "");
        return matches.Count == 0 ? null : Normalize(matches[^1].Groups[1].Value);
    }

    /// <summary>x64, x86 or arm64 for a spelling of one of them; null for anything else (including blank).</summary>
    public static string? Normalize(string? value) => (value ?? "").Trim().ToLowerInvariant() switch
    {
        "x64" or "amd64" or "64-bit" => "x64",
        "x86" or "32-bit" => "x86",
        "arm64" => "arm64",
        _ => null
    };
}
