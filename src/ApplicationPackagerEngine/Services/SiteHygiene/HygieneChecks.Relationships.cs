namespace ApplicationPackagerEngine.Services;

internal static partial class HygieneChecks
{
    private static void AddRelationships(List<HygieneCheckFamily> list)
    {
    }

    /// <summary>The content source path of each latest deployment type: SMS_DeploymentType.ContentId joined to SMS_Content.ContentUniqueID.</summary>
    internal static IReadOnlyList<(string AppModel, string DtModel, string DtName, string Path)> ContentLocations(HygieneSiteRead data)
    {
        var sources = data.Table(HygieneSets.AppContent).Records
            .GroupBy(r => r.S("ContentUniqueID"), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().S("ContentSource").Trim(), StringComparer.OrdinalIgnoreCase);
        var list = new List<(string, string, string, string)>();
        foreach (var dt in data.Table(HygieneSets.DeploymentTypes).Records)
        {
            if (!dt.B("IsLatest")) continue;
            if (!sources.TryGetValue(dt.S("ContentId"), out var path) || string.IsNullOrWhiteSpace(path)) continue;
            list.Add((dt.S("AppModelName"), dt.S("ModelName"), dt.S("LocalizedDisplayName"), path));
        }
        return list;
    }
}
