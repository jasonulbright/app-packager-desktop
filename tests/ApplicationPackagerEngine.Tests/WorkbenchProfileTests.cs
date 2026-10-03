using System.Text.Json.Nodes;
using ApplicationPackagerEngine.Models.Workbench;

namespace ApplicationPackagerEngine.Tests;

public sealed class WorkbenchProfileTests : IDisposable
{
    private readonly WorkbenchTestRoot _t = new();

    public void Dispose() => _t.Dispose();

    private InvalidOperationException SaveFails(WorkbenchProfile profile) =>
        Assert.Throws<InvalidOperationException>(() => _t.Store.SaveProfile(profile));

    [Fact]
    public void Save_RaisesTheRevisionOnEverySave()
    {
        var saved = _t.NewProfile("catalog:package-rev");
        Assert.Equal(1, saved.Revision);
        Assert.Equal(2, _t.Store.SaveProfile(saved).Revision);
    }

    [Fact]
    public void Save_RefusesASaveBuiltOnAStaleRevision()
    {
        var saved = _t.NewProfile("catalog:package-conflict");
        _t.Store.SaveProfile(saved);
        Assert.Contains("changed on disk", SaveFails(saved).Message);
        Assert.Equal(1, saved.Revision);
    }

    [Fact]
    public void Save_RefusesARevisionWithNoStoredProfile()
    {
        var profile = WorkbenchProfile.CreateNew("catalog:package-orphan");
        profile.Revision = 4;
        Assert.Contains("has no stored profile", SaveFails(profile).Message);
    }

    [Fact]
    public void Save_RefusesTheVirtualDefaultProfile()
    {
        var defaultProfile = _t.Store.GetProfile("catalog:package-default", "default");
        Assert.True(defaultProfile.IsDefault);
        Assert.Equal(
            "The packager default profile cannot be saved; use Copy-Profile to create an editable profile.",
            SaveFails(defaultProfile).Message);
    }

    [Fact]
    public void Save_RejectsAProfileSchemaNewerThanThisBuild()
    {
        var profile = WorkbenchProfile.CreateNew("catalog:package-future");
        profile.Data["SchemaVersion"] = 99;
        Assert.Contains("newer than this build supports", SaveFails(profile).Message);
    }

    [Fact]
    public void Get_RejectsAStoredProfileSchemaNewerThanThisBuild()
    {
        var saved = _t.NewProfile("catalog:package-future-read");
        var path = Path.Combine(_t.Store.GetProfileFolder(saved.ApplicationId, saved.ProfileId), "profile.json");
        File.WriteAllText(path, File.ReadAllText(path).Replace("\"SchemaVersion\": 1", "\"SchemaVersion\": 7"));
        var ex = Assert.Throws<InvalidOperationException>(() => _t.Store.GetProfile(saved.ApplicationId, saved.ProfileId));
        Assert.StartsWith("Profile schema 7 is newer than this build supports (1): ", ex.Message);
    }

    [Fact]
    public void Validate_RejectsAnEstimateGreaterThanTheMaximum()
    {
        var profile = WorkbenchProfile.CreateNew("catalog:package-timing");
        profile.Data["Timing"] = WorkbenchTestRoot.Json("""{ "EstimatedMinutes": 40, "MaximumMinutes": 30 }""");
        Assert.Equal("Timing.EstimatedMinutes (40) cannot exceed Timing.MaximumMinutes (30).", SaveFails(profile).Message);
    }

    [Theory]
    [InlineData("0", "0")]
    [InlineData("-5", "-5")]
    [InlineData("12.5", "12.5")]
    [InlineData("\"soon\"", "soon")]
    public void Validate_RejectsAnInvalidMinuteRange(string json, string shown)
    {
        var profile = WorkbenchProfile.CreateNew("catalog:package-range");
        profile.Data["Timing"] = WorkbenchTestRoot.Json($$"""{ "EstimatedMinutes": {{json}} }""");
        Assert.Equal($"Timing.EstimatedMinutes must be a positive whole number of minutes (got '{shown}').", SaveFails(profile).Message);
    }

    [Fact]
    public void Validate_RejectsARequirementOperationWithoutARuleId()
    {
        var profile = WorkbenchProfile.CreateNew("catalog:package-req");
        profile.Data["Requirements"] = WorkbenchTestRoot.Json("""{ "Operations": [ { "Op": "Add", "Rule": { "ConditionId": "cpu-arch" } } ] }""");
        Assert.Equal("Every requirement operation needs a RuleId.", SaveFails(profile).Message);
    }

    [Theory]
    [InlineData("""{ "Op": "Merge", "RuleId": "x", "Rule": {} }""", "Requirement operation Op must be Add, Replace or Remove (got 'Merge').")]
    [InlineData("""{ "Op": "Replace", "RuleId": "cpu-arch" }""", "Requirement operation 'Replace' for rule 'cpu-arch' needs a Rule.")]
    public void Validate_RejectsAMalformedRequirementOperation(string operation, string message)
    {
        var profile = WorkbenchProfile.CreateNew("catalog:package-req-op");
        profile.Data["Requirements"] = WorkbenchTestRoot.Json($$"""{ "Operations": [ {{operation}} ] }""");
        Assert.Equal(message, SaveFails(profile).Message);
    }

