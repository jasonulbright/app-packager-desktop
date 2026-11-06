using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.CmImport;
using ApplicationPackagerUI.ViewModels.Windows;

namespace ApplicationPackagerEngine.Tests.CmImport;

/// <summary>The conversion window keeps the switches consistent and shows failures as a status line.</summary>
public sealed class ConvertFromCmViewModelTests
{
    private sealed class FakeRunner : ICmImportRunner
    {
        public CmImportRequest? Last { get; private set; }
        public string? Fail { get; init; }
        public int Calls { get; private set; }

        private Task<CmImportOutcome> Answer(CmImportRequest request)
        {
            Calls++;
            Last = request;
            if (Fail is not null) throw new InvalidDataException(Fail);
            var plan = new CmImportPlan { ApplicationName = "App" };
            return Task.FromResult(new CmImportOutcome(plan, "REPORT", null, []));
        }

        public Task<CmImportOutcome> PreviewAsync(CmImportRequest request, Action<string>? log = null, CancellationToken ct = default) => Answer(request);

        public Task<CmImportOutcome> ConvertAsync(CmImportRequest request, Action<string>? log = null, CancellationToken ct = default) => Answer(request);
    }

    private static ConvertFromCmViewModel Create(FakeRunner runner, IntunePublishOptions? publish = null) =>
        new(runner, @"C:\out", () => publish);

    [Fact]
    public void PublishTurnsOnThePackagesAndTurnsOffSkipContent()
    {
        var vm = Create(new FakeRunner());
        vm.SkipContent = true;
        vm.Publish = true;
        Assert.True(vm.BuildIntuneWin);
        Assert.False(vm.SkipContent);
    }

    [Fact]
    public void TurningOffThePackagesTurnsOffPublish()
    {
        var vm = Create(new FakeRunner());
        vm.Publish = true;
        vm.BuildIntuneWin = false;
        Assert.False(vm.Publish);
    }

    [Fact]
    public void SkipContentTurnsOffPackagesAndPublish()
    {
        var vm = Create(new FakeRunner());
        vm.Publish = true;
        vm.SkipContent = true;
        Assert.False(vm.BuildIntuneWin);
        Assert.False(vm.Publish);
    }

    [Fact]
    public void TheButtonsNeedAnApplicationName()
    {
        var vm = Create(new FakeRunner());
        Assert.False(vm.PreviewCommand.CanExecute(null));
        vm.ApplicationName = "X";
        Assert.True(vm.PreviewCommand.CanExecute(null));
        Assert.True(vm.ConvertCommand.CanExecute(null));
    }

    [Fact]
    public async Task PreviewSendsTheChosenOptionsAndShowsTheReport()
    {
        var runner = new FakeRunner();
        var vm = Create(runner);
        vm.ApplicationName = "X";
        vm.AssignByGroup = true;
        vm.NativeDetectionOnly = true;
        await vm.PreviewCommand.ExecuteAsync(null);
        Assert.Equal("REPORT", vm.ReportText);
        Assert.Equal(CmImportPlanner.AssignByGroup, runner.Last!.Options.VariantStrategy);
        Assert.True(runner.Last.Options.NativeDetectionOnly);
        Assert.Null(runner.Last.Publish);
        Assert.True(vm.Idle);
    }

    [Fact]
    public async Task ConvertWithPublishAndNoTenantValuesStopsBeforeTheRunner()
    {
        var runner = new FakeRunner();
        var vm = Create(runner, publish: null);
        vm.ApplicationName = "X";
        vm.Publish = true;
        await vm.ConvertCommand.ExecuteAsync(null);
        Assert.Equal(0, runner.Calls);
        Assert.Contains("Settings", vm.StatusText);
    }

    [Fact]
    public async Task ConvertWithPublishPassesTheStoredTenantValues()
    {
        var runner = new FakeRunner();
        var publish = new IntunePublishOptions { TenantId = "t", ClientId = "c", ClientSecret = "s" };
        var vm = Create(runner, publish);
        vm.ApplicationName = "X";
        vm.Publish = true;
        await vm.ConvertCommand.ExecuteAsync(null);
        Assert.Same(publish, runner.Last!.Publish);
        Assert.True(runner.Last.Package);
    }

    [Fact]
    public async Task AFailureBecomesAStatusLineAndTheWindowIsUsableAgain()
    {
        var vm = Create(new FakeRunner { Fail = "no such application" });
        vm.ApplicationName = "X";
        await vm.PreviewCommand.ExecuteAsync(null);
        Assert.Contains("no such application", vm.StatusText);
        Assert.True(vm.Idle);
    }
}
