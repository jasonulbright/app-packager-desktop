using System.Text.RegularExpressions;

namespace ApplicationPackagerEngine.Scrapers;

/// <summary>
/// Comparable form of a vendor version string. Handles 2-4 (or more) dotted
/// parts, alphabetic part suffixes ("6.90a"), build metadata after '+'
/// ("21.0.12.1+1") and prerelease tails after '-' ("2.0.0-rc1").
///
/// Ordering rules that matter for max-by-version selection:
///   21.0.12.1+1 &gt; 21.0.12+10   (a longer core outranks build metadata)
///   6.90b       &gt; 6.90a        (part suffix breaks a numeric tie)
///   2.0.0       &gt; 2.0.0-rc1    (a release outranks its prerelease)
/// </summary>
public sealed class LooseVersion : IComparable<LooseVersion>
{
    private readonly (long Number, string Suffix)[] _core;
    private readonly long[] _build;
    private readonly string _prerelease;

    public string Original { get; }

    private LooseVersion(string original, (long, string)[] core, long[] build, string prerelease)
    {
        Original = original;
        _core = core;
        _build = build;
        _prerelease = prerelease;
    }

    /// <summary>
    /// Parses a version string. Returns null when the text carries no digits at all.
    /// </summary>
    public static LooseVersion? TryParse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var work = text.Trim();
        // Leading non-numeric decoration ("v1.2.3", "Version 1.2.3")
        var start = work.IndexOfAny("0123456789".ToCharArray());
        if (start < 0) return null;
        work = work[start..];

        var build = Array.Empty<long>();
        var plus = work.IndexOf('+');
        if (plus >= 0)
        {
            build = Regex.Matches(work[(plus + 1)..], @"\d+")
                .Select(m => long.TryParse(m.Value, out var n) ? n : 0L)
                .ToArray();
            work = work[..plus];
        }

        var prerelease = "";
        var dash = work.IndexOf('-');
        if (dash >= 0)
        {
            var tail = work[(dash + 1)..];
            // An all-numeric dash tail is more version parts ("4.2.0-20058", "1-8-2-4"),
            // not a prerelease label.
            if (Regex.IsMatch(tail, @"^[\d-]+$"))
            {
                work = work.Replace('-', '.');
            }
            else
            {
                prerelease = tail;
                work = work[..dash];
            }
        }

        var core = new List<(long, string)>();
        // Some vendors publish the parts comma-separated ("11,0,25").
        foreach (var part in work.Split('.', ','))
        {
            var m = Regex.Match(part, @"^(\d*)(.*)$");
            var number = long.TryParse(m.Groups[1].Value, out var n) ? n : 0L;
            core.Add((number, m.Groups[2].Value));
        }

        return core.Count == 0 ? null : new LooseVersion(text.Trim(), core.ToArray(), build, prerelease);
    }

    public int CompareTo(LooseVersion? other)
    {
        if (other is null) return 1;

        var len = Math.Max(_core.Length, other._core.Length);
        for (var i = 0; i < len; i++)
        {
            var a = i < _core.Length ? _core[i] : (0L, "");
            var b = i < other._core.Length ? other._core[i] : (0L, "");

            var byNumber = a.Item1.CompareTo(b.Item1);
            if (byNumber != 0) return byNumber;

            var bySuffix = string.Compare(a.Item2, b.Item2, StringComparison.OrdinalIgnoreCase);
            if (bySuffix != 0) return bySuffix;
        }

        // An empty prerelease is the release itself and outranks any prerelease tail.
        if (_prerelease.Length == 0 != (other._prerelease.Length == 0))
            return _prerelease.Length == 0 ? 1 : -1;
        var byPre = string.Compare(_prerelease, other._prerelease, StringComparison.OrdinalIgnoreCase);
        if (byPre != 0) return byPre;

        var buildLen = Math.Max(_build.Length, other._build.Length);
        for (var i = 0; i < buildLen; i++)
        {
            var a = i < _build.Length ? _build[i] : 0L;
            var b = i < other._build.Length ? other._build[i] : 0L;
            var cmp = a.CompareTo(b);
            if (cmp != 0) return cmp;
        }

        return 0;
    }

    /// <summary>
    /// Returns the highest of the given version strings, or null when none parse.
    /// Ties keep the first occurrence.
    /// </summary>
    public static string? SelectHighest(IEnumerable<string> candidates)
    {
        LooseVersion? best = null;
        foreach (var candidate in candidates)
        {
            var parsed = TryParse(candidate);
            if (parsed is null) continue;
            if (best is null || parsed.CompareTo(best) > 0) best = parsed;
        }
        return best?.Original;
    }
}
