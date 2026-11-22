namespace ApplicationPackagerEngine.Services;

/// <summary>Where history.db lives: results\history.db of the active site, else the single-site file in the SRLPackr folder.</summary>
public static class SiteHistoryDatabase
{
    public const string FileName = "history.db";

    public static string DefaultPath() =>
        SiteContext.Resolve(Path.Combine(SiteContext.Areas.Results, FileName), () => Path.Combine(AppDataPaths.LegacyRoot, FileName));
}
