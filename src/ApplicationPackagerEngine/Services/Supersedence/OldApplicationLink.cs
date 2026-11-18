using System.Text;

namespace ApplicationPackagerEngine.Services.Supersedence;

/// <summary>Why an application of the site is or is not offered as the old application of a line.</summary>
public enum OldApplicationState
{
    /// <summary>It can be linked.</summary>
    Linkable,
    /// <summary>It already holds the marker of this line.</summary>
    Linked,
    /// <summary>It holds the marker of another line.</summary>
    OtherLine,
    /// <summary>It has keywords of its own, which a write would replace.</summary>
    OwnKeywords,
    /// <summary>Its keywords could not be read, so nothing is written.</summary>
    KeywordsUnreadable,
    /// <summary>It is retired.</summary>
    Retired,
    /// <summary>Its version cannot be read.</summary>
    VersionUnreadable,
    /// <summary>It is not older than the new application, so the lookup would not choose it.</summary>
    NotOlder
}

/// <summary>An application as the site search read it. <see cref="Keywords"/> is null when the keywords could not be read.</summary>
public sealed record SiteApplicationRecord(string Title, string SoftwareVersion, bool IsExpired, IReadOnlyList<string>? Keywords);

/// <summary>One row of the list of the Old Application window.</summary>
public sealed record OldApplicationCandidate(
    string Title, string SoftwareVersion, OldApplicationState State, string Reason, bool CanLink, bool CanClear);

/// <summary>The result of a link or a removal: whether it ended well, and the sentence for the administrator.</summary>
public sealed record OldApplicationOutcome(bool Ok, string Message);

/// <summary>
/// The older-application override ("Use as old application"). The lookup of the old application takes only applications that carry the
/// identity marker of the line. An application that an earlier release published, or one that an administrator made by hand, has none, so
/// the lookup never chooses it. The administrator links it once: the program shows the title and the version, asks, and then writes the
/// marker of the line into the keywords of that application. The link is the marker on the site; nothing is kept in the settings, so the
/// next publish of the line, from this PC or another, finds the application through the same lookup. The rules are in plain code, so a
/// test can prove each one.
/// </summary>
public static class OldApplicationRules
{
    /// <summary>
    /// The applications of the site that can be linked as the old application of a line, with the reason for each one that cannot. An
    /// application with the title of the new one is never listed: the lookup never returns it. The linkable ones come first, the newest
    /// version first; the others follow by title. <c>CanClear</c> is true for an application whose keywords are the marker of this line and
    /// nothing else.
    /// </summary>
    public static IReadOnlyList<OldApplicationCandidate> Classify(
        IReadOnlyList<SiteApplicationRecord> applications, string marker, string newTitle, string newVersion)
    {
        var rows = new List<OldApplicationCandidate>();
        var newVersionReadable = Version.TryParse(VersionCompare.Normalize(newVersion), out _);
        foreach (var app in applications)
        {
            if (string.Equals(app.Title, newTitle, StringComparison.OrdinalIgnoreCase)) continue;
            OldApplicationCandidate Row(OldApplicationState state, string reason, bool canLink = false, bool canClear = false) =>
                new(app.Title, app.SoftwareVersion, state, reason, canLink, canClear);

            if (app.Keywords is null)
            {
                rows.Add(Row(OldApplicationState.KeywordsUnreadable, "The keywords could not be read, so nothing is written."));
                continue;
            }
            var keywords = app.Keywords.Where(k => !string.IsNullOrEmpty(k)).ToList();
            var mine = keywords.Count(k => string.Equals(k, marker, StringComparison.Ordinal));
            var markers = keywords.Count(IdentityMarker.IsMarker);
            var own = keywords.Count(k => !IdentityMarker.IsMarker(k));
            if (mine > 0)
            {
                rows.Add(Row(OldApplicationState.Linked, "It already holds the marker of this line.", canClear: own == 0 && markers == 1));
                continue;
            }
            if (markers > 0) { rows.Add(Row(OldApplicationState.OtherLine, "It holds the marker of another line.")); continue; }
            if (own > 0) { rows.Add(Row(OldApplicationState.OwnKeywords, "It has keywords of its own, which a write would replace.")); continue; }
            if (app.IsExpired) { rows.Add(Row(OldApplicationState.Retired, "It is retired.")); continue; }
            if (!Version.TryParse(VersionCompare.Normalize(app.SoftwareVersion), out _))
            {
                rows.Add(Row(OldApplicationState.VersionUnreadable, $"The version '{app.SoftwareVersion}' cannot be read."));
                continue;
            }
            if (!newVersionReadable || VersionCompare.CompareStrict(app.SoftwareVersion, newVersion) >= 0)
            {
                rows.Add(Row(OldApplicationState.NotOlder, $"It is not older than {newVersion}, so the lookup would not choose it."));
                continue;
            }
            rows.Add(Row(OldApplicationState.Linkable, "", canLink: true));
        }
        var linkable = rows.Where(r => r.CanLink)
            .OrderByDescending(r => Version.Parse(VersionCompare.Normalize(r.SoftwareVersion))).ToList();
        var others = rows.Where(r => !r.CanLink).OrderBy(r => r.Title, StringComparer.OrdinalIgnoreCase).ToList();
        return [.. linkable, .. others];
    }

