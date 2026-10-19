using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Tests.DetectionTester;
using ApplicationPackagerEngine.Tests.MaintenanceWindows;

namespace ApplicationPackagerEngine.Tests.Collections;

public sealed class QueryTemplateStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "apkg-qtemplates-" + Guid.NewGuid().ToString("N"));

    private string Folder => Path.Combine(_root, "Templates");

    public void Dispose()
    {
        try
        {
            foreach (var link in Directory.Exists(_root) ? Directory.GetDirectories(_root) : [])
                if (File.GetAttributes(link).HasFlag(FileAttributes.ReparsePoint)) Directory.Delete(link);
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException) { }
    }

    private string Write(string name, string content)
    {
        Directory.CreateDirectory(Folder);
        var path = Path.Combine(Folder, name);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void The245ShippedTemplatesLoadWithoutAUserFolder()
    {
        var store = new QueryTemplateStore(Folder);
        var all = store.All();

        Assert.Equal(245, all.Count);
        Assert.All(all, t => Assert.True(t.IsShipped));
        Assert.Equal(all.Count, all.Select(t => t.Id).Distinct().Count());
        Assert.Equal(225, all.Count(t => t.Id.StartsWith("shipped:operational-collections:", StringComparison.Ordinal)));
        Assert.Equal(20, all.Count(t => t.Id.StartsWith("shipped:parameterized-templates:", StringComparison.Ordinal)));
        Assert.All(all.Where(t => t.Id.Contains("operational", StringComparison.Ordinal)), t => Assert.Empty(t.Parameters));
        Assert.Equal(18, all.Count(t => t.Parameters.Count > 0));
        Assert.All(all, t => Assert.Null(WqlGuard.Check(t.Expression)));
        Assert.Null(store.Message);
    }

    [Fact]
    public void AParameterizedTemplateKeepsItsPlaceholdersLabelsAndDefaults()
    {
        var template = new QueryTemplateStore(Folder).All().First(t => t.Parameters.Count > 0);
        Assert.All(template.Parameters, p => Assert.Contains(p, template.Expression));
        Assert.Equal(template.Parameters, template.ParameterDetails!.Select(d => d.Placeholder).ToList());
        Assert.All(template.ParameterDetails!, d => Assert.False(string.IsNullOrWhiteSpace(d.Label)));
        Assert.False(string.IsNullOrWhiteSpace(template.Description));
        Assert.False(string.IsNullOrWhiteSpace(template.LimitingCollectionName));
    }

    [Fact]
    public void UserTemplatesFollowTheShippedOnesAndUnknownPropertiesAreIgnored()
    {
        Write("mine.json", """
            [
              { "Name": "My laptops", "Category": "Mine", "Query": "select * from SMS_R_System where Name like 'LT%'", "Colour": "blue" },
              { "Name": "By user", "Expression": "select * from SMS_R_System where LastLogonUserName = {User}", "Parameters": ["{User}", "{Unused}"] },
              { "Name": "No query" }
            ]
            """);
        Write("single.json", """{ "name": "Single", "query": "select * from SMS_R_System" }""");

        var store = new QueryTemplateStore(Folder);
        var all = store.All();

        Assert.Equal(248, all.Count);
        var user = all.Skip(245).ToList();
        Assert.All(user, t => Assert.False(t.IsShipped));
        Assert.All(user, t => Assert.StartsWith("user:", t.Id));
        Assert.Equal(["{User}"], user.Single(t => t.Name == "By user").Parameters);
        Assert.Equal("Other", user.Single(t => t.Name == "Single").Category);
        Assert.Null(store.Message);
    }

    [Fact]
    public void ACorruptOrOversizedFileIsSkippedAndNamedInTheMessage()
    {
        Write("broken.json", "{ \"Name\": ");
        Write("empty.json", "[]");
        using (var stream = File.Create(Path.Combine(Folder, "huge.json")))
            stream.SetLength(QueryTemplateStore.MaxUserFileBytes + 1);
        Write("good.json", """{ "Name": "Good", "Query": "select * from SMS_R_System" }""");

        var store = new QueryTemplateStore(Folder);
        var all = store.All();

        Assert.Equal(246, all.Count);
        Assert.Contains("broken.json (not valid JSON)", store.Message);
        Assert.Contains("huge.json (larger than 4 MB)", store.Message);
        Assert.Contains("empty.json", store.Message);
        Assert.StartsWith("3 user template files were skipped", store.Message);
        Assert.DoesNotContain(_root, store.Message);
    }

    [Fact]
    public void AUserTemplateNeverReplacesAShippedOne()
    {
        var shipped = new QueryTemplateStore(Folder).All()[0];
        Write("same.json", $$"""{ "Name": "{{shipped.Name}}", "Category": "{{shipped.Category}}", "Query": "select * from SMS_R_System where Name = 'x'" }""");

        var all = new QueryTemplateStore(Folder).All();
        Assert.Equal(shipped, all[0]);
        Assert.Equal(2, all.Count(t => t.Name == shipped.Name));
    }

    [Fact]
    public void AUserFolderThatIsAJunctionIsNotRead()
    {
        var target = Path.Combine(_root, "elsewhere");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "t.json"), """{ "Name": "Linked", "Query": "select * from SMS_R_System" }""");
        Directory.CreateDirectory(_root);
        Junctions.Create(Folder, target);

        var store = new QueryTemplateStore(Folder);
        Assert.Equal(245, store.All().Count);
        Assert.Contains("junction or symbolic link", store.Message);
    }

    [Fact]
    public void AUserFileThatIsALinkIsSkippedWhenTheSystemAllowsFileLinks()
    {
        var target = Path.Combine(_root, "target.json");
        Directory.CreateDirectory(_root);
        File.WriteAllText(target, """{ "Name": "Linked", "Query": "select * from SMS_R_System" }""");
        Directory.CreateDirectory(Folder);
        try
        {
            File.CreateSymbolicLink(Path.Combine(Folder, "link.json"), target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }
        var store = new QueryTemplateStore(Folder);
        Assert.Equal(245, store.All().Count);
        Assert.Contains("link.json (a link)", store.Message);
    }

    [Fact]
    public void LoadingLogsCountsOnly()
    {
        Write("mine.json", """{ "Name": "Secret name", "Query": "select * from SMS_R_System where Name = 'secret'" }""");
        var log = new RecordingLog();
        new QueryTemplateStore(Folder, log).All();
        Assert.Contains(log.Lines, l => l.Contains("245 shipped and 1 user"));
        Assert.DoesNotContain(log.Lines, l => l.Contains("secret", StringComparison.OrdinalIgnoreCase));
    }
}
