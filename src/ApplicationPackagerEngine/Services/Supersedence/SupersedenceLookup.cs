using System.Text;

namespace ApplicationPackagerEngine.Services.Supersedence;

/// <summary>A deployment type of an application on the site.</summary>
public sealed record LineDeploymentType(string CiId, string Name);

/// <summary>An application on the site that carries the identity marker of the line.</summary>
public sealed record LineApplication(
    string CiId, string Title, string SoftwareVersion, bool IsExpired, IReadOnlyList<LineDeploymentType> DeploymentTypes);

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
    /// The newest application with the marker whose title is not the title of the new application, that is not retired, and whose
    /// version is lower than the packaged version. A version that cannot be parsed is skipped and named.
    /// </summary>
    public static LookupResult Choose(IReadOnlyList<LineApplication> applications, string newTitle, string newVersion)
    {
        var notes = new List<string>();
        var candidates = new List<LineApplication>();
        foreach (var app in applications)
        {
            if (string.Equals(app.Title, newTitle, StringComparison.OrdinalIgnoreCase)) continue;
            if (app.IsExpired) { notes.Add($"'{app.Title}' is retired and was skipped."); continue; }
            if (!Version.TryParse(VersionCompare.Normalize(app.SoftwareVersion), out _))
            { notes.Add($"The version '{app.SoftwareVersion}' of '{app.Title}' cannot be read, so it was skipped."); continue; }
            if (VersionCompare.CompareStrict(app.SoftwareVersion, newVersion) >= 0)
            {
                // A line that already holds a higher or an equal version under another title: the new application is not newer than it.
                notes.Add($"'{app.Title}' {app.SoftwareVersion} is not older than {newVersion} and is not superseded.");
                continue;
            }
            candidates.Add(app);
        }
        if (!Version.TryParse(VersionCompare.Normalize(newVersion), out _))
            return new LookupResult(null, [$"The packaged version '{newVersion}' cannot be read."]);
        if (candidates.Count == 0) return new LookupResult(null, notes);

        var ordered = candidates.OrderByDescending(a => Version.Parse(VersionCompare.Normalize(a.SoftwareVersion))).ToList();
        foreach (var other in ordered.Skip(1))
            notes.Add($"'{other.Title}' {other.SoftwareVersion} also carries the marker and is not superseded by this publish.");
        return new LookupResult(ordered[0], notes);
    }

    /// <summary>
    /// Pairs the deployment types of the new application with those of the old one. One and one are paired directly. With more than
    /// one, the pair is made by the suffix of the name that staging writes (<c>&lt;Title&gt; - &lt;Suffix&gt;</c>). A type with no partner
    /// gets a warning and no relation. The program never links one architecture to another: the marker holds the architecture.
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

    /// <summary>Reads the lines of the site script: <c>APDAPP|ciid|expired|version|b64 title</c> and <c>APDDT|appCiid|dtCiid|b64 name</c>.</summary>
    public static IReadOnlyList<LineApplication> ParseSiteApplications(string? output)
    {
        var apps = new List<(string CiId, string Title, string Version, bool Expired)>();
        var dts = new List<(string AppCiId, LineDeploymentType Dt)>();
        foreach (var raw in (output ?? "").Split('\n'))
        {
            var line = raw.Trim();
            var parts = line.Split('|');
            if (parts.Length == 5 && parts[0] == ApplicationLine)
                apps.Add((parts[1], FromB64(parts[4]), parts[3], parts[2] == "1"));
            else if (parts.Length == 4 && parts[0] == DeploymentTypeLine)
                dts.Add((parts[1], new LineDeploymentType(parts[2], FromB64(parts[3]))));
        }
        return apps.Select(a => new LineApplication(a.CiId, a.Title, a.Version, a.Expired,
            dts.Where(d => d.AppCiId == a.CiId).Select(d => d.Dt).ToList())).ToList();
    }
}
