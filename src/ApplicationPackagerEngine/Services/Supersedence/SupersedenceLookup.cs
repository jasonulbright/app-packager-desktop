using System.Text;

namespace ApplicationPackagerEngine.Services.Supersedence;

/// <summary>A deployment type of an application on the site.</summary>
public sealed record LineDeploymentType(string CiId, string Name);

/// <summary>An application on the site that is an older or the new version of the application.</summary>
public sealed record LineApplication(
    string CiId, string Title, string SoftwareVersion, bool IsExpired, IReadOnlyList<LineDeploymentType> DeploymentTypes, string Publisher = "");

/// <summary>The answer of the lookup: the old application (or none), and the notes that explain what was skipped.</summary>
public sealed record LookupResult(LineApplication? Old, IReadOnlyList<string> Notes)
{
    public string Summary => Old is null
        ? "No older version found" + (Notes.Count > 0 ? ": " + string.Join(" ", Notes) : "")
        : $"Old application: {Old.Title} {Old.SoftwareVersion}";
}

/// <summary>One pair of deployment types: the new one supersedes the old one.</summary>
public sealed record DeploymentTypePair(LineDeploymentType New, LineDeploymentType Old);

/// <summary>The pairs and the deployment types that found no partner.</summary>
public sealed record PairingResult(IReadOnlyList<DeploymentTypePair> Pairs, IReadOnlyList<string> Warnings);

/// <summary>
/// The lookup of the old application and the pairing of the deployment types, as the specification describes them. The site is read by
/// a script (<see cref="ParseSiteApplications"/> reads its output); the choice is made here, in plain code, so a test can prove each rule.
/// </summary>
public static class SupersedenceLookup
{
    /// <summary>
    /// The highest version below the packaged one among every application that matches (title rule, vendor, architecture) and has another
    /// title than the new application. The highest older version is picked first: when it is retired or its version cannot be read, there is
    /// no older version to supersede and the notes say why; the next lower version (n-2) is never taken in its place. An application whose version
    /// cannot be read cannot be ranked and is named. With a <paramref name="key"/>: a vendor that the new application names must be the same
    /// vendor (<see cref="VendorKey"/>, case and legal suffixes ignored; a candidate without a vendor does not match), and a candidate whose
    /// architecture tag differs from the architecture of the new application is not a candidate.
    /// </summary>
    public static LookupResult Choose(IReadOnlyList<LineApplication> applications, string newTitle, string newVersion, SupersedenceKey? key = null)
    {
        var notes = new List<string>();
        if (!Version.TryParse(VersionCompare.Normalize(newVersion), out var newParsed))
            return new LookupResult(null, [$"The packaged version '{newVersion}' cannot be read."]);
        var newArchitecture = TitleArchitecture.Of(newTitle) ?? TitleArchitecture.Normalize(key?.Architecture);
        var older = new List<(LineApplication App, Version Version)>();
        foreach (var app in applications)
        {
            if (string.Equals(app.Title, newTitle, StringComparison.OrdinalIgnoreCase)) continue;
            if (key is not null && !VendorKey.Same(key.Publisher, app.Publisher)) continue;
            if (newArchitecture is not null && TitleArchitecture.Of(app.Title) is { } other && other != newArchitecture) continue;
            if (!Version.TryParse(VersionCompare.Normalize(app.SoftwareVersion), out var parsed))
            { notes.Add($"The version '{app.SoftwareVersion}' of '{app.Title}' cannot be read, so it was skipped."); continue; }
            if (VersionCompare.CompareStrict(app.SoftwareVersion, newVersion) >= 0)
            {
                // A line that already holds a higher or an equal version under another title: the new application is not newer than it.
                notes.Add($"'{app.Title}' {app.SoftwareVersion} is not older than {newVersion} and is not superseded.");
                continue;
            }
            older.Add((app, parsed));
        }
        _ = newParsed;
        if (older.Count == 0) return new LookupResult(null, notes);

        var ordered = older.OrderByDescending(o => o.Version).Select(o => o.App).ToList();
        var top = ordered[0];
        foreach (var other in ordered.Skip(1))
            notes.Add($"'{other.Title}' {other.SoftwareVersion} is also an older version and is not superseded by this publish.");
        if (top.IsExpired)
        {
            notes.Insert(0, $"'{top.Title}' {top.SoftwareVersion} is the highest older version and is retired, so nothing is superseded.");
            return new LookupResult(null, notes);
        }
        return new LookupResult(top, notes);
    }

