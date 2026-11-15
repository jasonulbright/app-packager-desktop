using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using ApplicationPackagerEngine.Services.VendorImport;
using ApplicationPackagerUI.Services;

namespace ApplicationPackagerUI.ViewModels.Windows;

/// <summary>One definition of the previewed document and what the import would do with it.</summary>
public sealed partial class VendorImportRow(ImportCandidate candidate) : ObservableObject
{
    public ImportCandidate Candidate { get; } = candidate;
    public string Id => Candidate.Definition.Id;
    public string Application => Candidate.Definition.Definition.Application;
    public string InstallerType => Candidate.Definition.Definition.InstallerType;
    public string Hosts => Candidate.Definition.Hosts.Count == 0 ? "none listed" : string.Join(", ", Candidate.Definition.Hosts);

    /// <summary>Whether the payload has a SHA-256 source, as the preview must say.</summary>
    public string PayloadHash
    {
        get
        {
            var d = Candidate.Definition.Definition;
            var source = !string.IsNullOrWhiteSpace(d.PayloadSha256) ? "PayloadSha256"
                : !string.IsNullOrWhiteSpace(d.PayloadSha256Url) ? "PayloadSha256Url"
                : !string.IsNullOrWhiteSpace(d.VersionScraper.PayloadHashQuery) ? "PayloadHashQuery" : null;
            var required = d.RequirePayloadHash || d.RequireValidSignature ? ", required" : "";
            return source is null ? "Payload SHA256 NOT verified (no source)" : source + required;
        }
    }

    public bool IsShippedConflict => Candidate.Conflict == ConflictKind.Shipped && !Candidate.Definition.Blocked;

    /// <summary>The administrator chose to replace the shipped definition with this Id (the default is to skip it).</summary>
    [ObservableProperty] private bool _replace;

    public string Status =>
        Candidate.Definition.Blocked
            ? "Blocked: " + (Candidate.Definition.CarriesCode
                ? "carries code (" + string.Join(", ", Candidate.Definition.CodeProperties) + ")"
                : Candidate.Definition.Findings.First(f => f.Severity == FindingSeverity.Blocking).Message)
            : Candidate.Conflict switch
            {
                ConflictKind.Shipped => "Same Id as a shipped definition: skipped unless replaced",
                ConflictKind.User => "Same Id as your own definition: skipped",
                ConflictKind.AnotherFeed => $"Same Id as the feed {Candidate.ConflictFeedId}: skipped",
                ConflictKind.StageFolder => "Same stage folder as another definition (Application and Architecture): skipped",
                _ => "Ready"
            };

    /// <summary>The preview text of the whole definition: the commands, the detection, the hosts, the findings, then the stored JSON.</summary>
    public string Details
    {
        get
        {
            var d = Candidate.Definition.Definition;
            var text = new StringBuilder();
            text.AppendLine($"{d.Vendor} - {d.Application} ({d.Architecture}), {d.InstallerType}");
            text.AppendLine("Hosts: " + Hosts);
            text.AppendLine("Payload hash: " + PayloadHash);
            text.AppendLine("Detection: " + d.Detection.Type);
            if (!string.IsNullOrWhiteSpace(d.InstallArgs)) text.AppendLine("Install arguments: " + d.InstallArgs);
            if (!string.IsNullOrWhiteSpace(d.UninstallCommand)) text.AppendLine("Uninstall command: " + d.UninstallCommand);
            foreach (var finding in Candidate.Definition.Findings) text.AppendLine($"[{finding.Severity}] {finding.Message}");
            text.AppendLine();
            text.Append(Candidate.Definition.Json);
            return text.ToString();
        }
    }
}

/// <summary>One stored feed on the list of imported feeds.</summary>
public sealed record VendorFeedRow(string FeedId, string Vendor, int Count, string Trust, string FetchedAt, string Address);

/// <summary>
/// The window that imports the definitions of a vendor feed from an address: fetch, a full preview, and the import only after the
/// confirmation. Nothing is written by Fetch or Cancel. It also lists the stored feeds, with Remove feed.
/// </summary>
public sealed partial class VendorImportViewModel : ObservableObject
{
    public const string WindowTitle = "Import Vendor Definitions from a URL";

