using ApplicationPackagerEngine.Models;

namespace ApplicationPackagerEngine.Scrapers;

/// <summary>
/// Determines the newest version by probing a versioned download URL. Covers
/// vendors that publish no version index and state no version on the page, where
/// the download host answering 200 is the only published version fact.
///
/// Url is a download URL template containing {version}. Pattern is the floor
/// version: the oldest release the walk may settle on. The walk starts there and
/// at each hop tries the next patch, then the first patch of the next minor, then
/// of the next major; the first candidate the host serves becomes the new current.
/// It stops when no candidate answers, or after MaxVersionHops hops.
///
/// A floor that no longer answers 200 fails the scrape rather than silently
/// reporting a version the host does not serve.
/// </summary>
public sealed class HeadProbeScraper : IVersionScraper
{
    public string Type => "HeadProbe";

    private const int DefaultMaxHops = 12;

    public async Task<string?> GetLatestVersionAsync(VersionScraperConfig config, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(config.Url) || string.IsNullOrWhiteSpace(config.Pattern))
            return null;

        var current = ParseTriple(config.Pattern.Trim());
        if (current is null) return null;

        if (!await IsPublishedAsync(config.Url, Format(current.Value), ct))
            return null;

        var maxHops = config.MaxVersionHops > 0 ? config.MaxVersionHops : DefaultMaxHops;

        for (var hop = 0; hop < maxHops; hop++)
        {
            var (major, minor, patch) = current.Value;
            var candidates = new[]
            {
                (major, minor, patch + 1),
                (major, minor + 1, 1),
                (major + 1, 0, 1),
                (major + 1, 1, 1)
            };

            (int, int, int)? next = null;
            foreach (var candidate in candidates)
            {
                ct.ThrowIfCancellationRequested();
                if (await IsPublishedAsync(config.Url, Format(candidate), ct))
                {
                    next = candidate;
                    break;
                }
            }

            if (next is null) break;
            current = next;
        }

        return Format(current.Value);
    }

    /// <summary>
    /// The newest build number the host serves for a template with a {build} placeholder,
    /// starting from <paramref name="floor"/>. Each hop tries the next three numbers, so a
    /// withdrawn build or two does not end the walk. Null when the floor is not a whole
    /// number or the host no longer serves it.
    /// </summary>
    public static async Task<int?> WalkBuildNumberAsync(string template, string floor, int maxHops,
        Func<string, CancellationToken, Task<bool>>? isPublished = null, CancellationToken ct = default)
    {
        if (!int.TryParse(floor?.Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var current))
            return null;
        isPublished ??= IsUrlPublishedAsync;
        if (!await isPublished(BuildNumberUrl(template, current), ct)) return null;

        for (var hop = 0; hop < (maxHops > 0 ? maxHops : DefaultMaxHops); hop++)
        {
            int? next = null;
            for (var step = 1; step <= 3; step++)
            {
                ct.ThrowIfCancellationRequested();
                if (await isPublished(BuildNumberUrl(template, current + step), ct))
                {
                    next = current + step;
                    break;
                }
            }
            if (next is null) break;
            current = next.Value;
        }
        return current;
    }

    /// <summary>The template with {build} replaced by the build number.</summary>
    public static string BuildNumberUrl(string template, int build) =>
        template.Replace("{build}", build.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase);

    private static string Format((int Major, int Minor, int Patch) v)
        => $"{v.Major}.{v.Minor}.{v.Patch}";

    public static (int, int, int)? ParseTriple(string version)
    {
        var parts = version.Split('.');
        if (parts.Length != 3) return null;
        if (!int.TryParse(parts[0], out var major)) return null;
        if (!int.TryParse(parts[1], out var minor)) return null;
        if (!int.TryParse(parts[2], out var patch)) return null;
        return (major, minor, patch);
    }

    /// <summary>
    /// Builds the probe URL for a candidate version. Exposed for tests so the
    /// walk's substitution can be checked without network access.
    /// </summary>
    public static string BuildUrl(string template, string version)
        => template.Replace("{version}", version, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Header-only request; the payload runs to megabytes and the walk issues
    /// one request per candidate.
    /// </summary>
    private static Task<bool> IsPublishedAsync(string template, string version, CancellationToken ct) =>
        IsUrlPublishedAsync(BuildUrl(template, version), ct);

    private static async Task<bool> IsUrlPublishedAsync(string url, CancellationToken ct)
    {
        using var process = CurlProcess.Start(
            ["-L", "--silent", "--show-error", "--head", "--max-time", "20", "-o", "NUL", "-w", "%{http_code}"], url);

        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var exitTask = process.WaitForExitAsync(ct);
        var completed = await Task.WhenAny(exitTask, Task.Delay(TimeSpan.FromSeconds(25), ct));

        if (completed != exitTask)
        {
            try { process.Kill(true); } catch { }
            return false;
        }

        var stdout = (await stdoutTask).Trim();
        return stdout == "200";
    }
}
