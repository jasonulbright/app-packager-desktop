using System.Text;
using System.Text.Json;
using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests.Deployments;

public sealed class DeploymentTemplateStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "apkg-dhtemplates-" + Guid.NewGuid().ToString("N"));

    private string Folder => Path.Combine(_root, "Templates");

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private DeploymentTemplateStore Store() => new(Folder);

    private static DeploymentTemplate Template(string id = "", string name = "Lab Pilot", string purpose = "Required", int hours = 48) =>
        new(id, name, purpose, "DisplaySoftwareCenterOnly", true, true, false, true, false, true, false, hours, false);

    [Fact]
    public void TheFirstUseWritesTheFourSeedsInTheSuiteFileFormat()
    {
        var all = Store().All();

        Assert.Equal(["Server Pilot", "Server Production", "Workstation Pilot", "Workstation Production"], all.Select(t => t.Name).Order());
        Assert.All(all, t => Assert.True(t.IsSeeded));
        Assert.Equal(["ServerPilot.json", "ServerProduction.json", "WorkstationPilot.json", "WorkstationProduction.json"],
            Directory.GetFiles(Folder).Select(Path.GetFileName).Order());
        var bytes = File.ReadAllBytes(Path.Combine(Folder, "WorkstationProduction.json"));
        Assert.Equal([0xEF, 0xBB, 0xBF], bytes[..3]);
        using var json = JsonDocument.Parse(bytes.AsMemory(3));
        var root = json.RootElement;
        Assert.Equal("Workstation Production", root.GetProperty("Name").GetString());
        Assert.Equal("Required", root.GetProperty("DeployPurpose").GetString());
        Assert.Equal("DisplayAll", root.GetProperty("UserNotification").GetString());
        Assert.Equal("LocalTime", root.GetProperty("TimeBasedOn").GetString());
        Assert.True(root.GetProperty("AllowMeteredConnection").GetBoolean());
        Assert.True(root.GetProperty("AllowBoundaryFallback").GetBoolean());
        Assert.False(root.GetProperty("AllowMicrosoftUpdate").GetBoolean());
        Assert.True(root.GetProperty("RequirePostRebootFullScan").GetBoolean());
        Assert.Equal(72, root.GetProperty("DefaultDeadlineOffsetHours").GetInt32());
        var server = all.Single(t => t.Name == "Server Production");
        Assert.Equal(("HideAll", 168), (server.UserNotification, server.DefaultDeadlineOffsetHours));
    }

    [Fact]
    public void SeedsAreWrittenOnlyIntoAFolderWithoutTemplates()
    {
        Directory.CreateDirectory(Folder);
        File.WriteAllText(Path.Combine(Folder, "Mine.json"), "{\"Name\":\"Mine\",\"DeployPurpose\":\"Available\",\"UserNotification\":\"DisplayAll\",\"TimeBasedOn\":\"UTC\"}");

        var all = Store().All();

        var mine = Assert.Single(all);
        Assert.Equal(("Mine", "Mine", true, false), (mine.Id, mine.Name, mine.UtcBasis, mine.IsSeeded));
        Assert.True(mine.AllowBoundaryFallback);
        Assert.True(mine.RequirePostRebootFullScan);
    }

    [Fact]
    public void ASuiteTemplateWithCollectionFieldsReadsWithoutThem()
    {
        Directory.CreateDirectory(Folder);
        File.WriteAllText(Path.Combine(Folder, "Saved.json"),
            "{\"Name\":\"Saved\",\"TargetCollectionName\":\"Pilot\",\"TargetCollectionID\":\"MCM00014\",\"DeployPurpose\":\"Required\",\"UserNotification\":\"HideAll\",\"TimeBasedOn\":\"LocalTime\",\"OverrideServiceWindow\":true,\"RebootOutsideServiceWindow\":false,\"AllowMeteredConnection\":null,\"AllowBoundaryFallback\":false,\"AllowMicrosoftUpdate\":true,\"RequirePostRebootFullScan\":false,\"DefaultDeadlineOffsetHours\":24}",
            new UTF8Encoding(true));
        var store = Store();

        var saved = Assert.Single(store.All());

        Assert.Equal(new DeploymentTemplate("Saved", "Saved", "Required", "HideAll", false, true, false, false, false, true, false, 24, false), saved);
        store.Save(saved with { DefaultDeadlineOffsetHours = 12 });
        var text = File.ReadAllText(Path.Combine(Folder, "Saved.json"));
        Assert.DoesNotContain("TargetCollection", text);
        Assert.Contains("\"DefaultDeadlineOffsetHours\": 12", text);
    }

    [Fact]
    public void ACorruptOrIncompleteFileIsSkippedAndNamedInTheMessage()
    {
        Directory.CreateDirectory(Folder);
        File.WriteAllText(Path.Combine(Folder, "Broken.json"), "{ not json");
        File.WriteAllText(Path.Combine(Folder, "NoName.json"), "{\"DeployPurpose\":\"Available\"}");
        File.WriteAllText(Path.Combine(Folder, "Good.json"), "{\"Name\":\"Good\",\"DeployPurpose\":\"Available\",\"UserNotification\":\"DisplayAll\"}");
        var store = Store();

        var all = store.All();

        Assert.Equal("Good", Assert.Single(all).Name);
        Assert.Equal("2 template files could not be read and are not listed: Broken.json, NoName.json.", store.Message);
    }

    [Fact]
    public void SaveWithoutAnIdMakesANewFileFromTheNameAndNeverReplacesAnother()
    {
        var store = Store();
        store.All();

        store.Save(Template(name: "Lab Pilot"));
        store.Save(Template(name: "Lab Pilot"));
        store.Save(Template(name: "Workstation Pilot"));

        var all = store.All();
        Assert.Equal(["LabPilot", "LabPilot-2", "WorkstationPilot-2"], all.Where(t => !t.IsSeeded).Select(t => t.Id).Order());
        Assert.Equal("Available", all.Single(t => t.Id == "WorkstationPilot").Purpose);
    }

    [Theory]
    [InlineData("WorkstationPilot")]
    [InlineData("serverproduction")]
    public void ASeedCannotBeChangedOrDeleted(string id)
    {
        var store = Store();
        store.All();

        Assert.Throws<DeploymentServiceException>(() => store.Save(Template(id)));
        Assert.Throws<DeploymentServiceException>(() => store.Delete(id));
        Assert.Equal(4, store.All().Count);
    }

    [Theory]
    [InlineData("..\\outside")]
    [InlineData("a/b")]
    [InlineData("CON")]
    [InlineData("com1")]
    [InlineData("name.json")]
    [InlineData("")]
    public void AnIdThatIsNotAPlainNameIsRefused(string id)
    {
        var store = Store();
        Assert.Throws<DeploymentServiceException>(() => store.Delete(id));
        if (id.Length > 0) Assert.Throws<DeploymentServiceException>(() => store.Save(Template(id)));
        Assert.False(File.Exists(Path.Combine(_root, "outside.json")));
    }

    [Theory]
    [InlineData("", "Available", 0, "Enter a name")]
    [InlineData("x", "Sometimes", 0, "purpose")]
    [InlineData("x", "Required", -1, "deadline offset")]
    [InlineData("x", "Required", 8761, "deadline offset")]
    [InlineData("a\nb", "Required", 1, "Enter a name")]
    public void AnInvalidTemplateIsRefused(string name, string purpose, int hours, string expected)
    {
        var error = Assert.Throws<DeploymentServiceException>(() => Store().Save(Template(name: name, purpose: purpose, hours: hours)));
        Assert.Contains(expected, error.Message);
    }

    [Fact]
    public void ANameWithWildcardsAndQuotesIsKeptAsTextAndTheIdHasOnlyPlainCharacters()
    {
        var store = Store();
        store.Save(Template(name: "-Pilot [x64] *'\"q\""));

        var saved = store.All().Single(t => !t.IsSeeded);
        Assert.Equal("-Pilot [x64] *'\"q\"", saved.Name);
        Assert.Equal("-Pilotx64q", saved.Id);
    }

    [Fact]
    public void DeleteRemovesAUserTemplate()
    {
        var store = Store();
        store.Save(Template(name: "Temp"));
        store.Delete("Temp");
        Assert.DoesNotContain(store.All(), t => t.Id == "Temp");
    }

    [Fact]
    public void ATemplatesFolderThatIsAJunctionIsNeitherReadNorWritten()
    {
        var target = Path.Combine(_root, "target");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "Evil.json"), "{\"Name\":\"Evil\",\"DeployPurpose\":\"Available\",\"UserNotification\":\"DisplayAll\"}");
        Junction(Folder, target);
        var store = Store();

        Assert.Empty(store.All());
        Assert.Contains("junction or symbolic link", store.Message);
        Assert.Throws<DeploymentServiceException>(() => store.Save(Template()));
        Assert.Single(Directory.GetFiles(target));
    }

    [Fact]
    public void AFileLargerThanTheLimitIsSkipped()
    {
        Directory.CreateDirectory(Folder);
        File.WriteAllText(Path.Combine(Folder, "Huge.json"), "{\"Name\":\"" + new string('a', 70_000) + "\"}");
        var store = Store();
        Assert.Empty(store.All());
        Assert.Contains("Huge.json", store.Message);
    }

    private static void Junction(string link, string target)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        using var process = System.Diagnostics.Process.Start(psi)!;
        process.WaitForExit();
        Assert.True(Directory.Exists(link));
    }
}
