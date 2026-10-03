using System.Globalization;
using System.IO;
using ApplicationPackagerEngine.Models.Workbench;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.Workbench;
using ApplicationPackagerUI.Services;

namespace ApplicationPackagerUI.ViewModels.Windows;

/// <summary>One stored installer revision of a BYO application.</summary>
public sealed record ByoSourceRow(int Revision, string FileName, string Version, string InstallerType, string Architecture,
    string Sha256, string Added, string Flags)
{
    public string ShortSha256 => Sha256.Length > 12 ? Sha256[..12] : Sha256;
}

/// <summary>Installer revisions of a BYO application, and portable bundle export and import.</summary>
public partial class WorkbenchWindowViewModel
{
    private const string BundleFilter = "Workbench bundle (*.zip)|*.zip|All files|*.*";

    [ObservableProperty] private bool _isByo;
    [ObservableProperty] private string _byoSourceText = "";
    public ObservableCollection<ByoSourceRow> ByoSources { get; } = [];

    private void LoadByoSources()
    {
        ByoSources.Clear();
        IsByo = SelectedApplication is not null && ApplicationIds.IsByo(SelectedApplication.ApplicationId);
        if (!IsByo || _store is null) return;
        var applicationId = SelectedApplication!.ApplicationId;
        IReadOnlyList<ByoSourceRevision> sources;
        try { sources = _store.GetByoSources(applicationId); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.Text.Json.JsonException)
        {
            ByoSourceText = "Source revisions could not be read: " + ex.Message;
            return;
        }
        foreach (var source in sources.Reverse())
        {
            ByoSources.Add(new ByoSourceRow(source.Revision, source.FileName, source.SoftwareVersion ?? "", source.InstallerType ?? "",
                source.Architecture ?? "", source.Sha256, AddedText(source.AddedAt),
                string.Join(", ", source.IncompatibleOverrides)));
        }
        if (sources.Count == 0)
        {
            ByoSourceText = "No installer revision is stored. Stage reports the missing revision; Replace installer adds one.";
            return;
        }
        var newest = sources[^1];
        var installer = _store.GetByoInstallerPath(applicationId, newest);
        ByoSourceText = File.Exists(installer)
            ? $"Stage uses revision {newest.Revision} ({newest.FileName}). Updates are manual: Replace installer adds a revision and keeps the earlier ones."
            : $"Revision {newest.Revision} has no installer in this data root; the bundle it came from did not include sources. Replace installer adds one.";
    }

    private static string AddedText(string? addedAt) =>
        DateTimeOffset.TryParse(addedAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at)
            ? at.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
            : addedAt ?? "";