    /// <summary>
    /// Pairs the deployment types of the new application with those of the old one. One and one are paired directly. With more than
    /// one, the pair is made by the suffix of the name that staging writes (<c>&lt;Title&gt; - &lt;Suffix&gt;</c>). A type with no partner
    /// gets a warning and no relation. The lookup (<see cref="Choose"/>) has already kept one architecture apart from another.
    /// </summary>
    public static PairingResult Pair(LineApplication newApplication, LineApplication oldApplication)
    {
        var pairs = new List<DeploymentTypePair>();
        var warnings = new List<string>();
        var news = newApplication.DeploymentTypes;
        var olds = oldApplication.DeploymentTypes;
        if (olds.Count == 0)
        {
            warnings.Add($"'{oldApplication.Title}' has no deployment type, so nothing is superseded.");
            return new PairingResult(pairs, warnings);
        }
        if (news.Count == 1 && olds.Count == 1)
        {
            pairs.Add(new DeploymentTypePair(news[0], olds[0]));
            return new PairingResult(pairs, warnings);
        }

        static string Suffix(string name, string title) =>
            name.StartsWith(title + " - ", StringComparison.OrdinalIgnoreCase) ? name[(title.Length + 3)..].Trim() : name.Trim();
        var unmatchedOld = olds.ToList();
        foreach (var dt in news)
        {
            var suffix = Suffix(dt.Name, newApplication.Title);
            var partner = unmatchedOld.FirstOrDefault(o => string.Equals(Suffix(o.Name, oldApplication.Title), suffix, StringComparison.OrdinalIgnoreCase));
            if (partner is null) { warnings.Add($"The deployment type '{dt.Name}' has no partner in '{oldApplication.Title}' and supersedes nothing."); continue; }
            unmatchedOld.Remove(partner);
            pairs.Add(new DeploymentTypePair(dt, partner));
        }
        foreach (var rest in unmatchedOld)
            warnings.Add($"The deployment type '{rest.Name}' of '{oldApplication.Title}' has no partner and is not superseded.");
        return new PairingResult(pairs, warnings);
    }

    public const string ApplicationLine = "APDAPP";
    public const string DeploymentTypeLine = "APDDT";

    private static string B64(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text));

    private static string FromB64(string text)
    {
        try { return Encoding.UTF8.GetString(Convert.FromBase64String(text)); }
        catch (FormatException) { return ""; }
    }

    /// <summary>Reads the lines of the site script: <c>APDAPP|ciid|expired|version|b64 title|b64 publisher</c> (the publisher is optional) and <c>APDDT|appCiid|dtCiid|b64 name</c>.</summary>
    public static IReadOnlyList<LineApplication> ParseSiteApplications(string? output)
    {
        var apps = new List<(string CiId, string Title, string Version, bool Expired, string Publisher)>();
        var dts = new List<(string AppCiId, LineDeploymentType Dt)>();
        foreach (var raw in (output ?? "").Split('\n'))
        {
            var line = raw.Trim();
            var parts = line.Split('|');
            if (parts.Length is 5 or 6 && parts[0] == ApplicationLine)
                apps.Add((parts[1], FromB64(parts[4]), parts[3], parts[2] == "1", parts.Length == 6 ? FromB64(parts[5]) : ""));
            else if (parts.Length == 4 && parts[0] == DeploymentTypeLine)
                dts.Add((parts[1], new LineDeploymentType(parts[2], FromB64(parts[3]))));
        }
        return apps.Select(a => new LineApplication(a.CiId, a.Title, a.Version, a.Expired,
            dts.Where(d => d.AppCiId == a.CiId).Select(d => d.Dt).ToList(), a.Publisher)).ToList();
    }
}