    [Fact]
    public void Validate_AcceptsARemoveOperationWithoutARule()
    {
        var profile = WorkbenchProfile.CreateNew("catalog:package-req-remove");
        profile.Data["Requirements"] = WorkbenchTestRoot.Json("""{ "Operations": [ { "Op": "Remove", "RuleId": "cpu-arch" } ] }""");
        Assert.Equal(1, _t.Store.SaveProfile(profile).Revision);
    }

    [Fact]
    public void Validate_RejectsAnAbsoluteSourceFileDestination()
    {
        var profile = WorkbenchProfile.CreateNew("catalog:package-abs");
        profile.Data["SourceFiles"] = new JsonArray(WorkbenchTestRoot.Json("""{ "Asset": "aaaaaaaaaaaaaaaa", "Destination": "C:\\evil.txt" }"""));
        Assert.Equal(@"SourceFiles destination 'C:\evil.txt' must be relative to the content root.", SaveFails(profile).Message);
    }

    [Theory]
    [InlineData("Install", """{ "Mode": "Replace" }""", "Install.Mode must be Generated, Extend or Custom (got 'Replace').")]
    [InlineData("Uninstall", """{ "Mode": "Script" }""", "Uninstall.Mode must be Generated, Extend or Custom (got 'Script').")]
    [InlineData("Detection", """{ "Mode": "Override" }""", "Detection.Mode must be Inherit or Custom (got 'Override').")]
    [InlineData("Detection", """{ "Mode": "Custom" }""", "Detection.Mode Custom requires a Detection.Rule.")]
    public void Validate_RejectsAnUnknownModeOrAMissingRule(string section, string block, string message)
    {
        var profile = WorkbenchProfile.CreateNew("catalog:package-modes");
        profile.Data[section] = WorkbenchTestRoot.Json(block);
        Assert.Equal(message, SaveFails(profile).Message);
    }

    [Fact]
    public void SaveAs_CopiesAProfileUnderANewIdWithTheNewNameAndItsAssets()
    {
        var id = "catalog:package-copy";
        var original = _t.NewProfile(id, "Managed");
        var asset = _t.Store.AddAsset(id, original.ProfileId, _t.NewFile(Path.Combine(_t.NewFolder(), "a.txt"), "x"));

        var copy = _t.Store.CopyProfile(id, original.ProfileId, "Lab");

        Assert.NotEqual(original.ProfileId, copy.ProfileId);
        Assert.Equal("Lab", copy.Name);
        Assert.Equal(1, copy.Revision);
        Assert.Equal(original.ProfileId, copy.Get("CopiedFrom").Text);
        Assert.True(File.Exists(_t.Store.ResolveAssetPath(id, copy.ProfileId, asset.AssetId)));
    }

    [Fact]
    public void SaveAs_FromTheDefaultProfileCreatesAnEditableProfile()
    {
        var copy = _t.Store.CopyProfile("catalog:package-copy-default", "default", "Mine", setActive: true);
        Assert.False(copy.IsDefault);
        Assert.Equal(copy.ProfileId, _t.Store.GetApplicationDefinition("catalog:package-copy-default").ActiveProfileId);
    }

    [Fact]
    public void List_IncludesTheVirtualDefaultProfileAlongsideSavedOnes()
    {
        var id = "catalog:package-list";
        Assert.Single(_t.Store.GetProfiles(id));
        var saved = _t.NewProfile(id);

        var profiles = _t.Store.GetProfiles(id);
        Assert.Equal(2, profiles.Count);
        Assert.True(profiles[0].IsDefault);
        Assert.False(profiles[0].IsActive);
        Assert.True(profiles.Single(p => p.ProfileId == saved.ProfileId).IsActive);
    }

    [Fact]
    public void ClearField_DistinguishesInheritFromExplicitNone()
    {
        var profile = WorkbenchProfile.CreateNew("catalog:package-inherit");
        profile.Set("Install.Command", "setup.exe /S");

        var inherited = profile.Clone();
        inherited.ClearField("Install.Command", FieldClearMode.Inherit);
        var explicitNone = profile.Clone();
        explicitNone.ClearField("Install.Command", FieldClearMode.Explicit);

        Assert.False(inherited.Get("Install.Command").Found);
        Assert.True(explicitNone.Get("Install.Command").Found);
        Assert.Null(explicitNone.Get("Install.Command").Value);
    }

    [Fact]
    public void ExplicitNone_SurvivesASaveAndReload()
    {
        var profile = WorkbenchProfile.CreateNew("catalog:package-explicit-store");
        profile.ClearField("Application.Icon", FieldClearMode.Explicit);
        var saved = _t.Store.SaveProfile(profile);
        var member = _t.Store.GetProfile(saved.ApplicationId, saved.ProfileId).Get("Application.Icon");
        Assert.True(member.Found);
        Assert.Null(member.Value);
    }
}
