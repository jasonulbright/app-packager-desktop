using System.Windows.Media.Imaging;
using ApplicationPackagerUI;
using ApplicationPackagerUI.Models;
using ApplicationPackagerUI.Services;
using ApplicationPackagerUI.ViewModels.Pages;
using Microsoft.Extensions.DependencyInjection;

namespace MockProof;

internal sealed partial class Scene
{
    [Screen("configmgr")]
    private async Task<BitmapSource> ConfigMgrSettings()
    {
        await OpenSettingsAsync("configmgr");
        return Capture(_main);
    }

    [Screen("supersedence")]
    private async Task<BitmapSource> ApplicationSettings()
    {
        var settings = await OpenSettingsAsync("supersedence");
        LoadOverrideRows(settings);
        await SettleAsync();
        return Capture(_main);
    }

    private async Task<SettingsViewModel> OpenSettingsAsync(string page)
    {
        _main.OpenSettingsPage(page);
        await SettleAsync();
        await SettleAsync();
        return App.Services.GetRequiredService<SettingsViewModel>();
    }

    /// <summary>
    /// The rows of the override table of the mock: the first twelve applications of its list. The rows of the mock have no definition file,
    /// so each cell is read through the registered columns here. 7-Zip is the one application with a choice of architecture.
    /// </summary>
    private static void LoadOverrideRows(SettingsViewModel settings)
    {
        var work = new ApplicationWork();
        settings.SupersedenceRows.Clear();
        foreach (var app in MockData.Rows().Take(12))
        {
            var row = new SupersedenceRow
            {
                Uid = app.Uid, ApplicationId = "catalog:" + app.Uid, DefinitionId = app.Uid, Application = app.Application, Vendor = app.Vendor,
                Architecture = "x64", OffersArchitecture = app.Application.StartsWith("7-Zip", StringComparison.Ordinal),
            };
            var oneClick = new OneClickRow { Uid = app.Uid, Application = app.Application, Vendor = app.Vendor };
            work.OneClickRowOf = _ => oneClick;
            foreach (var column in ApplicationColumns.All)
            {
                var reason = column.NotApplicable(work, row);
                var value = column.Get(work, row);
                if (column.Choices is { } choices) row.Lists[column.Id] = choices.Contains(value) ? choices : [value];
                row.Cells[column.Id] = value;
                row.Editable[column.Id] = reason is null;
                row.Tips[column.Id] = reason ?? column.Tooltip;
            }
            settings.SupersedenceRows.Add(row);
        }
    }
}