    /// <summary>What the administrator reads before the marker is written: the title and the version of the application first.</summary>
    public static string LinkConfirmationText(string title, string version, string newTitle) =>
        $"Use '{title}' version {version} as the old application of '{newTitle}'?\n\n" +
        $"The program writes the identity marker of this product line into the keywords of '{title}'. The next publish of '{newTitle}' with a " +
        "supersedence mode other than Off then finds it and supersedes it. Its deployment types, detection and deployments are not changed.";

    public static string ClearConfirmationText(string title, string version) =>
        $"Remove the identity marker from '{title}' version {version}?\n\n" +
        "The next publish of this line no longer finds it as an older application, so nothing supersedes it. If a publish of this program " +
        "marked it, link it again with Use as Old Application when you want it back.";

    /// <summary>
    /// The decision about a link, from the keywords that were read again just before the write. A null answer means "write the marker".
    /// An application that holds keywords of its own, or the marker of another line, is refused, and one that already holds the marker
    /// needs no write.
    /// </summary>
    public static OldApplicationOutcome? DecideLink(string applicationName, IReadOnlyList<string> keywords, string marker)
    {
        if (keywords.Any(k => string.Equals(k, marker, StringComparison.Ordinal)))
            return new(true, $"'{applicationName}' already holds the marker of this line.");
        if (keywords.Any(IdentityMarker.IsMarker))
            return new(false, $"'{applicationName}' holds the marker of another line, so it is not changed.");
        if (keywords.Count > 0)
            return new(false, $"'{applicationName}' has keywords of its own, which a write would replace, so it is not changed.");
        return null;
    }

    /// <summary>Only an application whose keywords are the marker of this line and nothing else is changed.</summary>
    public static bool CanClear(IReadOnlyList<string> keywords, string marker) =>
        keywords.Count == 1 && string.Equals(keywords[0], marker, StringComparison.Ordinal);
}

/// <summary>The scripts of the older-application override and the reader of what they print.</summary>
public static class OldApplicationScripts
{
    public const string ApplicationLine = "APDOLD";
    public const string KeywordLine = "APDKW";
    public const string MissingLine = "APDMISSING";
    public const string AmbiguousLine = "APDAMBIGUOUS";

    private static string Q(string text) => PowerShellLiteral.EscapeSingleQuoted(text);

    private const string ReadTagsBlock =
        "$tags = @()\n" +
        "$digest = [Microsoft.ConfigurationManagement.ApplicationManagement.Serialization.SccmSerializer]::DeserializeFromString([string]$app.SDMPackageXML, $true)\n" +
        "foreach ($info in $digest.DisplayInfo) { foreach ($tag in $info.Tags) { $tags += [string]$tag } }\n" +
        "$tags = @($tags | Where-Object { $_ })\n";

    /// <summary>
    /// Reads the applications whose name contains the text, with their keywords. The wildcard characters of the text are literal. The
    /// text limits the read: a site holds many applications, and a list of all of them is not a choice. One line for each:
    /// <c>APDOLD|expired|b64 version|b64 title|ok or unreadable|b64 keywords (one for each line)</c>.
    /// </summary>
    public static string BuildSearchScript(string siteCode, string? providerMachine, string nameContains)
    {
        var sb = new StringBuilder();
        MecmTarget.AppendCmPreamble(sb, siteCode, providerMachine);
        sb.AppendLine($"$pattern = '*' + [System.Management.Automation.WildcardPattern]::Escape('{Q(nameContains.Trim())}') + '*'");
        sb.AppendLine("function ConvertTo-B64([string]$text) { [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($text)) }");
        sb.AppendLine("foreach ($app in @(Get-CMApplication -Name $pattern -ErrorAction Stop)) {");
        sb.AppendLine("    $state = 'ok'");
        sb.AppendLine("    $tags = @()");
        sb.AppendLine("    try {");
        sb.Append(ReadTagsBlock);
        sb.AppendLine("    } catch { $state = 'unreadable'; $tags = @() }");
        sb.AppendLine("    $expired = if ($app.IsExpired) { 1 } else { 0 }");
        sb.AppendLine($"    Write-Host (\"{ApplicationLine}|{{0}}|{{1}}|{{2}}|{{3}}|{{4}}\" -f $expired, (ConvertTo-B64 ([string]$app.SoftwareVersion)), (ConvertTo-B64 ([string]$app.LocalizedDisplayName)), $state, (ConvertTo-B64 ($tags -join \"`n\")))");
        sb.AppendLine("}");
        return sb.ToString();
    }

