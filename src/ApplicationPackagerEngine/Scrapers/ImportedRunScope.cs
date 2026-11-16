namespace ApplicationPackagerEngine.Scrapers;

/// <summary>
/// The run of a definition that came from a vendor feed ("Imported definitions run with no ambient credentials"). While the scope is active, in the
/// same logical flow, a request carries no ambient credential (not the GitHub token of the administrator, from the environment, the settings or the
/// gh CLI) and every curl call is limited to HTTPS with its redirects (--proto =https --proto-redir =https).
/// </summary>
public static class ImportedRunScope
{
    private static readonly AsyncLocal<int> Depth = new();

    /// <summary>True while a definition of a vendor feed runs in this flow.</summary>
    public static bool Active => Depth.Value > 0;

    private sealed class Scope : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Depth.Value--;
        }
    }

    /// <summary>Enters the scope for an imported definition; for any other definition the scope does not change.</summary>
    public static IDisposable? EnterFor(Models.AppDefinition definition) =>
        definition.SourceOrigin == Models.Workbench.ApplicationOrigin.Imported ? Enter() : null;

    public static IDisposable Enter()
    {
        Depth.Value++;
        return new Scope();
    }
}