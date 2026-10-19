using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.Logging;
using ApplicationPackagerEngine.Tests.DetectionTester;
using K = ApplicationPackagerEngine.Services.MaintenanceScheduleKind;

namespace ApplicationPackagerEngine.Tests.MaintenanceWindows;

[Collection(ProcessEnvironmentCollection.Name)]
public sealed class MaintenanceTemplateStoreTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "apkg-mwtemplates-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
    }

    private MaintenanceTemplateStore Store(IAppLog? log = null) => new(_folder, log);

    private static MaintenanceSchedule Weekly(int hour = 3) =>
        new(K.Weekly, new DateTime(2000, 1, 1, hour, 0, 0), TimeSpan.FromHours(2), false, 1, DayOfWeek.Friday, null, null, 0, null);

    private static MaintenanceTemplate User(string id, string name = "Friday night") =>
        new(id, name, "description", Weekly(), "Any", false);

    [Fact]
    public void TheFiveShippedTemplatesAreConvertedToTheScheduleModel()
    {
        var shipped = Store().All().Where(t => t.IsShipped).ToDictionary(t => t.Id);
        Assert.Equal(5, shipped.Count);

        var daily = shipped["daily-after-hours"];
        Assert.Equal("Daily After Hours", daily.Name);
        Assert.Equal("Any", daily.WindowType);
        Assert.Equal(new MaintenanceSchedule(K.Daily, new DateTime(2000, 1, 1, 23, 0, 0), TimeSpan.FromHours(6), false, 1, null, null, null, 0, null), daily.Schedule);

        Assert.Equal(new MaintenanceSchedule(K.MonthlyByWeekday, new DateTime(2000, 1, 1, 1, 0, 0), TimeSpan.FromHours(6), false, 1, DayOfWeek.Saturday, null, 1, 0, null),
            shipped["monthly-first-saturday"].Schedule);
        Assert.Equal(new MaintenanceSchedule(K.Weekly, new DateTime(2000, 1, 1, 2, 0, 0), TimeSpan.FromHours(4), false, 1, DayOfWeek.Sunday, null, null, 0, null),
            shipped["weekly-sunday-2am"].Schedule);

        var plus3 = shipped["patch-tuesday-plus-3"];
        Assert.Equal("SoftwareUpdates", plus3.WindowType);
        Assert.Equal(new MaintenanceSchedule(K.PatchTuesday, new DateTime(2000, 1, 1, 2, 0, 0), TimeSpan.FromHours(4), false, 1, null, null, null, 3, null), plus3.Schedule);
        Assert.Equal(7, shipped["patch-tuesday-plus-7"].Schedule.PatchTuesdayOffsetDays);

        var projector = new ScheduleProjector(TimeZoneInfo.Utc);
        Assert.All(shipped.Values, t => Assert.Null(projector.Validate(t.Schedule)));
    }

    [Fact]
    public void AUserTemplateIsSavedAsAFileUnderTheTemplatesFolderAndListedAfterTheShippedOnes()
    {
        Store().Save(User("friday"));
        Assert.True(File.Exists(Path.Combine(_folder, "friday.json")));
        var all = new MaintenanceTemplateStore(_folder).All();
        Assert.Equal(6, all.Count);
        Assert.True(all.Take(5).All(t => t.IsShipped));
        Assert.Equal(User("friday"), all[5]);
    }

    [Fact]
    public void SavingAgainReplacesTheFileAndLeavesNoTemporaryFile()
    {
        var store = Store();
        store.Save(User("friday"));
        store.Save(User("friday", "Renamed"));
        Assert.Equal("Renamed", Store().All().Single(t => t.Id == "friday").Name);
        Assert.Equal(["friday.json"], Directory.GetFiles(_folder).Select(f => Path.GetFileName(f)).ToArray());
    }

    [Fact]
    public void ATemplateWithoutAnIdGetsOne()
    {
        Store().Save(User(""));
        var saved = Store().All().Single(t => !t.IsShipped);
        Assert.Matches("^[0-9a-f]{32}$", saved.Id);
    }

    [Theory]
    [InlineData("daily-after-hours")]
    [InlineData("DAILY-AFTER-HOURS")]
    public void AShippedIdCannotBeSavedOrDeleted(string id)
    {
        Assert.Throws<MaintenanceWindowException>(() => Store().Save(User(id)));
        Assert.Throws<MaintenanceWindowException>(() => Store().Delete(id));
        Assert.False(Directory.Exists(_folder) && Directory.GetFiles(_folder).Length > 0);
    }

    [Fact]
    public void ATemplateMarkedShippedCannotBeSaved()
    {
        Assert.Throws<MaintenanceWindowException>(() => Store().Save(User("mine") with { IsShipped = true }));
    }

    [Theory]
    [InlineData("..\\evil")]
    [InlineData("a/b")]
    [InlineData("con:")]
    [InlineData("x y")]
    public void AnIdThatIsNotAPlainNameIsRefused(string id)
    {
        Assert.Throws<MaintenanceWindowException>(() => Store().Save(User(id)));
        Assert.Throws<MaintenanceWindowException>(() => Store().Delete(id));
    }

    [Theory]
    [InlineData("CON")]
    [InlineData("con")]
    [InlineData("PRN")]
    [InlineData("Aux")]
    [InlineData("nul")]
    [InlineData("COM1")]
    [InlineData("com9")]
    [InlineData("LPT1")]
    [InlineData("lpt9")]
    public void AWindowsDeviceNameIsRefusedAsAnId(string id)
    {
        var error = Assert.Throws<MaintenanceWindowException>(() => Store().Save(User(id)));
        Assert.Equal("The template id cannot be a Windows device name such as CON, NUL or COM1.", error.Message);
        Assert.Throws<MaintenanceWindowException>(() => Store().Delete(id));
        Assert.False(Directory.Exists(_folder) && Directory.GetFiles(_folder).Length > 0);
    }

    [Theory]
    [InlineData("CON.txt")]
    [InlineData("nul.json")]
    public void ADeviceNameWithAnExtensionIsRefused(string id)
    {
        Assert.Throws<MaintenanceWindowException>(() => Store().Save(User(id)));
    }

    [Theory]
    [InlineData("CONSOLE")]
    [InlineData("COM10")]
    [InlineData("nul-window")]
    public void ANameThatOnlyStartsLikeADeviceNameIsAccepted(string id)
    {
        Store().Save(User(id));
        Assert.Contains(Store().All(), t => t.Id == id);
    }

    [Fact]
    public void AnInvalidScheduleIsRefused()
    {
        var bad = User("bad") with { Schedule = Weekly() with { Interval = 9 } };
        Assert.Contains("1 to 4 weeks", Assert.Throws<MaintenanceWindowException>(() => Store().Save(bad)).Message);
    }

    [Fact]
    public void AUserFileWithAShippedIdDoesNotReplaceTheShippedTemplate()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(Path.Combine(_folder, "weekly-sunday-2am.json"),
            System.Text.Json.JsonSerializer.Serialize(User("weekly-sunday-2am", "Hijacked"), MaintenanceTemplateStore.JsonOptions));
        var all = Store().All();
        Assert.Single(all, t => t.Id == "weekly-sunday-2am");
        Assert.Equal("Weekly Sunday 2 AM", all.Single(t => t.Id == "weekly-sunday-2am").Name);
    }

    [Fact]
    public void ACorruptFileIsSkippedAndLoggedAndTheOthersLoad()
    {
        var log = new RecordingLog();
        Store().Save(User("good"));
        File.WriteAllText(Path.Combine(_folder, "broken.json"), "{ not json");
        File.WriteAllText(Path.Combine(_folder, "empty.json"), "");
        File.WriteAllText(Path.Combine(_folder, "null.json"), "null");
        File.WriteAllText(Path.Combine(_folder, "noschedule.json"), "{\"Id\":\"noschedule\",\"Name\":\"x\"}");
        var all = Store(log).All();
        Assert.Equal(["good"], all.Where(t => !t.IsShipped).Select(t => t.Id).ToArray());
        Assert.Contains(log.Lines, l => l.Contains("could not be read"));
    }

    [Fact]
    public void AFileWhoseIdDiffersFromItsNameIsSkipped()
    {
        Store().Save(User("one"));
        File.Move(Path.Combine(_folder, "one.json"), Path.Combine(_folder, "two.json"));
        Assert.DoesNotContain(Store().All(), t => !t.IsShipped);
    }

    [Fact]
    public void DeleteRemovesTheUserFileAndAMissingIdIsNoError()
    {
        Store().Save(User("gone"));
        Store().Delete("gone");
        Store().Delete("gone");
        Assert.False(File.Exists(Path.Combine(_folder, "gone.json")));
    }

    [Fact]
    public void ATemplatesFolderThatIsAJunctionIsNeitherReadNorWritten()
    {
        var target = _folder + "-target";
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "planted.json"),
            System.Text.Json.JsonSerializer.Serialize(User("planted"), MaintenanceTemplateStore.JsonOptions));
        Junctions.Create(_folder, target);
        try
        {
            Assert.Equal(5, Store().All().Count);
            Assert.Equal(MaintenanceTemplateStore.LinkMessage, Assert.Throws<MaintenanceWindowException>(() => Store().Save(User("x"))).Message);
            Assert.Throws<MaintenanceWindowException>(() => Store().Delete("planted"));
            Assert.Equal(["planted.json"], Directory.GetFiles(target).Select(f => Path.GetFileName(f)).ToArray());
        }
        finally
        {
            Directory.Delete(_folder);
            Directory.Delete(target, recursive: true);
        }
    }

    [Fact]
    public void TheDefaultFolderIsUnderTheDataRoot()
    {
        var saved = Environment.GetEnvironmentVariable(AppDataPaths.DataRootVariable);
        try
        {
            Environment.SetEnvironmentVariable(AppDataPaths.DataRootVariable, _folder);
            Assert.Equal(Path.Combine(AppDataPaths.Root, "MaintenanceWindows", "Templates"), MaintenanceTemplateStore.DefaultFolder(), ignoreCase: true);
            new MaintenanceTemplateStore().Save(User("rooted"));
            Assert.True(File.Exists(Path.Combine(AppDataPaths.Root, "MaintenanceWindows", "Templates", "rooted.json")));
        }
        finally
        {
            Environment.SetEnvironmentVariable(AppDataPaths.DataRootVariable, saved);
        }
    }
}
