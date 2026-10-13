using System.Diagnostics;
using ApplicationPackagerEngine.Models.Inventory;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.Vulnerability;
using ApplicationPackagerUI.Models;

namespace ApplicationPackagerUI.Services;

/// <summary>The CVE cells and the compare status of a Packages row.</summary>
public static class CveRowState
{
    public const string UpdateAvailable = "Update available";

    /// <summary>True for "Update available" with or without a CVE count behind it.</summary>
    public static bool IsUpdateAvailable(string? status) =>
        status is not null && status.StartsWith(UpdateAvailable, StringComparison.Ordinal);

    public static void Apply(PackagerInfo row, VulnerabilityGridCells cells)
    {
        row.CveCount = cells.Count;
        row.CveCvss = cells.Cvss;
        row.CveKev = cells.Kev;
        row.CveEpss = cells.Epss;
        row.CveChecked = cells.Checked;
        row.CveFixed = cells.Fixed;
        row.CveNewest = cells.Newest;
        row.CveUpdated = cells.Updated;
    }

    /// <summary>Removes a count that described another site or another version pair.</summary>
    public static void Clear(PackagerInfo row) => Apply(row, VulnerabilityGridCells.From(null, row.Cpe));

    /// <summary>
    /// The row as the stored versions and the stored summary describe it: the CVE cells
    /// for this version pair and, when both versions are known, the compare result.
    /// </summary>
    public static void Preload(PackagerInfo row, VulnerabilitySummary? summary)
    {
        var cells = VulnerabilityGridCells.From(summary, row.Cpe, row.CurrentVersion, row.LatestVersion);
        Apply(row, cells);
        if (string.IsNullOrEmpty(row.CurrentVersion) || string.IsNullOrEmpty(row.LatestVersion)) return;
        var order = VersionCompare.Compare(row.CurrentVersion, row.LatestVersion);
        row.Status = order < 0
            ? cells.Text.Length > 0 ? $"{UpdateAvailable} ({cells.Text})" : UpdateAvailable
            : order == 0 ? "Up to date" : "Current newer";
    }

    /// <summary>Why a row gets no lookup, or null when it gets one.</summary>
    public static string? SkipReason(PackagerInfo row)
    {
        if (string.IsNullOrWhiteSpace(row.Cpe)) return VulnerabilityGridCells.CpeMissing;
        if (string.IsNullOrWhiteSpace(row.CurrentVersion)) return "No ConfigMgr version";
        if (string.IsNullOrWhiteSpace(row.LatestVersion)) return "No latest version";
        return VulnerabilityVersion.Compare(row.CurrentVersion, row.LatestVersion) >= 0 ? "No update" : null;
    }

    public static VulnerabilityReportSource ReportSource(PackagerInfo row) =>
        new(row.Application, row.Vendor, row.Script, row.CurrentVersion, row.LatestVersion, row.Status, row.Cpe, row.CveCount, row.CveChecked);
}

public enum CveRowOutcome { Skipped, Checked, Failed }

