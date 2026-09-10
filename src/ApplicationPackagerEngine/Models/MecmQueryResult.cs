namespace ApplicationPackagerEngine.Models;

/// <summary>How a site query ended.</summary>
public enum MecmQueryStatus
{
    /// <summary>
    /// The query did not reach a usable answer, so the site said nothing. This is the
    /// default so that a result nobody filled in cannot pass for an answer.
    /// </summary>
    Failed = 0,
    /// <summary>The site was queried and carries the application.</summary>
    Found = 1,
    /// <summary>The site was queried and does not carry the application.</summary>
    NotFound = 2
}

/// <summary>
/// One site query's outcome. A failed query is distinct from a successful one that found
/// nothing: only the second establishes that the site does not carry the application, and
/// only the second may clear a recorded version.
/// </summary>
public sealed class MecmQueryResult
{
    public MecmQueryStatus Status { get; init; }
    public string? DisplayName { get; init; }
    public string? SoftwareVersion { get; init; }
    public int MatchCount { get; init; }

    /// <summary>Why the query failed; null unless Status is Failed.</summary>
    public string? FailureReason { get; init; }

    public bool Found => Status == MecmQueryStatus.Found;
    public bool Failed => Status == MecmQueryStatus.Failed;

    public static MecmQueryResult NotFound() => new() { Status = MecmQueryStatus.NotFound };

    public static MecmQueryResult Failure(string reason) =>
        new() { Status = MecmQueryStatus.Failed, FailureReason = reason };
}

/// <summary>
/// Reads a query process's result. The query script suppressed its own errors and reported
/// them as ordinary output, so a site that could not be reached looked exactly like a site
/// that answered "no".
/// </summary>
public static class MecmQueryClassifier
{
    public static MecmQueryResult Classify(int exitCode, string? stdout, string? stderr)
    {
        var text = (stdout ?? "").Trim();

        // The script writes exactly one record. Anything beyond it means the run did
        // something other than answer the question, including a second record that
        // contradicts the first.
        var records = text.Split('\n')
            .Select(l => l.Trim().TrimEnd('\r'))
            .Where(l => l.Length > 0)
            .ToArray();

        if (records.Length == 0)
            return Unrecognised(exitCode, text, stderr);

        if (records.Length > 1)
            return MecmQueryResult.Failure(
                $"query returned {records.Length} records (exit {exitCode}): {records[0]}");

        var record = records[0];

        if (record.StartsWith("ERROR:", StringComparison.Ordinal))
            return MecmQueryResult.Failure(record);

        // Whole-token matches only. A line that merely begins with the sentinel is not
        // the sentinel.
        if (record.Equals("NOT_FOUND", StringComparison.Ordinal))
        {
            return exitCode == 0
                ? MecmQueryResult.NotFound()
                : MecmQueryResult.Failure($"query exited {exitCode}");
        }

        if (record.StartsWith("FOUND|", StringComparison.Ordinal))
            return ClassifyFound(exitCode, record);

        return Unrecognised(exitCode, text, stderr);
    }

    /// <summary>
    /// A found record is the script's complete shape: the marker, a usable display name,
    /// a software version field that may be empty, and a positive match count. A short or
    /// unparseable record is a malformed answer, not a smaller one.
    /// </summary>
    private static MecmQueryResult ClassifyFound(int exitCode, string record)
    {
        if (exitCode != 0) return MecmQueryResult.Failure($"query exited {exitCode}");

        var parts = record.Split('|');
        if (parts.Length != 4)
            return MecmQueryResult.Failure($"malformed FOUND record with {parts.Length} field(s): {record}");

        var displayName = parts[1].Trim();
        if (displayName.Length == 0)
            return MecmQueryResult.Failure($"FOUND record carries no application name: {record}");

        if (!int.TryParse(parts[3].Trim(), out var matchCount) || matchCount < 1)
            return MecmQueryResult.Failure($"FOUND record carries no usable match count: {record}");

        return new MecmQueryResult
        {
            Status = MecmQueryStatus.Found,
            DisplayName = displayName,
            // An application with no version recorded on the site is a real answer.
            SoftwareVersion = parts[2],
            MatchCount = matchCount
        };
    }

    private static MecmQueryResult Unrecognised(int exitCode, string stdout, string? stderr)
    {
        var detail = !string.IsNullOrWhiteSpace(stderr) ? FirstLine(stderr!)
            : stdout.Length > 0 ? FirstLine(stdout)
            : "no output";
        return MecmQueryResult.Failure($"unexpected query result (exit {exitCode}): {detail}");
    }

    private static string FirstLine(string text) =>
        text.Split('\n')[0].Trim().TrimEnd('\r');
}
