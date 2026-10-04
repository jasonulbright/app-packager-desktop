using System.Text.Json.Nodes;
using ApplicationPackagerEngine.Services.OneClick;

namespace ApplicationPackagerEngine.Tests.OneClick;

/// <summary>The One Click report: header table, Scope, Applications, Details, Rollback by scope, and the run list.</summary>
public sealed class OneClickReportWriterTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "apkg-oneclick-report-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private static OneClickReportRow Row(string app, string version, string outcome, string cm = "", string wsus = "", string wsusId = "",
        string reason = "", string wsusDetail = "", string cmId = "") => new()
    {
        Key = "package-" + app.Replace(" ", "").ToLowerInvariant(),
        Application = app,
        Version = version,
        Outcome = outcome,
        Reason = reason,
        Results = new Dictionary<string, string> { ["ConfigMgr"] = cm, ["WSUS"] = wsus, ["Intune"] = "" },
        Ids = new Dictionary<string, string> { ["ConfigMgr"] = cmId, ["WSUS"] = wsusId },
        Details = new Dictionary<string, string> { ["WSUS"] = wsusDetail }
    };

    [Fact]
    public void TheMarkdownAndJsonReportAreWrittenAndListed()
    {
        var folder = Path.Combine(_root, "OneClick");
        var run = new OneClickReportRun
        {
            Started = new DateTime(2026, 4, 1, 10, 0, 0), Ended = new DateTime(2026, 4, 1, 10, 12, 30),
            Operator = @"CONTOSO\operator", Computer = "WS01", Action = "Stage and Publish",
            Scope = new Dictionary<string, string> { ["ConfigMgr"] = "deploy to collection Pilot Devices", ["WSUS"] = "approved for group Pilot", ["Intune"] = "app created, not assigned" }
        };
        var rows = new[]
        {
            Row("App A", "2.0", "Published", "published App A 2.0", "published 1111-2222", "1111-2222", cmId: "App A 2.0"),
            Row("App | B", "1.0", "Not supported", wsus: "not supported: CustomInstall", reason: "WSUS: not supported"),
            Row("App C", "", "Failed", "failed: exit code 1", reason: "stage error")
        };
        var (md, json) = OneClickReportWriter.Write(run, rows, folder);

        var text = File.ReadAllText(md);
        Assert.Contains("| Published | 1 |", text);
        Assert.Contains("| Not supported | 1 |", text);
        Assert.Contains("| Failed | 1 |", text);
        Assert.Contains("- WSUS: approved for group Pilot", text);
        Assert.Contains("| App A | 2.0 | Published | published App A 2.0 | published 1111-2222 |  |  |", text);
        Assert.Contains(@"App \| B", text);
        Assert.Contains("## Rollback", text);

        var data = JsonNode.Parse(File.ReadAllText(json))!.AsObject();
        Assert.Equal(3, data["Applications"]!.AsArray().Count);
        Assert.Equal("1111-2222", (string?)data["Applications"]![0]!["WSUS"]!["Id"]);
        Assert.Equal(@"CONTOSO\operator", (string?)data["Operator"]);

        var list = OneClickReportWriter.List(folder);
        Assert.Single(list);
        Assert.Equal((1, 1, 3, md), (list[0].Published, list[0].Failed, list[0].Applications, list[0].MarkdownPath));
    }

    [Fact]
    public void TheReportCarriesTheFullRefusalTextThatTheCellShortensToItsCode()
    {
        const string full = "not supported: DetectionNotMappable: The detection only checks that a file or registry key exists.";
        var run = new OneClickReportRun { Started = new DateTime(2026, 4, 4, 10, 0, 0), Ended = new DateTime(2026, 4, 4, 10, 1, 0), Operator = "o", Computer = "c", Action = "Stage and Publish" };
        var (md, json) = OneClickReportWriter.Write(run,
        [
            Row("App B", "1.0", "Not supported", wsus: "not supported: DetectionNotMappable", reason: "WSUS: not supported", wsusDetail: full),
            Row("App A", "2.0", "Published", wsus: "published 1111-2222", wsusId: "1111-2222")
        ], Path.Combine(_root, "r4"));

        var text = File.ReadAllText(md);
        Assert.Contains("| App B | 1.0 | Not supported |  | not supported: DetectionNotMappable |", text);
        Assert.Contains("## Details", text);
        Assert.Contains("- App B, WSUS: " + full, text);
        Assert.DoesNotContain("App A, WSUS", text);
        var data = JsonNode.Parse(File.ReadAllText(json))!;
        Assert.Equal(full, (string?)data["Applications"]![0]!["WSUS"]!["Detail"]);
        Assert.Equal("", (string?)data["Applications"]![1]!["WSUS"]!["Detail"]);
    }

    [Fact]
    public void NoDetailsSectionIsWrittenWhenNoResultHasALongerMessage()
    {
        var run = new OneClickReportRun { Started = new DateTime(2026, 4, 5, 10, 0, 0), Ended = new DateTime(2026, 4, 5, 10, 1, 0), Action = "Stage and Publish" };
        var (md, _) = OneClickReportWriter.Write(run, [Row("App A", "2.0", "Published", wsus: "published 1", wsusId: "1")], Path.Combine(_root, "r5"));
        Assert.DoesNotContain("## Details", File.ReadAllText(md));
    }

    [Fact]
    public void RollbackListsOnlyTheDestinationsInTheScope()
    {
        var run = new OneClickReportRun
        {
            Started = new DateTime(2026, 4, 3, 10, 0, 0), Ended = new DateTime(2026, 4, 3, 10, 1, 0), Action = "Stage and Publish",
            Scope = new Dictionary<string, string> { ["ConfigMgr"] = "application only, no distribution, no deployment", ["WSUS"] = "published, not approved" }
        };
        var text = File.ReadAllText(OneClickReportWriter.Write(run, [], Path.Combine(_root, "r3")).MarkdownPath);
        Assert.Contains("- ConfigMgr: retire the application", text);
        Assert.Contains("- WSUS: expire the package ID", text);
        Assert.DoesNotContain("- Intune: unassign", text);
        Assert.DoesNotContain("- Intune: app created", text);
    }

    [Fact]
    public void TwoReportsStartedInOneSecondKeepBothFilesAndListNewestFirst()
    {
        var folder = Path.Combine(_root, "same-second");
        var started = new DateTime(2026, 4, 6, 9, 0, 0);
        var first = OneClickReportWriter.Write(new OneClickReportRun { Started = started, Ended = started, Action = "Report only" },
            [Row("First", "1.0", "Checked")], folder);
        var second = OneClickReportWriter.Write(new OneClickReportRun { Started = started, Ended = started, Action = "Report only" },
            [Row("Second", "1.0", "Checked")], folder);

        Assert.NotEqual(first.MarkdownPath, second.MarkdownPath);
        Assert.Equal("one-click-20260406-090000-2.md", Path.GetFileName(second.MarkdownPath));
        Assert.Contains("First", File.ReadAllText(first.MarkdownPath));
        Assert.Contains("Second", File.ReadAllText(second.MarkdownPath));
        Assert.Equal([second.JsonPath, first.JsonPath], OneClickReportWriter.List(folder).Select(i => i.JsonPath));
    }

    [Fact]
    public void MarkdownAndHtmlInAnyTextFieldRenderAsTyped()
    {
        var run = new OneClickReportRun
        {
            Started = new DateTime(2026, 4, 7, 9, 0, 0), Ended = new DateTime(2026, 4, 7, 9, 1, 0),
            Operator = "<img src=x>", Computer = "*WS01*", Action = "Stage and Publish",
            Scope = new Dictionary<string, string> { ["WSUS"] = "approved for group [Pilot](http://evil)" }
        };
        var row = Row("[Click](http://evil.example)", "1.0 <b>", "Failed", cm: "failed: `code` _x_", reason: "<script>alert(1)</script>",
            wsusDetail: "# heading & ![img](x)");

        var text = OneClickReportWriter.Markdown(run, [row]);

        Assert.Contains(@"\[Click\]\(http://evil.example\)", text);
        Assert.Contains("1.0 &lt;b&gt;", text);
        Assert.Contains(@"failed: \`code\` \_x\_", text);
        Assert.Contains("&lt;script&gt;alert\\(1\\)&lt;/script&gt;", text);
        Assert.Contains(@"\# heading &amp; \!\[img\]\(x\)", text);
        Assert.Contains("| Operator | &lt;img src=x&gt; |", text);
        Assert.Contains(@"| Computer | \*WS01\* |", text);
        Assert.Contains(@"- WSUS: approved for group \[Pilot\]\(http://evil\)", text);
        Assert.DoesNotContain("<", text.Replace("&lt;", ""));
        Assert.DoesNotContain("](", text);
    }

    [Fact]
    public void AMissingFolderListsNothingAndTheFileIsNamedByTheStartTime()
    {
        Assert.Empty(OneClickReportWriter.List(Path.Combine(_root, "none")));
        var run = new OneClickReportRun { Started = new DateTime(2026, 4, 2, 1, 2, 3), Ended = new DateTime(2026, 4, 2, 1, 2, 4), Action = "Report only" };
        Assert.Equal("one-click-20260402-010203.md", Path.GetFileName(OneClickReportWriter.Write(run, [], Path.Combine(_root, "r2")).MarkdownPath));
    }
}