/// <summary>What one Check CVEs run shares between its rows.</summary>
public sealed class CveRunContext
{
    public required InventoryEnvironmentIdentity Environment { get; init; }
    public string ApiKey { get; init; } = "";
    public int CacheTtlMinutes { get; init; } = VulnerabilitySettings.DefaultCacheTtlMinutes;
    public bool Force { get; init; }
    /// <summary>Null uses the per-user cache file.</summary>
    public string? CachePath { get; init; }
    /// <summary>Filled by the first row that needs it.</summary>
    public IReadOnlyDictionary<string, string>? KevCatalog { get; set; }
    /// <summary>The result of each row that reached a lookup, by row Uid.</summary>
    public Dictionary<string, VulnerabilityResult> Results { get; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>The CVE lookup of one Packages row: the cells, the status text and the inventory summary.</summary>
public sealed class CveRowChecker(IVulnerabilityLookup lookup, ApplicationInventoryService inventory)
{
    public async Task<CveRowOutcome> CheckAsync(PackagerInfo row, CveRunContext context, Action<string> log, CancellationToken ct = default)
    {
        if (CveRowState.SkipReason(row) is { } reason)
        {
            CveRowState.Apply(row, new VulnerabilityGridCells { Cpe = row.Cpe, Count = reason });
            log($"Skipped: {reason}.");
            return CveRowOutcome.Skipped;
        }

        try
        {
            context.KevCatalog ??= await lookup.GetKevCatalogAsync(ct);
            var result = await lookup.GetProductVulnerabilitiesAsync(row.Cpe, row.CurrentVersion, row.LatestVersion,
                new VulnerabilityLookupOptions
                {
                    ApiKey = context.ApiKey,
                    CacheTtlMinutes = context.CacheTtlMinutes,
                    KevCatalog = context.KevCatalog,
                    Force = context.Force,
                    VendorAdvisoryUrl = row.AdvisoryUrl,
                    CachePath = context.CachePath
                }, ct);
            context.Results[row.Uid] = result;
            var summary = VulnerabilitySummary.FromResult(result);
            CveRowState.Apply(row, VulnerabilityGridCells.From(summary, row.Cpe));
            if (!string.IsNullOrEmpty(result.Error))
            {
                // A failed lookup is shown, not recorded: the inventory keeps the last good summary.
                log($"ERROR: {result.Error}");
                return CveRowOutcome.Failed;
            }

            var text = VulnerabilityText.CountText(result.Count, result.KevCount);
            if (CveRowState.IsUpdateAvailable(row.Status)) row.Status = $"{CveRowState.UpdateAvailable} ({text})";
            var cvss = result.MaxCvss is null ? "" : "; max CVSS " + VulnerabilityText.CvssText(result.MaxCvss, result.MaxSeverity);
            log($"{text} between {result.From} and {result.To} ({result.Source}){cvss}");
            try { inventory.RecordVulnerabilitySummary(row.Uid, context.Environment, row.Cpe, summary); }
            catch (Exception ex) when (ex is System.Data.Common.DbException or System.IO.IOException or ArgumentException)
            {
                log($"Inventory write failed for {row.Application}: {ex.Message}");
            }
            return CveRowOutcome.Checked;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            row.CveCount = VulnerabilityGridCells.Error;
            log($"ERROR: {ex.Message}");
            return CveRowOutcome.Failed;
        }
    }
}

/// <summary>Opens a link in the default browser. Reference and advisory text is untrusted: only an https URL leaves the application.</summary>
public static class ExternalLinks
{
    public static bool OpenHttps(string? url, Action<string>? log = null)
    {
        if (!VulnerabilityText.IsHttps(url))
        {
            log?.Invoke($"Link not opened (not https): {url}");
            return false;
        }
        try
        {
            using var process = Process.Start(new ProcessStartInfo(new Uri(url!).AbsoluteUri) { UseShellExecute = true });
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            log?.Invoke($"Link failed: {ex.Message}");
            return false;
        }
    }
}

/// <summary>Where the CVE window opens: beside the main window, so both stay readable.</summary>
public static class CveWindowPlacement
{
    private const double Gap = 8;

    /// <summary>The top-left corner for a window of the given size, inside the screen area.</summary>
    public static System.Windows.Point Beside(System.Windows.Rect owner, System.Windows.Size window, System.Windows.Rect area)
    {
        double left;
        if (owner.Right + Gap + window.Width <= area.Right) left = owner.Right + Gap;
        else if (owner.Left - Gap - window.Width >= area.Left) left = owner.Left - Gap - window.Width;
        // Neither side has room: the window takes the far edge and covers as little of the owner as it can.
        else left = owner.Left + owner.Width / 2 <= area.Left + area.Width / 2 ? area.Right - window.Width : area.Left;
        left = Math.Max(area.Left, Math.Min(left, area.Right - window.Width));
        var top = Math.Max(area.Top, Math.Min(owner.Top, area.Bottom - window.Height));
        return new System.Windows.Point(left, top);
    }
}

/// <summary>Runs each lookup on the thread pool: the cache file read and the response parse stay off the UI thread.</summary>
public sealed class BackgroundVulnerabilityLookup(IVulnerabilityLookup inner) : IVulnerabilityLookup
{
    public Task<IReadOnlyDictionary<string, string>> GetKevCatalogAsync(CancellationToken ct = default) =>
        Task.Run(() => inner.GetKevCatalogAsync(ct), ct);

    public Task<VulnerabilityResult> GetProductVulnerabilitiesAsync(string cpe, string currentVersion, string latestVersion,
        VulnerabilityLookupOptions? options = null, CancellationToken ct = default) =>
        Task.Run(() => inner.GetProductVulnerabilitiesAsync(cpe, currentVersion, latestVersion, options, ct), ct);
}
