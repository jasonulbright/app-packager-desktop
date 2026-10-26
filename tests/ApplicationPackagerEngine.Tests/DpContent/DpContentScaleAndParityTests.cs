using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests.DpContent;

public sealed class DpContentScaleAndParityTests
{
    [Fact]
    public async Task ALoadOf300DpsAnd30000RowsIsOneScriptRunWithFifteenQueries()
    {
        var state = new JsonObject
        {
            ["Options"] = new JsonObject(),
            ["Generate"] = new JsonObject { ["Dps"] = 300, ["Packages"] = 100, ["Pairs"] = 30_000 }
        };
        using var h = new DpContentHarness(state);
        var snapshot = await h.Service.LoadAsync(new Progress<string>(), CancellationToken.None);

        Assert.Single(h.Site.Runs);
        Assert.Equal(15, h.Site.Queries());
        Assert.Equal(300, snapshot.Dps.Count);
        Assert.Equal(100, snapshot.Content.Count);
        Assert.Equal(30_000, snapshot.Pairs.Count);
        Assert.False(snapshot.Truncated);
        var failed = Enumerable.Range(0, 30_000).Count(i => i % 97 == 0);
        var inProgress = Enumerable.Range(0, 30_000).Count(i => i % 97 != 0 && i % 89 == 0);
        Assert.Equal(failed, snapshot.Dps.Sum(d => d.Failed));
        Assert.Equal(inProgress, snapshot.Content.Sum(c => c.InProgress));
        Assert.Equal(30_000 - failed - inProgress, snapshot.Dps.Sum(d => d.Installed));
        Assert.All(snapshot.Dps, d => Assert.Equal(100, d.Total));
        Assert.All(snapshot.Content, c => Assert.Equal(300, c.DpsTargeted));
        Assert.All(snapshot.Dps, d => Assert.Equal(Math.Round(100 * 1024.0 / 1024 / 1024, 2), d.TotalSizeGb));
    }

    [Fact]
    public void TheRollupIsLinearInTheRowCount()
    {
        static DpContentSiteRead Rows(int n)
        {
            var read = new DpContentSiteRead();
            for (var d = 0; d < 300; d++) read.Dps.Add(new DpContentSiteRead.Dp(StubDpContentSite.NalOf($"DP{d:D3}.contoso.com"), $"DP{d:D3}", "MCM", false, "Windows NT Server"));
            for (var c = 0; c < 1000; c++) read.Contents.Add(new DpContentSiteRead.Content($"G{c:D7}", $"C{c}", DpContentTypes.Package, 10, $"G{c:D7}"));
            for (var i = 0; i < n; i++) read.Summaries.Add(new DpContentSiteRead.Summary($"G{i / 300 % 1000:D7}", read.Dps[i % 300].Nal, i % 9, 1, null, 0, "MCM"));
            return read;
        }
        var small = Rows(30_000);
        var large = Rows(300_000);
        var t1 = Stopwatch.StartNew();
        var a = DpContentAggregator.Snapshot(small, DpContentAggregator.Pairs(small), 30_000, DateTime.UtcNow);
        t1.Stop();
        var t2 = Stopwatch.StartNew();
        var b = DpContentAggregator.Snapshot(large, DpContentAggregator.Pairs(large), 300_000, DateTime.UtcNow);
        t2.Stop();
        Assert.Equal(30_000, a.Dps.Sum(d => d.Total));
        Assert.Equal(300_000, b.Dps.Sum(d => d.Total));
        Assert.True(t2.Elapsed < TimeSpan.FromSeconds(30), "A rollup of 300,000 rows must not grow faster than linearly.");
    }

    // ---- Parity with the source module's rollups ------------------------------------------------

    private const string SourceModule = @"C:\projects\dp-content-manager\Module\DPContentMgrCommon.psm1";

    private static readonly (string Id, string Dp, int State)[] Fixed =
    [
        ("MCM000A6", "CM01.contoso.com", 0), ("MCM000A6", "CM01.contoso.com.backup", 8), ("MCM000A6", "DP10.contoso.com", 3),
        ("MCM000A7", "CM01.contoso.com", 7), ("MCM000A7", "DP10.contoso.com", 1), ("MCM000A7", "DP1.contoso.com", 2),
        ("MCM000A8", "CM01.contoso.com", 4), ("MCM000A8", "DP1.contoso.com", 5), ("MCM000A8", "DP10.contoso.com", 6),
        ("MCM00009", "DP1.contoso.com", 9), ("MCM00009", "CM01.contoso.com", 0), ("MCM00009", "DP10.contoso.com", 0),
        ("MCM00010", "CM01.contoso.com", 0), ("MCM00010", "DP1.contoso.com", 0), ("MCM00010", "DP10.contoso.com", 0)
    ];

