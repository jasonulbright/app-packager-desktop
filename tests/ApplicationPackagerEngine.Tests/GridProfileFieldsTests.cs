using ApplicationPackagerEngine.Models.Workbench;
using ApplicationPackagerEngine.Services.Workbench;
using ApplicationPackagerUI.Services;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// Q-106: the Workbench profile fields of the Application Settings grid (runtime and reboot behavior). The grid keeps the semantics of the
/// Workbench: an empty value inherits, a value is a custom override, and the profile that the operator chose is never replaced.
/// </summary>
public sealed class GridProfileFieldsTests : IDisposable
{
    private const string Definition = "7zip";
    private readonly string _root = Directory.CreateTempSubdirectory().FullName;
    private readonly WorkbenchStore _store;
    private readonly string _appId = ApplicationIds.New(ApplicationOrigin.Catalog, name: Definition);

    public GridProfileFieldsTests() => _store = new WorkbenchStore(_root);

    public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } }

    private static Dictionary<string, string> Fields(params (string Path, string Value)[] pairs) => pairs.ToDictionary(p => p.Path, p => p.Value);

    [Fact]
    public void AValueIsWrittenAsACustomOverrideAndReadBack()
    {
        var saved = GridProfileFields.Write(_store, _appId, Definition,
            Fields((GridProfileFields.EstimatedMinutes, "20"), (GridProfileFields.MaximumMinutes, "45"), (GridProfileFields.InstallReboot, "Soft reboot")));

        Assert.Equal(Definition, saved.Get("MigratedFrom").Text);
        Assert.Equal(20, WorkbenchJson.AsInt(saved.Get("Timing.EstimatedMinutes").Value));
        var state = GridProfileFields.Read(_store, _appId, Definition);
        Assert.Equal("20", state.Fields[GridProfileFields.EstimatedMinutes]);
        Assert.Equal("45", state.Fields[GridProfileFields.MaximumMinutes]);
        Assert.Equal("Soft reboot", state.Fields[GridProfileFields.InstallReboot]);
        Assert.Null(state.ActiveElsewhere);
    }

    [Fact]
    public void AnEmptyValueOrInheritRemovesTheKeySoTheNextLevelSuppliesTheValue()
    {
        GridProfileFields.Write(_store, _appId, Definition, Fields((GridProfileFields.EstimatedMinutes, "20"), (GridProfileFields.InstallReboot, "Hard reboot")));
        var saved = GridProfileFields.Write(_store, _appId, Definition, Fields((GridProfileFields.EstimatedMinutes, ""), (GridProfileFields.InstallReboot, GridProfileFields.Inherit)));

        Assert.False(saved.Get("Timing.EstimatedMinutes").Found);
        Assert.False(saved.Get("Install.RebootPolicy").Found);
        Assert.All(GridProfileFields.Read(_store, _appId, Definition).Fields.Values, v => Assert.Equal("", v));
    }

    [Fact]
    public void AWriteKeepsTheOtherFieldsOfTheProfileAndItsIdentity()
    {
        var first = GridProfileFields.Write(_store, _appId, Definition, Fields((GridProfileFields.EstimatedMinutes, "20")));
        var second = GridProfileFields.Write(_store, _appId, Definition, Fields((GridProfileFields.MaximumMinutes, "40")));

        Assert.Equal(first.ProfileId, second.ProfileId);
        Assert.Equal(1, _store.GetProfiles(_appId).Count(p => !p.IsDefault));
        Assert.Equal("20", GridProfileFields.Read(_store, _appId, Definition).Fields[GridProfileFields.EstimatedMinutes]);
    }

    [Fact]
    public void TheProfileOfTheGridBecomesActiveOnlyWhileTheApplicationIsOnTheDefaultProfile()
    {
        var saved = GridProfileFields.Write(_store, _appId, Definition, Fields((GridProfileFields.EstimatedMinutes, "20")));
        Assert.Equal(saved.ProfileId, _store.GetApplicationDefinition(_appId).ActiveProfileId);

        // The operator chooses another profile: the grid does not replace it and says so.
        var own = WorkbenchProfile.CreateNew(_appId, name: "Pilot");
        _store.SaveProfile(own, setActive: true);
        GridProfileFields.Write(_store, _appId, Definition, Fields((GridProfileFields.MaximumMinutes, "50")));

        Assert.Equal(own.ProfileId, _store.GetApplicationDefinition(_appId).ActiveProfileId);
        Assert.Equal("Pilot", GridProfileFields.Read(_store, _appId, Definition).ActiveElsewhere);
    }

    [Theory]
    [InlineData(GridProfileFields.EstimatedMinutes, "15", true)]
    [InlineData(GridProfileFields.EstimatedMinutes, "", true)]
    [InlineData(GridProfileFields.EstimatedMinutes, "0", false)]
    [InlineData(GridProfileFields.MaximumMinutes, "soon", false)]
    [InlineData(GridProfileFields.InstallReboot, "Force restart", true)]
    [InlineData(GridProfileFields.InstallReboot, "Sometimes", false)]
    [InlineData("Install.Command", "x", false)]
    public void OnlyAValueThatTheStageAcceptsIsValid(string path, string value, bool valid) =>
        Assert.Equal(valid, GridProfileFields.IsValid(path, value));

    [Fact]
    public void TheColumnsOfTheRegistryEditThePendingFieldsAndRefuseAnActiveOtherProfile()
    {
        var row = new ApplicationPackagerUI.Models.SupersedenceRow { Uid = "u", ApplicationId = _appId, DefinitionId = Definition, Application = "7-Zip", Vendor = "v" };
        var work = new ApplicationWork();
        var column = ApplicationColumns.Find("reboot")!;
        Assert.Equal("Inherit", column.Get(work, row));
        Assert.True(column.Set(work, row, "No reboot"));
        Assert.Equal("No reboot", column.Get(work, row));
        Assert.False(column.Set(work, row, "Sometimes"));
        Assert.Equal("No reboot", work.ProfilePending[_appId][GridProfileFields.InstallReboot]);

        work.ProfileOf = _ => new GridProfileFields.State(GridProfileFields.State.Empty.Fields, "Pilot");
        Assert.Contains("Pilot", column.NotApplicable(work, row)!);
        Assert.Contains("Pilot", ApplicationColumns.Find("estimated")!.NotApplicable(work, row)!);
    }
}
