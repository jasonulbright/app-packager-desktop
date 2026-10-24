using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests.SiteHygiene;

/// <summary>Content, devices, boundaries, task sequences, updates, site maintenance, distribution points, compliance, drivers and security.</summary>
public sealed class SiteHygieneOtherCheckTests
{
    private static readonly string[] Areas = ["Content", "Devices", "Boundaries", "TaskSequences", "Updates", "UpdateContent", "Site", "DistributionPoints", "Compliance", "Drivers", "Security"];

    [Theory]
    [InlineData("BND-01")]
    [InlineData("BND-02")]
    [InlineData("CFG-01")]
    [InlineData("CFG-02")]
    [InlineData("CFG-03")]
    [InlineData("DPT-02")]
    [InlineData("TSQ-02")]
    [InlineData("TSQ-04")]
    public void TheNativeCheckFindsWhatTheSourceFindsByKey(string checkId)
    {
        var (findings, checks) = SiteHygieneParity.Native(Areas);
        Assert.Equal(HygieneCheckStates.Ran, checks.Single(c => c.CheckId == checkId).State);
        SiteHygieneParity.SameAsSource(findings, checkId);
        Assert.NotEmpty(findings.Where(f => f.CheckId == checkId));
    }

    [Theory]
    [InlineData("BND-03")]
    [InlineData("DEV-03")]
    [InlineData("MNT-01")]
    [InlineData("UPD-03")]
    public void TheNativeCheckFindsWhatTheSourceFindsByName(string checkId)
    {
        var (findings, _) = SiteHygieneParity.Native(Areas);
        SiteHygieneParity.SameAsSource(findings, checkId, s => s.ObjectName, f => f.ObjectName);
        Assert.NotEmpty(findings.Where(f => f.CheckId == checkId));
    }

    [Theory]
    [InlineData("CNT-01")]
    [InlineData("CNT-02")]
    [InlineData("CNT-03")]
    [InlineData("DEV-01")]
    [InlineData("DEV-02")]
    [InlineData("TSQ-01")]
    [InlineData("TSQ-03")]
    [InlineData("UPD-01")]
    [InlineData("UPD-04")]
    [InlineData("UPD-05")]
    [InlineData("DPT-01")]
    [InlineData("DRV-01")]
    [InlineData("SEC-01")]
    public void ACheckWithNoCaseOnCm01FindsNothingThereAsTheSourceDoes(string checkId)
    {
        var (findings, checks) = SiteHygieneParity.Native(Areas);
        Assert.Equal(HygieneCheckStates.Ran, checks.Single(c => c.CheckId == checkId).State);
        Assert.DoesNotContain(findings, f => f.CheckId == checkId);
        Assert.DoesNotContain(SiteHygieneFixtures.SourceFindings(), f => f.CheckId == checkId);
    }

    [Fact]
    public void TheDisabledSiteBackupIsFoundUnderTheNameTheSiteUses()
    {
        var (findings, _) = SiteHygieneParity.Native("Site");
        var backup = Assert.Single(findings, f => f.CheckId == "MNT-02");
        Assert.Equal("Backup SMS Site Server", backup.ObjectKey);
        Assert.Equal("Set-CMSiteMaintenanceTask -SiteCode 'MCM' -Name 'Backup SMS Site Server' -Enabled $true", backup.FixText);
        Assert.DoesNotContain(SiteHygieneFixtures.SourceFindings(), f => f.CheckId == "MNT-02");
        var cleanup = Assert.Single(findings, f => f.CheckId == "MNT-01");
        Assert.Equal("Set-CMSiteMaintenanceTask -SiteCode 'MCM' -Name 'Delete Obsolete Client Discovery Data' -Enabled $true", cleanup.FixText);
    }

