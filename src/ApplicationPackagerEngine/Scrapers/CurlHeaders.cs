namespace ApplicationPackagerEngine.Scrapers;

/// <summary>
/// Renders a definition's request headers as curl.exe arguments. A header
/// given with -H replaces the one curl or a -A switch would send, so a
/// User-Agent entry wins over the scraper default.
/// </summary>
public static class CurlHeaders
{
    /// <summary>
    /// --compressed, then -H and "Name: value" per header, as separate argument list
    /// entries; empty for no headers. --compressed accompanies the headers because a
    /// browser-header request advertises compression and a CDN then answers with a
    /// compressed body.
    /// </summary>
    public static IReadOnlyList<string> Arguments(IReadOnlyList<string>? headers)
    {
        if (headers is null || headers.Count == 0) return [];
        var parts = new List<string> { "--compressed" };
        foreach (var header in headers)
        {
            Validate(header);
            parts.Add("-H");
            parts.Add(header.Trim());
        }
        return parts;
    }

    /// <summary>
    /// A line break would inject a second header and a double quote has no use
    /// in a header value, so both refuse the definition.
    /// </summary>
    public static void Validate(string header)
    {
        if (string.IsNullOrWhiteSpace(header) || header.IndexOf(':') <= 0)
            throw new InvalidOperationException($"Request header '{header}' is not 'Name: value'.");
        if (header.IndexOfAny(['"', '\r', '\n']) >= 0)
            throw new InvalidOperationException($"Request header '{header}' contains a double quote or a line break.");
    }

    /// <summary>Splits a validated header into name and value.</summary>
    public static (string Name, string Value) Split(string header)
    {
        Validate(header);
        var colon = header.IndexOf(':');
        return (header[..colon].Trim(), header[(colon + 1)..].Trim());
    }
}
