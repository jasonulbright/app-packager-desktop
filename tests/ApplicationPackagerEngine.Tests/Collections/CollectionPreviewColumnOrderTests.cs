using ApplicationPackagerEngine.Services;
using static ApplicationPackagerEngine.Tests.Collections.StubSitePowerShell;

namespace ApplicationPackagerEngine.Tests.Collections;

/// <summary>The preview shows its columns in the order of the query's select list, not in the provider's property order.</summary>
public sealed class CollectionPreviewColumnOrderTests
{
    [Theory]
    [InlineData("select ResourceID, Name from SMS_R_System", new[] { "Name", "ResourceID" }, new[] { "ResourceID", "Name" })]
    [InlineData("SELECT  name ,resourceid FROM SMS_R_System", new[] { "ResourceID", "Name" }, new[] { "Name", "ResourceID" })]
    [InlineData("select SMS_R_System.Name, SMS_G_System_OS.Caption, SMS_R_System.ResourceId from SMS_R_System inner join SMS_G_System_OS on SMS_G_System_OS.ResourceID = SMS_R_System.ResourceId",
        new[] { "SMS_G_System_OS.Caption", "SMS_R_System.Name", "SMS_R_System.ResourceId" }, new[] { "SMS_R_System.Name", "SMS_G_System_OS.Caption", "SMS_R_System.ResourceId" })]
    [InlineData("select SMS_R_System.Name, ResourceId from SMS_R_System", new[] { "Name", "ResourceId" }, new[] { "Name", "ResourceId" })]
    [InlineData("select distinct Name, Client from SMS_R_System where Name in (select Name, Build from SMS_R_User)", new[] { "Client", "Name" }, new[] { "Name", "Client" })]
    [InlineData("select * from SMS_R_System", new[] { "Active", "Name" }, new[] { "Active", "Name" })]
    [InlineData("select Name, Missing from SMS_R_System", new[] { "Extra", "Name" }, new[] { "Name", "Extra" })]
    [InlineData("select Name from SMS_R_System where Name = 'a, from b'", new[] { "Name" }, new[] { "Name" })]
    public void TheColumnsFollowTheSelectList(string expression, string[] read, string[] shown)
    {
        var order = CollectionService.SelectOrder(expression, read);
        Assert.Equal(shown, order.Select(i => read[i]));
    }

    [Fact]
    public async Task ThePreviewOfTheRealScriptKeepsTheQueryOrderForColumnsAndValues()
    {
        if (!WindowsPowerShellPresent) return;
        var site = new StubSitePowerShell(State(new { Reject = "xx" },
            new { CollectionID = "SMS00001", Name = "All Systems", LimitToCollectionID = "", Rules = Array.Empty<object>() }));
        var service = new CollectionService(site);

        var preview = await service.PreviewWqlAsync("select ResourceID, Name from SMS_R_System", 5, CancellationToken.None);

        Assert.True(preview.IsValid, preview.Message);
        Assert.Equal(["ResourceID", "Name"], preview.Columns);
        Assert.Equal(["16777220", "DC01"], preview.FirstRows[0]);
    }
}
