using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Models.Signing;
using ApplicationPackagerUI.Models;
using ApplicationPackagerUI.Services;

namespace ApplicationPackagerUI.ViewModels.Pages;

/// <summary>
/// The dirty tracker of Settings (the "No unsaved changes" line). It compares the edit buffer of the page with a snapshot of the buffer
/// as it was loaded or saved, value by value: an edit that is changed back reads as clean again. A text that Save Settings trims is
/// compared trimmed. A value that Save Settings cannot store as typed (a bad port, an empty runtime) keeps the page dirty after a save.
/// A typed secret is never copied into the snapshot; the snapshot holds only that the box is not empty. The Sidebar applies each change
/// at once and is not part of the buffer.
/// </summary>
public partial class SettingsViewModel
{
    private const string OneClickRowPrefix = "oneclick.row/";
    private const string InstallPrefix = "install/";
    private const string PathPrefix = "path/";

    /// <summary>The entries that describe an edit by itself: their clean value is empty, whatever the buffer holds at the time of a snapshot.</summary>
    private static readonly string[] EditOnlyKeys = ["profile", "secret.intune", "secret.nvd", "secret.github", "remove.nvd", "remove.github"];

    private readonly Dictionary<string, string> _baseline = new(StringComparer.Ordinal);
    private bool _baselineReady;
    private bool _loadingSettings;
    private int _dirtyHold;
    private bool _hasPendingEntry;

    /// <summary>True when the page holds an edit that is not saved.</summary>
    [ObservableProperty] private bool _hasUnsavedChanges;

    /// <summary>The line under the list of pages.</summary>
    public string UnsavedChangesText => HasUnsavedChanges ? "Unsaved changes" : "No unsaved changes";

    partial void OnHasUnsavedChangesChanged(bool value) => OnPropertyChanged(nameof(UnsavedChangesText));