    [Fact]
    public void TheDisabledRuleIsKeyedByItsIdAndRemovedById()
    {
        var (findings, _) = SiteHygieneParity.Native("Updates");
        var adr = Assert.Single(findings, f => f.CheckId == "UPD-03");
        Assert.Equal("1", adr.ObjectKey);
        Assert.Equal(HygieneSeverity.Info, adr.Severity);
        Assert.Equal("Remove-CMSoftwareUpdateAutoDeploymentRule -Id 1 -Force", adr.FixText);
    }

    private static readonly string[] DeviceColumns = ["ResourceID", "Name", "IsClient", "ClientVersion", "LastActiveTime", "SMBIOSGUID"];
    private static readonly string[] TaskColumns = ["ItemName", "SiteCode", "Enabled"];

    [Fact]
    public void EachInactiveClientIsItsOwnFindingWithTheSeverityOfTheCleanupState()
    {
        var read = SiteHygieneParity.EmptySite()
            .Put("Devices", DeviceColumns,
                ["100", "OLD-1", "True", "5.00.9146.1009", "2026-06-01T00:00:00", "A"],
                ["101", "OLD-2", "True", "5.00.9146.1009", "2026-05-01T00:00:00", "B"],
                ["102", "RECENT", "True", "5.00.9146.1009", "2026-10-20T00:00:00", "C"],
                ["103", "NO-CLIENT", "False", "", "2026-01-01T00:00:00", ""],
                ["104", "NEVER", "True", "5.00.9146.1009", "", "D"])
            .Put("MaintenanceTasks", TaskColumns, ["Delete Inactive Client Discovery Data", "MCM", "False"], ["Delete Aged Discovery Data", "MCM", "False"]);
        var (findings, _) = SiteHygieneParity.Native(read, "Devices");
        var inactive = findings.Where(f => f.CheckId == "DEV-01").ToList();
        Assert.Equal(["100", "101"], inactive.Select(f => f.ObjectKey).Order());
        Assert.All(inactive, f => Assert.Equal(HygieneSeverity.Warning, f.Severity));
        Assert.Equal("Remove-CMDevice -ResourceId 100 -Force", inactive.Single(f => f.ObjectKey == "100").FixText);

        read.Put("MaintenanceTasks", TaskColumns, ["Delete Aged Discovery Data", "MCM", "True"]);
        var (again, _) = SiteHygieneParity.Native(read, "Devices");
        Assert.All(again.Where(f => f.CheckId == "DEV-01"), f => Assert.Equal(HygieneSeverity.Info, f.Severity));
    }

    [Fact]
    public void DuplicateDeviceRecordsAreKeyedByTheirResourceIds()
    {
        var read = SiteHygieneParity.EmptySite().Put("Devices", DeviceColumns,
            ["300", "PC-1", "True", "5.00.9146.1009", "2026-10-20T00:00:00", "G1"],
            ["200", "pc-1", "True", "5.00.9146.1009", "2026-10-20T00:00:00", "G2"],
            ["400", "PC-2", "True", "5.00.9146.1009", "2026-10-20T00:00:00", "G3"],
            ["401", "PC-3", "True", "5.00.9146.1009", "2026-10-20T00:00:00", "G3"],
            ["500", "VM-1", "True", "5.00.9146.1009", "2026-10-20T00:00:00", "00000000-0000-0000-0000-000000000000"],
            ["501", "VM-2", "True", "5.00.9146.1009", "2026-10-20T00:00:00", "00000000-0000-0000-0000-000000000000"]);
        var (findings, _) = SiteHygieneParity.Native(read, "Devices");
        Assert.Equal(["200, 300", "G3"], findings.Where(f => f.CheckId == "DEV-02").Select(f => f.ObjectKey).Order());
    }