    /// <summary>
    /// Analyses a replacement installer, shows its identity differences from the newest
    /// revision with the review of its values, and adds it as the next revision once accepted.
    /// </summary>
    [RelayCommand]
    private async Task ReplaceInstallerAsync()
    {
        if (!IsByo || Prompts is null || _store is null || SelectedApplication is null) return;
        var applicationId = SelectedApplication.ApplicationId;
        var file = Prompts.PickFiles("Replace installer", "Installers (*.msi;*.exe)|*.msi;*.exe|All files|*.*", multiselect: false).FirstOrDefault();
        if (file is null) return;

        DroppedInstallerAnalysis analysis;
        try { analysis = await _analyzer.AnalyzeAsync(file, deepInspect: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            await Prompts.InfoAsync("Replace Installer", "Analysis failed: " + ex.Message);
            return;
        }

        var identity = ByoInstallerIdentity.From(analysis);
        var definition = _store.GetApplicationDefinition(applicationId);
        var sources = _store.GetByoSources(applicationId);
        var previous = sources.Count == 0 ? 0 : sources[^1].Revision;
        var changes = _store.PreviewByoSource(applicationId, identity);
        var review = await Prompts.ReviewReplacementAsync(analysis, changes, previous + 1, previous,
            definition.DisplayName ?? SelectedApplication.DisplayName, definition.Publisher ?? "");
        if (review is null) return;

        ByoSourceUpdate update;
        try
        {
            update = await Task.Run(() => _store.UpdateByoSource(applicationId, file, identity,
                review.Overrides.SoftwareVersion, ByoStageValues.From(review.Overrides, review.Confirmed)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            await Prompts.InfoAsync("Replace Installer", ex.Message);
            return;
        }
        LoadByoSources();
        var blocking = update.IncompatibleOverrides.Count == 0
            ? ""
            : " Blocking changes: " + string.Join(", ", update.IncompatibleOverrides) + "; stage again before the next publish.";
        await Prompts.InfoAsync("Installer Replaced", $"Source revision {update.Revision} added for {definition.DisplayName}.{blocking}");
    }

    /// <summary>
    /// Writes the application, its profiles and assets as a portable bundle. BYO installer
    /// binaries go in only when the operator includes them; the bundle records it when they do not.
    /// </summary>
    [RelayCommand]
    private async Task ExportBundleAsync()
    {
        if (Prompts is null || _store is null || SelectedApplication is null) return;
        if (_session?.IsDirty == true && !await ConfirmDiscardAsync("Save the profile before the export? A bundle carries the saved profiles.")) return;
        var applicationId = SelectedApplication.ApplicationId;
        if (!Directory.Exists(_store.GetApplicationFolder(applicationId)))
        {
            await Prompts.InfoAsync("Export Bundle", $"Application '{applicationId}' has nothing stored to export.");
            return;
        }
        var includeSources = false;
        if (ApplicationIds.IsByo(applicationId))
        {
            includeSources = await Prompts.YesNoAsync("Export Bundle",
                "Include the installer binaries? Without them the bundle is marked IncompleteSource, and the imported application needs Replace installer before it stages.");
        }
        var path = Prompts.PickSaveFile("Export bundle", BundleFilter, BundleFileName(SelectedApplication));
        if (path is null) return;
        try
        {
            var result = await Task.Run(() => WorkbenchBundles.Export(_store, applicationId, path, includeSources));
            await Prompts.InfoAsync("Export Bundle",
                $"Exported {result.EntryCount} file(s) to {result.Path}. Sources: {(result.SourcesIncluded ? "included" : "not included (IncompleteSource)")}.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            await Prompts.InfoAsync("Export Bundle", ex.Message);
        }
    }

    /// <summary>
    /// Imports a bundle after its schema and every entry hash are verified. An application
    /// already stored under the bundle's id is replaced only when the operator confirms.
    /// </summary>
    [RelayCommand]
    private async Task ImportBundleAsync()
    {
        if (Prompts is null || _store is null) return;
        if (_session?.IsDirty == true && !await ConfirmDiscardAsync("Save the profile before the import?")) return;
        var path = Prompts.PickFiles("Import bundle", BundleFilter, multiselect: false).FirstOrDefault();
        if (path is null) return;
        try
        {
            var manifest = WorkbenchBundles.ReadManifest(path);
            var applicationId = WorkbenchBundles.TargetApplicationId(manifest, null);
            var force = false;
            if (Directory.Exists(_store.GetApplicationFolder(applicationId)))
            {
                if (!await Prompts.YesNoAsync("Import Bundle",
                        $"Application '{applicationId}' already exists in this data root. Replace it with the bundle's definition, profiles and assets?"))
                    return;
                force = true;
            }
            var result = await Task.Run(() => WorkbenchBundles.Import(_store, path, applicationId, force));
            await ReloadApplicationsAsync(result.ApplicationId);
            await Prompts.InfoAsync("Import Bundle",
                $"Imported {result.EntryCount} file(s) as {result.ApplicationId}. Source status: {result.SourceStatus}.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException
                                       or System.Text.Json.JsonException or InvalidDataException)
        {
            await Prompts.InfoAsync("Import Bundle", ex.Message);
        }
    }

    private async Task ReloadApplicationsAsync(string selectApplicationId)
    {
        var applications = await _catalog.LoadAsync(_store!);
        var selected = applications.FirstOrDefault(a => string.Equals(a.ApplicationId, selectApplicationId, StringComparison.OrdinalIgnoreCase))
                       ?? applications.FirstOrDefault();
        _loading = true;
        try
        {
            Applications = new ObservableCollection<WorkbenchApplicationItem>(applications);
            SelectedApplication = selected;
        }
        finally { _loading = false; }
        if (selected is not null) await OpenApplicationAsync(selected, null);
    }

    private static string BundleFileName(WorkbenchApplicationItem application)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var name = new string([.. application.DisplayName.Where(c => !invalid.Contains(c))]).Trim();
        return (name.Length == 0 ? ApplicationIds.ToKey(application.ApplicationId) : name) + ".bundle.zip";
    }
}