    /// <summary>Reads the keywords of one application by its exact name: <c>APDKW|b64 keywords</c>, or <c>APDMISSING</c>, or <c>APDAMBIGUOUS</c>.</summary>
    public static string BuildKeywordsScript(string siteCode, string? providerMachine, string applicationName)
    {
        var sb = new StringBuilder();
        MecmTarget.AppendCmPreamble(sb, siteCode, providerMachine);
        sb.AppendLine($"$found = @(Get-CMApplication -Name '{Q(applicationName)}' -DisableWildcardHandling -ErrorAction Stop)");
        sb.AppendLine($"if ($found.Count -eq 0) {{ Write-Host '{MissingLine}'; exit 0 }}");
        sb.AppendLine($"if ($found.Count -gt 1) {{ Write-Host '{AmbiguousLine}'; exit 0 }}");
        sb.AppendLine("$app = $found[0]");
        sb.Append(ReadTagsBlock);
        sb.AppendLine($"Write-Host (\"{KeywordLine}|{{0}}\" -f [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($tags -join \"`n\")))");
        return sb.ToString();
    }

    /// <summary>
    /// Writes the marker as the keyword of the application. The script reads the keywords again and writes only when there are none, so
    /// a keyword that an administrator added after the read is never replaced.
    /// </summary>
    public static string BuildLinkScript(string siteCode, string? providerMachine, string applicationName, string marker)
    {
        var sb = new StringBuilder();
        MecmTarget.AppendCmPreamble(sb, siteCode, providerMachine);
        sb.AppendLine($"$app = Get-CMApplication -Name '{Q(applicationName)}' -DisableWildcardHandling -ErrorAction Stop");
        sb.Append(ReadTagsBlock);
        sb.AppendLine("if ($tags.Count -gt 0) { throw 'the application has keywords now, which a write would replace' }");
        sb.AppendLine($"Set-CMApplication -InputObject $app -Keyword '{Q(marker)}' -ErrorAction Stop");
        sb.AppendLine($"Write-Host 'Identity marker             : {Q(applicationName)}'");
        return sb.ToString();
    }

    /// <summary>Empties the keywords of the application, only when they are the marker and nothing else.</summary>
    public static string BuildClearScript(string siteCode, string? providerMachine, string applicationName, string marker)
    {
        var sb = new StringBuilder();
        MecmTarget.AppendCmPreamble(sb, siteCode, providerMachine);
        sb.AppendLine($"$app = Get-CMApplication -Name '{Q(applicationName)}' -DisableWildcardHandling -ErrorAction Stop");
        sb.Append(ReadTagsBlock);
        sb.AppendLine($"if ($tags.Count -ne 1 -or $tags[0] -cne '{Q(marker)}') {{ throw 'the keywords are not the marker of this line alone' }}");
        sb.AppendLine("Set-CMApplication -InputObject $app -Keyword '' -ErrorAction Stop");
        sb.AppendLine($"Write-Host 'Identity marker removed      : {Q(applicationName)}'");
        return sb.ToString();
    }

    private static string FromB64(string text)
    {
        try { return Encoding.UTF8.GetString(Convert.FromBase64String(text)); }
        catch (FormatException) { return ""; }
    }

    private static IReadOnlyList<string> Lines(string text) =>
        text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    /// <summary>Reads the lines of <see cref="BuildSearchScript"/>.</summary>
    public static IReadOnlyList<SiteApplicationRecord> ParseSearch(string? output)
    {
        var records = new List<SiteApplicationRecord>();
        foreach (var raw in (output ?? "").Split('\n'))
        {
            var parts = raw.Trim().Split('|');
            if (parts.Length != 6 || parts[0] != ApplicationLine) continue;
            records.Add(new SiteApplicationRecord(FromB64(parts[3]), FromB64(parts[2]), parts[1] == "1",
                parts[4] == "ok" ? Lines(FromB64(parts[5])) : null));
        }
        return records;
    }

