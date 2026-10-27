using System.Runtime.CompilerServices;
using System.Windows.Threading;
using ApplicationPackagerUI.Services;
using ApplicationPackagerUI.ViewModels.Pages;
using ApplicationPackagerUI.Views.Pages;

namespace ApplicationPackagerEngine.Tests.AddFlow;

public sealed class AddFileDropRoutingTests
{
    private static void OnSta(Action test) => StaTestHelper.Run(() =>
    {
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        test();
    });

    [Fact]
    public void ADropOnTheGridOpensAddFromFileWithTheFilePreloaded() => OnSta(() =>
    {
        var service = new FakeAddService();
        var addFile = new AddFileViewModel(service: service, prompts: new FakeAddPrompts(), results: new AddResultViewModel(new FakeResultActions()));
        var shown = new List<Type>();
        var route = new AddFileRoute(addFile: addFile, navigate: shown.Add);
        var packages = (PackagesViewModel)RuntimeHelpers.GetUninitializedObject(typeof(PackagesViewModel));
        packages.AddFileRoute = route;

        var drop = packages.HandleDroppedFilesCommand.ExecuteAsync(new[] { @"C:\in\contoso.msi" });
        StaTestHelper.PumpUntil(() => drop.IsCompleted);

        Assert.Equal([typeof(AddFilePage)], shown);
        Assert.Equal(@"C:\in\contoso.msi", service.WaitForAnalysis(0).Path);
        Assert.Equal(AddFileStage.Analyzing, addFile.Stage);
    });

    [Fact]
    public void ADropOfTwoFilesOpensTheSamePageAndSaysOneAtATime() => OnSta(() =>
    {
        var service = new FakeAddService();
        var addFile = new AddFileViewModel(service: service, prompts: new FakeAddPrompts(), results: new AddResultViewModel(new FakeResultActions()));
        var shown = new List<Type>();
        new AddFileRoute(addFile: addFile, navigate: shown.Add).Route([@"C:\in\a.msi", @"C:\in\b.msi"]);

        Assert.Equal([typeof(AddFilePage)], shown);
        Assert.Empty(service.Analyses);
        Assert.Contains("one installer at a time", addFile.StatusText);
    });

    [Fact]
    public void ADroppedFileOfAnotherTypeIsRefusedOnThePage() => OnSta(() =>
    {
        var service = new FakeAddService();
        var addFile = new AddFileViewModel(service: service, prompts: new FakeAddPrompts(), results: new AddResultViewModel(new FakeResultActions()));
        new AddFileRoute(addFile: addFile, navigate: _ => { }).Route([@"C:\in\notes.txt"]);
        StaTestHelper.Flush();

        Assert.Empty(service.Analyses);
        Assert.Contains("cannot be added", addFile.StatusText);
    });
}