    /// <summary>The source's rollup of <see cref="Fixed"/>: per content TotalDPs, Installed, InProgress, Failed, PctComplete; per DP the same.</summary>
    private static readonly Dictionary<string, (int Total, int Installed, int InProgress, int Failed, string Pct)> FallbackContent = new()
    {
        ["MCM000A6"] = (3, 2, 0, 1, "66.7%"), ["MCM000A7"] = (3, 0, 3, 0, "0%"), ["MCM000A8"] = (3, 0, 2, 1, "0%"),
        ["MCM00009"] = (3, 2, 0, 0, "66.7%"), ["MCM00010"] = (3, 3, 0, 0, "100%")
    };

    private static readonly Dictionary<string, (int Total, int Installed, int InProgress, int Failed, string Pct)> FallbackDp = new()
    {
        ["CM01.contoso.com"] = (5, 3, 2, 0, "60%"), ["CM01.contoso.com.backup"] = (1, 1, 0, 0, "100%"),
        ["DP10.contoso.com"] = (5, 2, 1, 2, "40%"), ["DP1.contoso.com"] = (4, 1, 2, 0, "25%")
    };

    private static (Dictionary<string, (int, int, int, int, string)> Content, Dictionary<string, (int, int, int, int, string)> Dps)? RunSource()
    {
        if (!File.Exists(SourceModule)) return null;
        var folder = Path.Combine(Path.GetTempPath(), "apkg-dpcparity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var rows = Fixed.Select(r => new { PackageID = r.Id, DPName = r.Dp, State = r.State });
            File.WriteAllText(Path.Combine(folder, "rows.json"), JsonSerializer.Serialize(rows));
            var script = Path.Combine(folder, "parity.ps1");
            File.WriteAllText(script, """
                param([string]$Module, [string]$Folder)
                $ErrorActionPreference = 'Stop'
                Import-Module $Module -Force -DisableNameChecking
                $rows = @(Get-Content -LiteralPath (Join-Path $Folder 'rows.json') -Raw | ConvertFrom-Json | ForEach-Object { $_ })
                $content = @(ConvertTo-ContentStatusSummary -StatusRows $rows | ForEach-Object { [pscustomobject]@{ K = $_.PackageID; T = $_.TotalDPs; I = $_.InstalledCount; P = $_.InProgressCount; F = $_.FailedCount; C = $_.PctComplete } })
                $dps = @(ConvertTo-DPStatusSummary -StatusRows $rows | ForEach-Object { [pscustomobject]@{ K = $_.DPName; T = $_.TotalContent; I = $_.InstalledCount; P = $_.InProgressCount; F = $_.FailedCount; C = $_.PctComplete } })
                [IO.File]::WriteAllText((Join-Path $Folder 'out.json'), (ConvertTo-Json -InputObject ([pscustomobject]@{ Content = $content; Dps = $dps }) -Depth 4 -Compress))
                """, new System.Text.UTF8Encoding(true));
            var psi = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
            foreach (var a in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script, "-Module", SourceModule, "-Folder", folder }) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            var error = p.StandardError.ReadToEndAsync();
            _ = p.StandardOutput.ReadToEnd();
            Assert.True(p.WaitForExit(120_000), "The source module did not finish.");
            var outPath = Path.Combine(folder, "out.json");
            Assert.True(File.Exists(outPath), "The source module wrote no result: " + error.Result);
            using var doc = JsonDocument.Parse(File.ReadAllText(outPath));
            static Dictionary<string, (int, int, int, int, string)> Map(JsonElement list) => list.EnumerateArray().ToDictionary(
                e => e.GetProperty("K").GetString()!, e => (e.GetProperty("T").GetInt32(), e.GetProperty("I").GetInt32(), e.GetProperty("P").GetInt32(), e.GetProperty("F").GetInt32(), e.GetProperty("C").GetString()!));
            return (Map(doc.RootElement.GetProperty("Content")), Map(doc.RootElement.GetProperty("Dps")));
        }
        finally
        {
            try { Directory.Delete(folder, true); } catch (IOException) { }
        }
    }

    private static DpContentSnapshot Ours()
    {
        var read = new DpContentSiteRead();
        foreach (var dp in Fixed.Select(r => r.Dp).Distinct()) read.Dps.Add(new DpContentSiteRead.Dp(StubDpContentSite.NalOf(dp), dp, "MCM", false, "Windows NT Server"));
        foreach (var id in Fixed.Select(r => r.Id).Distinct()) read.Contents.Add(new DpContentSiteRead.Content(id, id, DpContentTypes.Package, 1, id));
        foreach (var r in Fixed) read.Summaries.Add(new DpContentSiteRead.Summary(r.Id, StubDpContentSite.NalOf(r.Dp), r.State, 1, null, 0, "MCM"));
        return DpContentAggregator.Snapshot(read, DpContentAggregator.Pairs(read), Fixed.Length, DateTime.UtcNow);
    }

    private static string Pct(double percent) => percent.ToString("0.#", CultureInfo.InvariantCulture) + "%";

    [Fact]
    public void TheContentAndDpRollupsEqualTheSourceModule()
    {
        var source = RunSource();
        var content = source?.Content ?? FallbackContent.ToDictionary(p => p.Key, p => (p.Value.Total, p.Value.Installed, p.Value.InProgress, p.Value.Failed, p.Value.Pct));
        var dps = source?.Dps ?? FallbackDp.ToDictionary(p => p.Key, p => (p.Value.Total, p.Value.Installed, p.Value.InProgress, p.Value.Failed, p.Value.Pct));
        var ours = Ours();
        var compared = 0;
        foreach (var (id, s) in content)
        {
            var row = ours.Content.Single(c => c.PackageId == id);
            Assert.Equal(s, (row.DpsTargeted, row.Installed, row.InProgress, row.Failed, Pct(row.PercentOk)));
            compared++;
        }
        foreach (var (name, s) in dps)
        {
            var row = ours.Dps.Single(d => d.DpName == name);
            Assert.Equal(s, (row.Total, row.Installed, row.InProgress, row.Failed, Pct(row.PercentOk)));
            compared++;
        }
        Assert.Equal(9, compared);
        if (source is not null)
        {
            Assert.Equal(FallbackContent.Count, source.Value.Content.Count);
            foreach (var (id, v) in FallbackContent) Assert.Equal(v, source.Value.Content[id]);
            foreach (var (id, v) in FallbackDp) Assert.Equal(v, source.Value.Dps[id]);
        }
    }

    [Fact]
    public void ThePercentRoundsHalfAwayFromZeroWhereTheSourceRoundsToEven()
    {
        Assert.Equal(6.3, DpContentRollup.PercentOk(1, 16));
        Assert.Equal(0.1, DpContentRollup.PercentOk(1, 2000));
        Assert.Equal(99.9, DpContentRollup.PercentOk(1999, 2001));
        Assert.Equal(0, DpContentRollup.PercentOk(0, 0));
    }

    [Theory]
    [InlineData("[\"Display=\\\\CM01.contoso.com\\\"]MSWNET:[\"SMS_SITE=MCM\"]\\\\CM01.contoso.com\\", "CM01.contoso.com")]
    [InlineData("[\"Display=\\\\pull01.contoso.com\\\"]MSWNET:[\"SMS_SITE=PS1\"]\\\\pull01.contoso.com\\", "pull01.contoso.com")]
    [InlineData("[\"Display=\\\\contoso.cloudapp.net\\\"]MSWNET:[\"SMS_SITE=MCM\"]\\\\contoso.cloudapp.net\\", "contoso.cloudapp.net")]
    [InlineData("[\"Display=\\\\Old display name\\\"]MSWNET:[\"SMS_SITE=MCM\"]\\\\DP02.contoso.com\\", "DP02.contoso.com")]
    [InlineData("no nal path", "no nal path")]
    public void TheServerNameIsTheEndOfTheNalPath(string nal, string server) => Assert.Equal(server, DpContentAggregator.ServerOf(nal));

    [Theory]
    [InlineData(0, false, false, false)]
    [InlineData(8, false, false, false)]
    [InlineData(1, true, false, true)]
    [InlineData(2, true, false, true)]
    [InlineData(7, true, false, true)]
    [InlineData(4, true, false, true)]
    [InlineData(5, true, false, true)]
    [InlineData(3, true, true, false)]
    [InlineData(6, true, true, false)]
    public void TheStateMapFollowsTheSummarizerClass(int state, bool issue, bool failed, bool inProgress)
    {
        var pair = new PairRow("MCM000A6", "x", DpContentTypes.Package, "CM01", StubDpContentSite.Nal, state, DpContentStates.Name(state), "1", null);
        Assert.Equal(issue, pair.IsIssue);
        Assert.Equal(failed, pair.IsFailed);
        Assert.Equal(inProgress, pair.IsInProgress);
        var withError = pair with { MessageState = 4, MessageId = 2306 };
        Assert.True(withError.IsFailed);
        Assert.True(withError.IsIssue);
        Assert.False(withError.IsInProgress);
    }
}
