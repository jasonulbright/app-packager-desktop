using System.Reflection;
using System.Windows.Media.Imaging;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerUI;
using ApplicationPackagerUI.Services;
using ApplicationPackagerUI.ViewModels.Pages;
using ApplicationPackagerUI.Views.Pages;
using Microsoft.Extensions.DependencyInjection;

namespace MockProof;

/// <summary>Add from web and the Deployment Helper, with the values and rows of the mock (assets/views.js and assets/deployment.js).</summary>
internal sealed partial class Scene
{
    private Wpf.Ui.Controls.NavigationView Navigation => (Wpf.Ui.Controls.NavigationView)_main.FindName("RootNavigation");

    [Screen("add-web")]
    private async Task<BitmapSource> AddWeb()
    {
        Navigation.Navigate(typeof(AddAppPage));
        _packages.StatusText = "Ready";
        await SettleAsync();
        return Capture(_main);
    }

    [Screen("deployment")]
    private async Task<BitmapSource> Deployment()
    {
        var viewModel = (DeploymentHelperViewModel)App.Services.GetRequiredService<SitePages>().GetService(typeof(DeploymentHelperViewModel))!;
        MockSite(viewModel);
        Navigation.Navigate(typeof(DeploymentHelperPage));
        _packages.StatusText = "Ready";
        await SettleAsync();
        var form = viewModel.Deploy;
        await Until(() => form.IsLoaded && !form.IsLoading, 60, "the objects and collections of the Deployment Helper");

        // The mock lists the collections in the order of its example, not by name.
        var collections = form.Collections.OrderBy(c => Array.FindIndex(Collections, x => x.Id == c.CollectionId)).ToList();
        form.Collections.Clear();
        foreach (var collection in collections) form.Collections.Add(collection);

        form.SelectedObject = form.Objects.Single(o => o.Name == "7-Zip 26.06 (x64 edition)");
        form.SelectedCollection = form.Collections.Single(c => c.Name == "All Workstations");
        await Until(() => !form.IsLoadingDetails && form.DpGroups.Count == 1, 30, "the distribution point group");
        form.AvailableDate = new DateTime(2026, 11, 24);
        form.AvailableTimeText = "08:10";
        // The lists hold the rows that the mock shows; its counts are those of the whole site.
        form.ObjectCount = "18 objects";
        form.CollectionCount = "27 collections";
        viewModel.Shared.StatusMessage = "";
        _packages.StatusText = "Ready";
        await SettleAsync();
        return Capture(_main);
    }

    private static readonly (string Name, string Version, string Id)[] Applications =
    [
        ("7-Zip (x64 edition) - 26.03", "26.03", "16779607"),
        ("7-Zip - 26.06", "26.06", "16786887"),
        ("7-Zip 26.03 (x64 edition)", "26.03", "16779604"),
        ("7-Zip 26.06 (x64 edition)", "26.06", "16784385"),
        ("Audacity 3.7.9", "3.7.9", "16777744"),
        ("CMTrace Open", "1.5.2", "16779146"),
    ];

    private static readonly (string Name, string Id, int Members)[] Collections =
    [
        ("All Servers", "MCM00016", 1),
        ("All Workstations", "MCM00384", 2),
        ("CH Lab 1.0.4", "MCM0002D", 0),
        ("Pilot Devices", "MCM0001F", 1),
        ("Copy of All Workstations", "MCM00383", 2),
        ("Test Devices", "MCM00025", 1),
    ];

    /// <summary>Gives the page a site that answers with the rows of the mock, so the scene reads nothing from ConfigMgr and writes nothing.</summary>
    private static void MockSite(DeploymentHelperViewModel viewModel)
    {
        var services = viewModel.Shared.Services;
        var replaced = services with
        {
            Deployments = Answers<IDeploymentService>((method, _) => method.Name switch
            {
                nameof(IDeploymentService.ListObjectsAsync) => Task.FromResult<IReadOnlyList<DeployableObject>>(Applications
                    .Select(a => new DeployableObject(Type: DeploymentObjectType.Application, Id: a.Id, Name: a.Name, Version: a.Version, Program: "", PackagedByProduct: false))
                    .ToList()),
                nameof(IDeploymentService.ListDpGroupsAsync) => Task.FromResult<IReadOnlyList<DpGroupInfo>>(
                    [new DpGroupInfo("Lab distribution points", "{GRP-1}", "Distributed", 1, 1, 0, 0)]),
                _ => throw new NotSupportedException(method.Name),
            }),
            Collections = Answers<ICollectionService>((method, _) => method.Name switch
            {
                nameof(ICollectionService.ListCollectionsAsync) => Task.FromResult<IReadOnlyList<CollectionRow>>(Collections
                    .Select(c => new CollectionRow(c.Id, c.Name, "", "", "", c.Members, false, "Periodic", "", 0, 0, 0, 0))
                    .ToList()),
                _ => throw new NotSupportedException(method.Name),
            }),
            Templates = Answers<IDeploymentTemplateStore>((method, _) => method.Name switch
            {
                nameof(IDeploymentTemplateStore.All) => Array.Empty<DeploymentTemplate>(),
                "get_Message" => null,
                _ => throw new NotSupportedException(method.Name),
            }),
            LoadPreferences = () => null,
            SavePreferences = _ => true,
        };
        typeof(DeploymentShared).GetField("<Services>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(viewModel.Shared, replaced);
    }

    private static T Answers<T>(Func<MethodInfo, object?[]?, object?> answer) where T : class
    {
        var proxy = DispatchProxy.Create<T, MockAnswers>();
        ((MockAnswers)(object)proxy).Answer = answer;
        return proxy;
    }
}

/// <summary>An interface whose members are answered by a function.</summary>
public class MockAnswers : DispatchProxy
{
    public Func<MethodInfo, object?[]?, object?> Answer { get; set; } = (_, _) => null;

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => Answer(targetMethod!, args);
}
