namespace ApplicationPackagerEngine.Services;

public sealed partial class SiteHygieneService
{
    private sealed record PreparedChange(HygieneFixKind Kind, IReadOnlyList<string> Keys, DateTime PreparedUtc);

    public Task<HygieneChange> PrepareAsync(HygieneChange request, CancellationToken ct) => throw new NotSupportedException();

    public Task<HygieneOutcome> ApplyAsync(HygieneChange change, IProgress<HygieneOutcome> progress, CancellationToken ct) => throw new NotSupportedException();
}
