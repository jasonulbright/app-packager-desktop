using ApplicationPackagerEngine.Services;
using Microsoft.Win32;

namespace ApplicationPackagerEngine.Tests.DetectionTester;

public sealed class InstalledApplicationsServiceTests
{
    private const string Uninstall = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    private static FakeRegistryProvider App(FakeRegistryProvider registry, RegistryHive hive, RegistryView view,
        string id, string? name, params (string Name, object Value)[] values)
    {
        var path = $@"{Uninstall}\{id}";
        registry.AddKey(hive, view, path);
        if (name is not null) registry.Set(hive, view, path, "DisplayName", name);
        foreach (var (valueName, value) in values) registry.Set(hive, view, path, valueName, value,
            value is int ? RegistryValueKind.DWord : RegistryValueKind.String);
        return registry;
    }

    [Fact]
    public void ReadsTheThreeHivesWithEveryField()
    {
        var registry = new FakeRegistryProvider();
        App(registry, RegistryHive.LocalMachine, RegistryView.Registry64, "{A}", "Alpha",
            ("Publisher", "Contoso"), ("DisplayVersion", "1.2"), ("InstallDate", "20261001"),
            ("UninstallString", "msiexec /x {A}"), ("QuietUninstallString", "msiexec /x {A} /qn"),
            ("InstallLocation", @"C:\Program Files\Alpha"));
        App(registry, RegistryHive.LocalMachine, RegistryView.Registry32, "Beta", "Beta");
        App(registry, RegistryHive.CurrentUser, RegistryView.Registry64, "Gamma", "Gamma");

        var apps = new InstalledApplicationsService(registry).Enumerate();

        Assert.Equal(["Alpha", "Beta", "Gamma"], apps.Select(a => a.Name));
        var alpha = apps[0];
        Assert.Equal(new InstalledApplication("Alpha", "Contoso", "1.2", "20261001", "HKLM 64-bit",
            $@"HKLM\{Uninstall}\{{A}}", "msiexec /x {A}", "msiexec /x {A} /qn", @"C:\Program Files\Alpha"), alpha);
        Assert.Equal("HKLM 32-bit", apps[1].Source);
        Assert.Equal($@"HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\Beta", apps[1].KeyPath);
        Assert.Equal("HKCU", apps[2].Source);
        Assert.Equal($@"HKCU\{Uninstall}\Gamma", apps[2].KeyPath);
    }

    [Fact]
    public void AnEntryInTwoHivesIsKeptOnceForEachHive()
    {
        var registry = new FakeRegistryProvider();
        App(registry, RegistryHive.LocalMachine, RegistryView.Registry64, "Same", "Same App");
        App(registry, RegistryHive.LocalMachine, RegistryView.Registry32, "Same", "Same App");

        var apps = new InstalledApplicationsService(registry).Enumerate();

        Assert.Equal(2, apps.Count);
        Assert.Equal(["HKLM 64-bit", "HKLM 32-bit"], apps.Select(a => a.Source));
    }

    [Fact]
    public void EntriesWithoutADisplayNameAreSkipped()
    {
        var registry = new FakeRegistryProvider();
        App(registry, RegistryHive.LocalMachine, RegistryView.Registry64, "NoName", null, ("Publisher", "Contoso"));
        App(registry, RegistryHive.LocalMachine, RegistryView.Registry64, "Blank", "   ");
        App(registry, RegistryHive.LocalMachine, RegistryView.Registry64, "Named", "Named");

        Assert.Equal(["Named"], new InstalledApplicationsService(registry).Enumerate().Select(a => a.Name));
    }

    [Fact]
    public void SystemComponentsAreSkippedUnlessAsked()
    {
        var registry = new FakeRegistryProvider();
        App(registry, RegistryHive.LocalMachine, RegistryView.Registry64, "Sys", "Runtime Part", ("SystemComponent", 1));
        App(registry, RegistryHive.LocalMachine, RegistryView.Registry64, "App", "Visible");

        var service = new InstalledApplicationsService(registry);
        Assert.Equal(["Visible"], service.Enumerate().Select(a => a.Name));
        Assert.Equal(["Runtime Part", "Visible"], service.Enumerate(includeSystemComponents: true).Select(a => a.Name));
        Assert.Equal(["Visible"], service.Enumerate(includeSystemComponents: false).Select(a => a.Name));
    }

    [Fact]
    public void ASystemComponentFlagOfZeroOrTextOneIsReadAsWritten()
    {
        var registry = new FakeRegistryProvider();
        App(registry, RegistryHive.LocalMachine, RegistryView.Registry64, "Zero", "Flag Zero", ("SystemComponent", 0));
        App(registry, RegistryHive.CurrentUser, RegistryView.Registry64, "Text", "Flag Text", ("SystemComponent", "1"));

        var service = new InstalledApplicationsService(registry);
        Assert.Equal(["Flag Zero"], service.Enumerate().Select(a => a.Name));
        Assert.Equal(["Flag Text", "Flag Zero"], service.Enumerate(includeSystemComponents: true).Select(a => a.Name));
    }

    [Fact]
    public void AnUnreadableEntryOrHiveIsSkipped()
    {
        var registry = new FakeRegistryProvider();
        App(registry, RegistryHive.LocalMachine, RegistryView.Registry64, "Locked", "Locked");
        registry.Deny(RegistryHive.LocalMachine, RegistryView.Registry64, $@"{Uninstall}\Locked");
        App(registry, RegistryHive.LocalMachine, RegistryView.Registry64, "Open", "Open");
        App(registry, RegistryHive.LocalMachine, RegistryView.Registry32, "Hidden", "Hidden");
        registry.DenySubKeyList(RegistryHive.LocalMachine, RegistryView.Registry32, Uninstall);
        App(registry, RegistryHive.CurrentUser, RegistryView.Registry64, "Mine", "Mine");
        registry.Deny(RegistryHive.CurrentUser, RegistryView.Registry64, Uninstall);

        Assert.Equal(["Open"], new InstalledApplicationsService(registry).Enumerate().Select(a => a.Name));
    }

    [Fact]
    public void AMissingUninstallKeyYieldsNoEntries() =>
        Assert.Empty(new InstalledApplicationsService(new FakeRegistryProvider()).Enumerate());

    [Fact]
    public void NamesSortWithoutRegardToCase()
    {
        var registry = new FakeRegistryProvider();
        App(registry, RegistryHive.LocalMachine, RegistryView.Registry64, "1", "beta");
        App(registry, RegistryHive.LocalMachine, RegistryView.Registry64, "2", "Alpha");

        Assert.Equal(["Alpha", "beta"], new InstalledApplicationsService(registry).Enumerate().Select(a => a.Name));
    }

    [Fact]
    public void ThisComputerListsAtLeastOneApplication() =>
        Assert.NotEmpty(new InstalledApplicationsService().Enumerate());
}