    /// <summary>Reads the answer of <see cref="BuildKeywordsScript"/>: the keywords, or null with the problem when the application is missing or not unique.</summary>
    public static (IReadOnlyList<string>? Keywords, string? Problem) ParseKeywords(string? output, string applicationName)
    {
        foreach (var raw in (output ?? "").Split('\n'))
        {
            var line = raw.Trim();
            if (line == MissingLine) return (null, $"The application '{applicationName}' is not on the site.");
            if (line == AmbiguousLine) return (null, $"More than one application on the site has the title '{applicationName}', so nothing is changed.");
            var parts = line.Split('|');
            if (parts.Length == 2 && parts[0] == KeywordLine) return (Lines(FromB64(parts[1])), null);
        }
        return (null, $"The keywords of '{applicationName}' could not be read: the site gave no answer.");
    }
}

/// <summary>Runs one script against the site: the exit code, the output and the error text.</summary>
public delegate Task<(int ExitCode, string StdOut, string StdErr)> SiteScriptRunner(string script, CancellationToken ct);

/// <summary>
/// Searches the site, links and unlinks the old application of one line. The site is reached through a <see cref="SiteScriptRunner"/>, so
/// a test can answer for it. Nothing is written without <c>confirmed</c>.
/// </summary>
public sealed class OldApplicationService(string siteCode, string? providerMachine, string marker, SiteScriptRunner? runner = null)
{
    private readonly SiteScriptRunner _run = runner ?? ((script, ct) => WindowsPowerShellHost.RunAsync(script, null, ct, "ap-oldapp"));

    public string Marker => marker;

    /// <summary>The applications whose name contains the text, classified for the line.</summary>
    /// <exception cref="InvalidOperationException">The text is empty, or the site could not be read.</exception>
    public async Task<IReadOnlyList<OldApplicationCandidate>> SearchAsync(string newTitle, string newVersion, string nameContains, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(nameContains)) throw new InvalidOperationException("Type part of the application name to search for.");
        var result = await _run(OldApplicationScripts.BuildSearchScript(siteCode, providerMachine, nameContains), ct);
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"The site could not be read: {SupersedenceScripts.FailureText(result.StdOut, result.StdErr)}");
        return OldApplicationRules.Classify(OldApplicationScripts.ParseSearch(result.StdOut), marker, newTitle, newVersion);
    }

    private async Task<(IReadOnlyList<string>? Keywords, string? Problem)> ReadKeywordsAsync(string applicationName, CancellationToken ct)
    {
        var read = await _run(OldApplicationScripts.BuildKeywordsScript(siteCode, providerMachine, applicationName), ct);
        if (read.ExitCode != 0)
            return (null, $"The keywords of '{applicationName}' could not be read: {SupersedenceScripts.FailureText(read.StdOut, read.StdErr)}");
        return OldApplicationScripts.ParseKeywords(read.StdOut, applicationName);
    }

    /// <summary>Links an application as the old application of the line: writes the marker into its keywords.</summary>
    public async Task<OldApplicationOutcome> LinkAsync(string applicationName, bool confirmed, CancellationToken ct)
    {
        if (!confirmed) return new(false, "The link is written only after the administrator confirms it.");
        var (keywords, problem) = await ReadKeywordsAsync(applicationName, ct);
        if (keywords is null) return new(false, problem!);
        if (OldApplicationRules.DecideLink(applicationName, keywords, marker) is { } decided) return decided;
        var write = await _run(OldApplicationScripts.BuildLinkScript(siteCode, providerMachine, applicationName, marker), ct);
        if (write.ExitCode != 0)
            return new(false, $"The marker was not written: {SupersedenceScripts.FailureText(write.StdOut, write.StdErr)}");
        return new(true, $"'{applicationName}' is the old application of this line.");
    }

    /// <summary>Takes the marker off an application, only when it is the one keyword of that application.</summary>
    public async Task<OldApplicationOutcome> ClearAsync(string applicationName, bool confirmed, CancellationToken ct)
    {
        if (!confirmed) return new(false, "The marker is removed only after the administrator confirms it.");
        var (keywords, problem) = await ReadKeywordsAsync(applicationName, ct);
        if (keywords is null) return new(false, problem!);
        if (!OldApplicationRules.CanClear(keywords, marker))
            return new(false, $"The keywords of '{applicationName}' are not the marker of this line alone, so they are not changed.");
        var write = await _run(OldApplicationScripts.BuildClearScript(siteCode, providerMachine, applicationName, marker), ct);
        if (write.ExitCode != 0)
            return new(false, $"The marker was not removed: {SupersedenceScripts.FailureText(write.StdOut, write.StdErr)}");
        return new(true, $"The marker is removed from '{applicationName}'.");
    }
}