    [Fact]
    public void ContentFailuresStalledTransfersAndUndistributedDeployedContentAreFound()
    {
        string[] content = ["ObjectID", "PackageID", "SoftwareName", "ObjectType", "Targeted", "NumberSuccess", "NumberInProgress", "NumberErrors", "NumberUnknown", "SourceSize", "LastUpdateDate"];
        string[] apps = ["CI_ID", "ModelID", "ModelName", "LocalizedDisplayName", "IsDeployed", "IsExpired", "IsSuperseded", "IsSuperseding", "IsEnabled", "HasContent", "Manufacturer", "SoftwareVersion", "DateCreated", "NumberOfDeploymentTypes", "CIVersion"];
        var read = SiteHygieneParity.EmptySite()
            .Put("ContentStatus", content,
                ["MCM00100", "MCM00100", "Failing", "0", "2", "1", "0", "1", "0", "10", "2026-10-20T00:00:00"],
                ["MCM00101", "MCM00101", "Stalled", "0", "2", "1", "1", "0", "0", "10", "2026-10-01T00:00:00"],
                ["ScopeId_X/Application_1", "MCM00102", "Undistributed App", "512", "0", "0", "0", "0", "0", "10", ""],
                ["ScopeId_X/Application_2", "MCM00103", "Script App", "512", "0", "0", "0", "0", "0", "0", ""])
            .Put("Applications", apps,
                ["1", "11", "ScopeId_X/Application_1", "Undistributed App", "True", "False", "False", "False", "True", "True", "", "1", "2026-01-01T00:00:00", "1", "1"],
                ["2", "12", "ScopeId_X/Application_2", "Script App", "True", "False", "False", "False", "True", "False", "", "1", "2026-01-01T00:00:00", "1", "1"]);
        var (findings, _) = SiteHygieneParity.Native(read, "Content");
        Assert.Equal("MCM00100", Assert.Single(findings, f => f.CheckId == "CNT-01").ObjectKey);
        Assert.Equal("MCM00101", Assert.Single(findings, f => f.CheckId == "CNT-02").ObjectKey);
        var cnt3 = Assert.Single(findings, f => f.CheckId == "CNT-03");
        Assert.Equal("MCM00102", cnt3.ObjectKey);
        Assert.Equal("Application", cnt3.ObjectType);
    }

    [Fact]
    public void ATaskSequenceThatReferencesDeletedContentAndAnApplicationWithoutTheSettingAreFound()
    {
        string[] apps = ["CI_ID", "ModelID", "ModelName", "LocalizedDisplayName"];
        var read = SiteHygieneParity.EmptySite()
            .Put("TaskSequences", ["PackageID", "Name", "BootImageID", "ProgramFlags"], ["MCM00008", "Build", "MCM00099", "0"])
            .Put("TsReferences", ["PackageID", "ObjectID"], ["MCM00008", "MCM00050"], ["MCM00008", "ScopeId_X/Application_1"])
            .Put("Packages", ["PackageID", "Name", "PackageType"], ["MCM00050", "Present", "0"])
            .Put("Applications", apps, ["1", "11", "ScopeId_X/Application_1", "Office"])
            .Put("TsAppDefinitions", ["ModelName", "AutoInstall"], ["ScopeId_X/Application_1", "False"]);
        var (findings, _) = SiteHygieneParity.Native(read, "TaskSequences");
        var broken = Assert.Single(findings, f => f.CheckId == "TSQ-01");
        Assert.Contains("MCM00099", broken.Evidence);
        var setting = Assert.Single(findings, f => f.CheckId == "TSQ-03");
        Assert.Equal("Set-CMApplication -ModelName 'ScopeId_X/Application_1' -AutoInstall $true", setting.FixText);
    }

