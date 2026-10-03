using ApplicationPackagerUI.Models;
using ApplicationPackagerUI.Services;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// Stage and Package publishes only the rows a newer vendor version brought in. A row
/// restaged because it has no sealed build record, or a stale one, is staged and not
/// published: its version is already in ConfigMgr.
/// </summary>
public class OneClickPublishSelectionTests
{
    private static PackagerInfo Row(string name, string status, string latest, string current) =>
        new() { Application = name, Status = status, LatestVersion = latest, CurrentVersion = current };

    [Fact]
    public void ARowRestagedForAMissingBuildRecordIsNotPublished()
    {
        var newer = Row("Newer", "Staged", "2.0", "1.0");
        var noRecord = Row("No record", "Staged", "3.1", "3.1");
        var failed = Row("Failed", "Stage error", "4.0", "3.0");
        var versionUpdates = new HashSet<PackagerInfo> { newer, failed };

        var publishable = OneClickPublishSelection.Publishable([newer, noRecord, failed], versionUpdates).ToList();

        Assert.Equal([newer], publishable);
    }

    [Fact]
    public void AnUpgradeWhereEveryRowOnlyLacksABuildRecordPublishesNothing()
    {
        var rows = new[] { Row("A", "Staged", "1.0", "1.0"), Row("B", "Staged", "2.0", "2.0") };

        Assert.Empty(OneClickPublishSelection.Publishable(rows, new HashSet<PackagerInfo>()));
    }
}