    private readonly IVendorImportService _service;
    private readonly Func<string, string, Task<bool>> _confirm;
    private ImportPreview? _preview;
    private CancellationTokenSource? _fetching;

    public VendorImportViewModel(IVendorImportService service, Func<string, string, Task<bool>> confirm)
    {
        _service = service;
        _confirm = confirm;
        ReloadFeeds();
    }

    public string Title => WindowTitle;

    [ObservableProperty] private string _address = "";
    [ObservableProperty] private string _pinnedDigest = "";
    [ObservableProperty] private string _vendor = "";
    [ObservableProperty] private string _feedId = "";
    [ObservableProperty] private bool _confirmedNotVerified;
    [ObservableProperty] private bool _busy;
    [ObservableProperty] private string _statusText = "Enter the HTTPS address of the feed and select Fetch. Nothing is stored until you select Import.";
    [ObservableProperty] private string _sourceText = "";
    [ObservableProperty] private string _findingsText = "";
    [ObservableProperty] private string _trustLabel = "";
    [ObservableProperty] private VendorImportRow? _selectedRow;
    [ObservableProperty] private VendorFeedRow? _selectedFeed;

    public ObservableCollection<VendorImportRow> Rows { get; } = [];
    public ObservableCollection<VendorFeedRow> Feeds { get; } = [];

    public bool HasPreview => _preview is not null;
    public bool NeedsNotVerifiedConfirmation => _preview?.TrustState == FeedTrust.NotVerified;

    /// <summary>True once an import stored definitions: the Packages grid reloads when the window closes.</summary>
    public bool ImportedAny { get; private set; }

    public event EventHandler? CloseRequested;

    partial void OnBusyChanged(bool value)
    {
        FetchCommand.NotifyCanExecuteChanged();
        ImportCommand.NotifyCanExecuteChanged();
        CancelFetchCommand.NotifyCanExecuteChanged();
        RemoveFeedCommand.NotifyCanExecuteChanged();
    }

    partial void OnAddressChanged(string value) => FetchCommand.NotifyCanExecuteChanged();
    partial void OnVendorChanged(string value) => ImportCommand.NotifyCanExecuteChanged();
    partial void OnFeedIdChanged(string value) => ImportCommand.NotifyCanExecuteChanged();
    partial void OnConfirmedNotVerifiedChanged(bool value) => ImportCommand.NotifyCanExecuteChanged();
    partial void OnSelectedFeedChanged(VendorFeedRow? value) => RemoveFeedCommand.NotifyCanExecuteChanged();

    private bool CanFetch() => !Busy && Address.Trim().Length > 0;

    [RelayCommand(CanExecute = nameof(CanFetch))]
    private async Task FetchAsync()
    {
        ClearPreview();
        if (!Uri.TryCreate(Address.Trim(), UriKind.Absolute, out var address) || address.Scheme != Uri.UriSchemeHttps)
        {
            StatusText = "The address must be an absolute https:// address. Other schemes are refused.";
            return;
        }
        _fetching = new CancellationTokenSource();
        Busy = true;
        StatusText = $"Fetching from {address.Host}...";
        try
        {
            var preview = await _service.PreviewAsync(address, PinnedDigest, _fetching.Token);
            Show(preview);
        }
        catch (FeedFetchException ex) { StatusText = ex.Message + " Nothing was written."; }
        catch (OperationCanceledException) { StatusText = "Cancelled. Nothing was written."; }
        finally
        {
            Busy = false;
            _fetching.Dispose();
            _fetching = null;
        }
    }

    private bool CanCancelFetch() => Busy;

    [RelayCommand(CanExecute = nameof(CanCancelFetch))]
    private void CancelFetch() => _fetching?.Cancel();