    [Fact]
    public void UpdateGroupsPackagesDriversAndAdministratorsAreFound()
    {
        var read = SiteHygieneParity.EmptySite()
            .Put("UpdateGroups", ["CI_ID", "LocalizedDisplayName", "NumberOfUpdates", "NumberOfExpiredUpdates", "ContainsSupersededUpdates"], ["1", "Old", "10", "5", "True"], ["2", "Huge", "1200", "0", "False"])
            .Put("ExpiredUpdates", ["CI_ID"], ["7"])
            .Put("UpdateContentMap", ["CI_ID", "ContentID"], ["7", "70"])
            .Put("UpdatePackageContent", ["PackageID", "ContentID"], ["MCM00060", "70"], ["MCM00060", "71"])
            .Put("UpdatePackages", ["PackageID", "Name"], ["MCM00060", "Updates 2025"])
            .Put("Drivers", ["CI_ID", "LocalizedDisplayName"], ["90", "NIC"], ["91", "Packaged"])
            .Put("DriverContainers", ["CI_ID"], ["91"])
            .Put("AdminUsers", ["AdminID", "LogonName", "IsDeleted", "RoleNames"], ["5", "CONTOSO\\gone", "True", "Full Administrator|Read-only Analyst"])
            .Put("DistributionPoints", ["NALPath", "Name"], ["NAL-1", "\\\\DP1"])
            .Put("BoundaryGroupSiteSystems", ["ServerNALPath"], ["NAL-2"]);
        var (findings, _) = SiteHygieneParity.Native(read, "Updates", "UpdateContent", "Drivers", "Security", "DistributionPoints");
        Assert.Contains("50%", Assert.Single(findings, f => f.CheckId == "UPD-01").Evidence);
        Assert.Equal("2", Assert.Single(findings, f => f.CheckId == "UPD-04").ObjectKey);
        Assert.Contains("1 of 2 content item(s)", Assert.Single(findings, f => f.CheckId == "UPD-05").Evidence);
        Assert.Equal("90", Assert.Single(findings, f => f.CheckId == "DRV-01").ObjectKey);
        Assert.Contains("Full Administrator, Read-only Analyst", Assert.Single(findings, f => f.CheckId == "SEC-01").Evidence);
        Assert.Equal("DP1", Assert.Single(findings, f => f.CheckId == "DPT-01").ObjectName);
    }

    [Fact]
    public void AnErroringRuleOutranksADisabledOneAndAStaleRuleCanRunAgain()
    {
        var read = SiteHygieneParity.EmptySite().Put("AutoDeploymentRules", ["AutoDeploymentID", "Name", "AutoDeploymentEnabled", "LastRunTime", "LastErrorCode"],
            ["1", "Erroring", "False", "2026-10-20T00:00:00", "-2147467259"],
            ["2", "Stale", "True", "2026-08-01T00:00:00", "0"],
            ["3", "Fine", "True", "2026-10-20T00:00:00", "0"]);
        var (findings, _) = SiteHygieneParity.Native(read, "Updates");
        var rules = findings.Where(f => f.CheckId == "UPD-03").ToDictionary(f => f.ObjectKey);
        Assert.Equal(2, rules.Count);
        Assert.Equal(HygieneSeverity.Error, rules["1"].Severity);
        Assert.Equal(HygieneFixKind.None, rules["1"].FixKind);
        Assert.Equal("Invoke-CMSoftwareUpdateAutoDeploymentRule -Id 2", rules["2"].FixText);
    }

    [Fact]
    public void OverlappingRangesArePairedOnceInSourceOrder()
    {
        var read = SiteHygieneParity.EmptySite().Put("Boundaries", ["BoundaryID", "DisplayName", "Value", "BoundaryType", "GroupCount"],
            ["1", "C", "10.0.0.200-10.0.0.250", "3", "1"],
            ["2", "A", "10.0.0.1-10.0.0.100", "3", "1"],
            ["3", "B", "10.0.0.50-10.0.0.210", "3", "1"],
            ["4", "Subnet", "10.0.0.0", "0", "1"],
            ["5", "Bad", "x-y", "3", "1"]);
        var (findings, _) = SiteHygieneParity.Native(read, "Boundaries");
        Assert.Equal(["10.0.0.1-10.0.0.100 overlaps 10.0.0.50-10.0.0.210", "10.0.0.200-10.0.0.250 overlaps 10.0.0.50-10.0.0.210"],
            findings.Where(f => f.CheckId == "BND-03").Select(f => f.ObjectName).Order(StringComparer.Ordinal));
    }
}