    /// <summary>
    /// True while a box that commits its value when it loses the focus (a number box, a text cell of a grid) holds typed text that the
    /// buffer does not have yet. The page sets it, so the line never reads clean while someone types.
    /// </summary>
    public bool HasPendingEntry
    {
        get => _hasPendingEntry;
        set
        {
            if (_hasPendingEntry == value) return;
            _hasPendingEntry = value;
            RefreshDirtyState();
        }
    }

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        // The dirty line itself, and the search box, the bulk bar and the check marks of the grid, hold no setting.
        if (e.PropertyName is not (nameof(HasUnsavedChanges) or nameof(UnsavedChangesText) or nameof(SupersedenceSelectedCount)
            or nameof(ApplicationFilter) or nameof(BulkColumn) or nameof(BulkValue) or nameof(BulkScope) or nameof(BulkValues)))
            RefreshDirtyState();
    }

    /// <summary>The editors of the sections raise their own change notices; the dirty line follows them too.</summary>
    private void TrackEditors()
    {
        foreach (var editor in new INotifyPropertyChanged[] { TitleOptions, Signing, Discovery, Wsus })
            editor.PropertyChanged += (_, _) => RefreshDirtyState();
    }

    /// <summary>
    /// Compares the buffer with the snapshot. The view calls it after an input event too, because some values of the buffer (the Citrix
    /// switches and the install parameters) raise no change notice.
    /// </summary>
    public void RefreshDirtyState()
    {
        if (!_baselineReady || _loadingSettings || _dirtyHold > 0) return;
        HasUnsavedChanges = _hasPendingEntry || ChangedEntries().Count > 0;
    }

    /// <summary>The names of the entries of the buffer that differ from the snapshot. It holds no value, so it is safe to show.</summary>
    public IReadOnlyList<string> ChangedEntries()
    {
        var now = CaptureEditBuffer();
        return now.Keys.Union(_baseline.Keys, StringComparer.Ordinal)
            .Where(k => !now.TryGetValue(k, out var a) || !_baseline.TryGetValue(k, out var b) || !string.Equals(a, b, StringComparison.Ordinal))
            .ToList();
    }

    /// <summary>Holds the comparison while an action changes many rows; the last release compares once.</summary>
    private IDisposable HoldDirtyState()
    {
        _dirtyHold++;
        return new DirtyHold(this);
    }

    private sealed class DirtyHold(SettingsViewModel owner) : IDisposable
    {
        public void Dispose()
        {
            if (--owner._dirtyHold == 0) owner.RefreshDirtyState();
        }
    }

    /// <summary>The whole buffer is the saved state: after a load, and after a save that wrote the file.</summary>
    private void AdoptBaseline()
    {
        _baseline.Clear();
        foreach (var (key, value) in CaptureEditBuffer()) _baseline[key] = value;
        foreach (var key in EditOnlyKeys) _baseline[key] = "";
        _baselineReady = true;
    }

    /// <summary>One group of entries was read from the store again (the lists that load late or on demand): only that group is the saved state.</summary>
    private void AdoptSection(string prefix)
    {
        if (!_baselineReady || _loadingSettings) return;
        foreach (var key in _baseline.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList()) _baseline.Remove(key);
        foreach (var (key, value) in CaptureEditBuffer())
            if (key.StartsWith(prefix, StringComparison.Ordinal)) _baseline[key] = value;
        RefreshDirtyState();
    }

    /// <summary>
    /// The snapshot after a save. Where Save Settings stored another value than the one in a box, the snapshot takes the stored value,
    /// so the line keeps saying that the box is not saved.
    /// </summary>
    private void AdoptSavedBaseline(AppSettings saved)
    {
        AdoptBaseline();
        _baseline["EstimatedRuntimeMins"] = Number(saved.EstimatedRuntimeMins);
        _baseline["MaximumRuntimeMins"] = Number(saved.MaximumRuntimeMins);
        _baseline["OneClickCadenceDays"] = Number(saved.OneClick.DefaultCadenceDays);
        _baseline["wsus.Port"] = saved.Wsus.PortNumber.ToString(CultureInfo.InvariantCulture);
        foreach (var row in OneClickRows)
            _baseline[OneClickRowPrefix + row.Uid] = RowText(row, row.CadenceOverride is null ? "" : row.CadenceDisplay.Trim());
    }

    private static string Number(double? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "";

    private static string RowText(OneClickRow row, string cadence) =>
        Join(row.Tracked, cadence, row.ConfigMgr, row.Wsus, row.Intune);

    private static string Join(params object?[] values) => JsonSerializer.Serialize(values);

    private static string Trimmed(string? value) => (value ?? "").Trim();

    private Dictionary<string, string> CaptureEditBuffer()
    {
        var buffer = new Dictionary<string, string>(StringComparer.Ordinal);
        void Put(string key, object? value) => buffer[key] = value?.ToString() ?? "";

        Put(nameof(CompanyName), CompanyName);
        Put(nameof(CheckForUpdatesAtStartup), CheckForUpdatesAtStartup);
        Put(nameof(CurrentTheme), CurrentTheme);
        Put(nameof(SiteCode), SiteCode);
        Put(nameof(ProviderMachine), Trimmed(ProviderMachine));
        Put(nameof(FileShareRoot), FileShareRoot);
        Put(nameof(DownloadRoot), DownloadRoot);
        Put(nameof(PackagersRoot), PackagersRoot);
        Put("discovery.SqlServer", Trimmed(Discovery.SqlServer));
        Put("discovery.SqlDatabase", Trimmed(Discovery.SqlDatabase));
        Put("discovery.Encrypt", Discovery.EncryptConnection);
        Put("discovery.TrustCertificate", Discovery.TrustServerCertificate);
        Put("discovery.ClearDismissed", Discovery.ClearPending);

        Put(nameof(EstimatedRuntimeMins), Number(EstimatedRuntimeMins));
        Put(nameof(MaximumRuntimeMins), Number(MaximumRuntimeMins));
        Put(nameof(ContentFallback), ContentFallback);
        Put(nameof(SlowNetworkDeploymentMode), SlowNetworkDeploymentMode);
        Put("title", Join(TitleOptions.IncludeVersionInTitle, TitleOptions.RemoveVersionFromTitle, TitleOptions.AppendArchitectureToTitle));
        Put("signing", Join(JsonSerializer.Serialize(Signing.ToPolicy()), Signing.StoreLocation, Trimmed(Signing.TimestampServer)));
        Put(nameof(M365Channel), M365Channel);

        Put(nameof(ContentLayout), ContentLayout);
        Put(nameof(AutoDistribute), AutoDistribute);
        Put(nameof(DpGroupName), Trimmed(DpGroupName));
        Put(nameof(DeployToTestCollection), DeployToTestCollection);
        Put(nameof(TestCollectionName), Trimmed(TestCollectionName));
        Put(nameof(TestCollectionIsUser), TestCollectionIsUser);
        Put(nameof(CreateTestCollectionIfMissing), CreateTestCollectionIfMissing);

        Put(nameof(UseConfigMgr), UseConfigMgr);
        Put(nameof(UseIntune), UseIntune);
        Put(nameof(UseWsus), UseWsus);
        Put("wsus.ServerName", Trimmed(Wsus.ServerName));
        Put("wsus.Port", Trimmed(Wsus.Port));
        Put("wsus.UseSsl", Wsus.UseSsl);
        Put("wsus.Classification", Wsus.Classification);
        Put("wsus.ApprovalGroup", Trimmed(Wsus.ApprovalGroup));
        Put("wsus.DeclineSuperseded", Wsus.DeclineSuperseded);

        Put(nameof(IntuneTenantId), Trimmed(IntuneTenantId));
        Put(nameof(IntuneClientId), Trimmed(IntuneClientId));
        Put(nameof(IntuneMinimumWindowsRelease), IntuneMinimumWindowsRelease);

        Put(nameof(SkipConfigReview), SkipConfigReview);
        Put(nameof(LookupCvesAfterCheckMecm), LookupCvesAfterCheckMecm);
        Put("citrix", JsonSerializer.Serialize(TrimSwitches(CwaSwitches)));
        foreach (var group in InstallParameterGroups)
        {
            // The panel writes the default of a parameter when it first shows; a missing value and its default are the same value.
            var values = new SortedDictionary<string, string>(group.Values, StringComparer.Ordinal);
            foreach (var parameter in group.Parameters)
                if (!values.ContainsKey(parameter.Key)) values[parameter.Key] = parameter.DefaultValue;
            Put(InstallPrefix + group.DefinitionId, JsonSerializer.Serialize(values));
        }
        foreach (var row in LocalSourceRows) Put(PathPrefix + "source/" + row.Key, Trimmed(row.Path));
        foreach (var row in SideFileRows) Put(PathPrefix + "side/" + row.Key, Trimmed(row.Path));

        Put(nameof(OneClickAction), OneClickAction);
        Put(nameof(OneClickForce), OneClickForce);
        Put(nameof(OneClickCadenceDays), Number(OneClickCadenceDays));
        Put("oneclick.defaults", Join(OneClickDefaultConfigMgr, OneClickDefaultWsus, OneClickDefaultIntune));
        Put(nameof(OneClickOnExisting), OneClickOnExisting);
        foreach (var row in OneClickRows) Put(OneClickRowPrefix + row.Uid, RowText(row, row.CadenceDisplay.Trim()));

        Put(nameof(ConfigMgrVersionRule), ConfigMgrVersionRule);
        Put(nameof(IntuneVersionRule), IntuneVersionRule);
        Put(nameof(SupersedenceMode), SupersedenceMode);
        Put("grid.supersedence", Lines((_supersedenceWork.TitleOverrides ?? new())
            .Where(p => !string.IsNullOrWhiteSpace(p.Value.Mode) || !string.IsNullOrWhiteSpace(p.Value.VersionRule))
            .Select(p => (p.Key, Join(p.Value.Mode ?? "", p.Value.VersionRule ?? "")))));
        Put("grid.conditions", Lines(ApplicationGridSettings.Copy(_work.Conditions).Select(p => (p.Key, JsonSerializer.Serialize(p.Value)))));
        Put("grid.commands", Lines(_work.Commands.Select(p => (p.Key, Join(p.Value.Install ?? "", p.Value.Uninstall ?? "")))));
        Put("grid.distribution", Lines(_work.Distribution.Select(p => (p.Key, Join(p.Value.DpGroupName ?? "", p.Value.TestCollectionName ?? "")))));

        Put("profile", Lines(_work.ProfileEdits().Select(e => (e.Key, e.Value))));
        // The secrets: only the fact that a box is not empty. The conditions are the ones of Save Settings.
        Put("secret.intune", string.IsNullOrEmpty(IntuneClientSecret) ? "" : "typed");
        Put("secret.nvd", string.IsNullOrWhiteSpace(NvdApiKey) ? "" : "typed");
        Put("secret.github", string.IsNullOrWhiteSpace(GitHubToken) ? "" : "typed");
        Put("remove.nvd", RemoveNvdApiKey && HasStoredNvdApiKey ? "yes" : "");
        Put("remove.github", RemoveGitHubToken && HasStoredGitHubToken ? "yes" : "");
        return buffer;
    }

    /// <summary>The entries of a map in one fixed order, with the identifiers compared without case.</summary>
    private static string Lines(IEnumerable<(string Key, string Value)> entries) =>
        string.Join("\n", entries.Select(e => Join(e.Key.ToUpperInvariant(), e.Value)).Order(StringComparer.Ordinal));
}
