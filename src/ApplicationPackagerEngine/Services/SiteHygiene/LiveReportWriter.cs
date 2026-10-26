namespace ApplicationPackagerEngine.Services;

/// <summary>
/// Writes a Live grid or a Trends series through <see cref="HygieneReportWriter"/>: the same reports folder,
/// the CSV formula guard, the HTML encoding, the new-file rule and the notes in a separate .about.txt beside
/// the CSV file. The file name starts with site-hygiene-live.
/// </summary>
public sealed class LiveReportWriter(HygieneReportWriter? writer = null) : ILiveReportWriter
{
    private readonly HygieneReportWriter _writer = writer ?? new HygieneReportWriter();

    public string Folder => _writer.Folder;

    internal static HygieneReport Map(LiveReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        return new HygieneReport("Live " + report.View, report.Columns, report.Rows, report.Truncated, report.Note)
        {
            Filter = report.Filter,
            ScannedUtc = report.ReadUtc,
            TimeLabel = "Read"
        };
    }

    public string WriteCsv(LiveReport report) => _writer.WriteCsv(Map(report));

    public string WriteHtml(LiveReport report) => _writer.WriteHtml(Map(report));
}