    private void Show(ImportPreview preview)
    {
        _preview = preview;
        OnPropertyChanged(nameof(HasPreview));
        OnPropertyChanged(nameof(NeedsNotVerifiedConfirmation));
        TrustLabel = preview.TrustLabel;
        var document = preview.Document;
        SourceText = $"Address: {document.SafeAddress}\nSize: {document.Size:N0} bytes\nSHA-256: {document.Sha256}\n"
                     + $"Vendor in the document: {preview.Feed.Vendor ?? "none"}\nTrust state: {preview.TrustLabel}";
        var findings = preview.Feed.Findings.Select(f => $"[{f.Severity}] {f.Message}").ToList();
        if (preview.Refusal is not null) findings.Insert(0, "[Blocking] " + preview.Refusal);
        FindingsText = string.Join(Environment.NewLine, findings);
        if (preview.Refused)
        {
            StatusText = "The document is refused. Nothing was written.";
            ImportCommand.NotifyCanExecuteChanged();
            return;
        }
        Vendor = preview.Feed.Vendor ?? "";
        FeedId = preview.Feed.FeedId ?? "";
        foreach (var candidate in preview.Candidates)
        {
            var row = new VendorImportRow(candidate);
            row.PropertyChanged += (_, _) => ImportCommand.NotifyCanExecuteChanged();
            Rows.Add(row);
        }
        SelectedRow = Rows.FirstOrDefault();
        StatusText = $"{preview.Candidates.Count} definition(s) in the document, {preview.Candidates.Count(c => c.Importable)} ready. Nothing is stored yet.";
        ImportCommand.NotifyCanExecuteChanged();
    }

    private void ClearPreview()
    {
        _preview = null;
        Rows.Clear();
        SelectedRow = null;
        SourceText = "";
        FindingsText = "";
        TrustLabel = "";
        ConfirmedNotVerified = false;
        OnPropertyChanged(nameof(HasPreview));
        OnPropertyChanged(nameof(NeedsNotVerifiedConfirmation));
        ImportCommand.NotifyCanExecuteChanged();
    }

    private bool CanImport() =>
        !Busy && _preview is { Refused: false } preview
        && Vendor.Trim().Length > 0 && FeedId.Trim().Length > 0
        && (preview.TrustState != FeedTrust.NotVerified || ConfirmedNotVerified)
        && Rows.Any(r => r.Candidate.Importable || (r.IsShippedConflict && r.Replace));

    [RelayCommand(CanExecute = nameof(CanImport))]
    private async Task ImportAsync()
    {
        var preview = _preview!;
        var replace = Rows.Where(r => r.IsShippedConflict && r.Replace).Select(r => r.Id).ToList();
        if (replace.Count > 0
            && !await _confirm("Replace shipped definitions",
                $"Replace the shipped definition(s) {string.Join(", ", replace)} with the imported one(s)? The imported definition has its own profiles; the Workbench profiles of the shipped application stay with the shipped one."))
            return;
        Busy = true;
        try
        {
            var source = _service.Import(preview, new ImportConfirmation(
                Vendor.Trim(), FeedId.Trim(), ConfirmedNotVerified, new HashSet<string>(replace, StringComparer.OrdinalIgnoreCase)));
            ImportedAny = true;
            StatusText = $"Imported {source.Ids.Count} definition(s) from {source.Vendor} as the feed {source.FeedId} ({source.TrustState}).";
            ClearPreview();
            ReloadFeeds();
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            StatusText = "Not imported: " + ex.Message;
        }
        finally { Busy = false; }
    }

    private bool CanRemoveFeed() => SelectedFeed is not null && !Busy;

    [RelayCommand(CanExecute = nameof(CanRemoveFeed))]
    private async Task RemoveFeedAsync()
    {
        var feed = SelectedFeed!;
        if (!await _confirm("Remove feed",
                $"Remove the feed {feed.FeedId} ({feed.Vendor}) and its {feed.Count} definition(s)? Stages that already exist stay. This cannot be undone.")) return;
        try
        {
            _service.RemoveFeed(feed.FeedId);
            ImportedAny = true;
            StatusText = $"Removed the feed {feed.FeedId} and its {feed.Count} definition(s).";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            StatusText = "Not removed: " + ex.Message;
        }
        ReloadFeeds();
    }

    private void ReloadFeeds()
    {
        Feeds.Clear();
        foreach (var feed in _service.Feeds())
            Feeds.Add(new VendorFeedRow(feed.Source.FeedId, feed.Source.Vendor, feed.DefinitionFiles.Count, feed.Source.TrustState,
                feed.Source.FetchedAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm"), feed.Source.FinalAddress));
    }

    [RelayCommand]
    private void Close()
    {
        _fetching?.Cancel();
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }
}
