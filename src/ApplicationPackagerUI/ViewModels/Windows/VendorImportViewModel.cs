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

    public bool IsShippedConflict => Candidate.Conflict == ConflictKind.Shipped && (!Candidate.Definition.Blocked || Unblocked);

    /// <summary>The only block is code that can be unblocked (scripts and commands), and the administrator has not unblocked it yet.</summary>
    public bool CanUnblock => Candidate.CanUnblock && !Unblocked;

    /// <summary>The administrator saw the full text of every script and command, with its SHA-256, and unblocked this definition (step 3).</summary>
    [ObservableProperty] private bool _unblocked;

    partial void OnUnblockedChanged(bool value)
    {
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(IsShippedConflict));
        OnPropertyChanged(nameof(CanUnblock));
        OnPropertyChanged(nameof(Details));
        if (!value) Replace = false;
    }

    /// <summary>The administrator chose to replace the shipped definition with this Id (the default is to skip it).</summary>
    [ObservableProperty] private bool _replace;

    public string Status =>
        Candidate.Definition.Blocked && !Unblocked
            ? "Blocked: " + (Candidate.Definition.CarriesCode
                ? "carries code (" + string.Join(", ", Candidate.Definition.CodeProperties) + ")" + (Candidate.CanUnblock ? "; select Unblock Selected to review it" : "; this kind of code is never unblocked")
                : Candidate.Definition.Findings.First(f => f.Severity == FindingSeverity.Blocking).Message)
            : Candidate.Conflict switch
            {
                ConflictKind.Shipped => "Same Id as a shipped definition: skipped unless replaced",
                ConflictKind.User => "Same Id as your own definition: skipped",
                ConflictKind.AnotherFeed => $"Same Id as the feed {Candidate.ConflictFeedId}: skipped",
                ConflictKind.StageFolder => "Same stage folder as another definition (Application and Architecture): skipped",
                _ => Unblocked ? "Unblocked: imports with its scripts and commands" : "Ready"
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
            if (Candidate.CanUnblock || Unblocked)
            {
                text.AppendLine();
                text.AppendLine("Scripts and commands of this definition (they run as the system account on computers):");
                text.Append(DefinitionUnblock.Describe(d));
            }
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
        ReloadAccess();
    }

    public string Title => WindowTitle;

    [ObservableProperty] private string _address = "";
    [ObservableProperty] private string _pinnedDigest = "";
    [ObservableProperty] private string _pinnedPublisher = "";
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
        UnblockSelectedCommand.NotifyCanExecuteChanged();
        BlockAgainCommand.NotifyCanExecuteChanged();
        CancelFetchCommand.NotifyCanExecuteChanged();
        RemoveFeedCommand.NotifyCanExecuteChanged();
        CheckForUpdateCommand.NotifyCanExecuteChanged();
        ApplyUpdateCommand.NotifyCanExecuteChanged();
        AddAccessCommand.NotifyCanExecuteChanged();
        RotateAccessCommand.NotifyCanExecuteChanged();
        RemoveAccessCommand.NotifyCanExecuteChanged();
    }

    partial void OnAddressChanged(string value) => FetchCommand.NotifyCanExecuteChanged();
    partial void OnVendorChanged(string value) => ImportCommand.NotifyCanExecuteChanged();
    partial void OnFeedIdChanged(string value) => ImportCommand.NotifyCanExecuteChanged();
    partial void OnConfirmedNotVerifiedChanged(bool value)
    {
        ImportCommand.NotifyCanExecuteChanged();
        ApplyUpdateCommand.NotifyCanExecuteChanged();
    }
    partial void OnSelectedFeedChanged(VendorFeedRow? value)
    {
        RemoveFeedCommand.NotifyCanExecuteChanged();
        CheckForUpdateCommand.NotifyCanExecuteChanged();
    }
    partial void OnSelectedRowChanged(VendorImportRow? value)
    {
        UnblockSelectedCommand.NotifyCanExecuteChanged();
        BlockAgainCommand.NotifyCanExecuteChanged();
    }

    private bool CanUnblockSelected() => !Busy && SelectedRow is { CanUnblock: true };

    /// <summary>
    /// Unblocks the selected definition after a confirmation that names it and shows the full text of every script and command with its SHA-256. The
    /// unblock is for this definition only and for exactly this text and these settings: an update that changes one blocks it again.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanUnblockSelected))]
    private async Task UnblockSelectedAsync()
    {
        var row = SelectedRow!;
        Busy = true;
        try
        {
            // The dialog is a message box: it has no scroll bar and it clips a long text. It shows the SHA-256 of every piece and the digest; the
            // full text is in the details of the definition above (the box that scrolls), and it is shown here only when it is short.
            var text = DefinitionUnblock.Describe(row.Candidate.Definition.Definition);
            if (text.Length > 3000)
                text = "The text of the scripts and commands is long. Read it in the details of the definition before you confirm."
                       + Environment.NewLine + DefinitionUnblock.Summary(row.Candidate.Definition.Definition);
            if (!await _confirm($"Unblock {row.Id}",
                    $"Unblock the definition {row.Id} ({row.Application})? Its scripts and commands run as the system account on the computers that install it. The import does not sign or trust them. This unblock is for this definition and for exactly this text and these settings.{Environment.NewLine}{Environment.NewLine}{text}"))
                return;
            row.Unblocked = true;
            StatusText = $"{row.Id} is unblocked for this import. Nothing is stored until you select Import.";
        }
        finally { Busy = false; }
        ImportCommand.NotifyCanExecuteChanged();
        UnblockSelectedCommand.NotifyCanExecuteChanged();
        BlockAgainCommand.NotifyCanExecuteChanged();
    }

    private bool CanBlockAgain() => !Busy && SelectedRow is { Unblocked: true };

    [RelayCommand(CanExecute = nameof(CanBlockAgain))]
    private void BlockAgain()
    {
        SelectedRow!.Unblocked = false;
        ImportCommand.NotifyCanExecuteChanged();
        UnblockSelectedCommand.NotifyCanExecuteChanged();
        BlockAgainCommand.NotifyCanExecuteChanged();
    }

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
        var fetching = new CancellationTokenSource();
        _fetching = fetching;
        Busy = true;
        StatusText = $"Fetching from {address.Host}...";
        try
        {
            var preview = await _service.PreviewAsync(address, PinnedDigest, fetching.Token, PinnedPublisher);
            Show(preview);
        }
        catch (FeedFetchException ex) { StatusText = ex.Message + " Nothing was written."; }
        catch (OperationCanceledException) { StatusText = "Cancelled. Nothing was written."; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText = "The stored definitions could not be read: " + ex.Message + " Nothing was written.";
        }
        finally
        {
            Busy = false;
            fetching.Dispose();
            if (ReferenceEquals(_fetching, fetching)) _fetching = null;
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
                     + $"Vendor in the document: {preview.Feed.Vendor ?? "none"}\nTrust state: {preview.TrustLabel}"
                     + (preview.Signer is null ? "" : $"\nSigned by: {preview.Signer}");
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
        && Rows.Any(r => r.Candidate.Importable || (r.IsShippedConflict && r.Replace) || (r.Unblocked && r.Candidate.Conflict == ConflictKind.None));

    [RelayCommand(CanExecute = nameof(CanImport))]
    private async Task ImportAsync()
    {
        var preview = _preview!;
        var replace = Rows.Where(r => r.IsShippedConflict && r.Replace).Select(r => r.Id).ToList();
        var unblock = Rows.Where(r => r.Unblocked).Select(r => r.Id).ToList();
        // Busy while the question is open, so that nothing else (a second fetch) changes the preview under it.
        Busy = true;
        try
        {
            if (replace.Count > 0
                && !await _confirm("Replace shipped definitions",
                    $"Replace the shipped definition(s) {string.Join(", ", replace)} with the imported one(s)? The imported definition has its own profiles; the Workbench profiles of the shipped application stay with the shipped one."))
                return;
            var source = _service.Import(preview, new ImportConfirmation(
                Vendor.Trim(), FeedId.Trim(), ConfirmedNotVerified, new HashSet<string>(replace, StringComparer.OrdinalIgnoreCase),
                new HashSet<string>(unblock, StringComparer.OrdinalIgnoreCase)));
            ImportedAny = true;
            StatusText = $"Imported {source.Ids.Count} definition(s) from {source.Vendor} as the feed {source.FeedId} ({source.TrustState}).";
            Address = "";
            ClearUpdate();
            ClearPreview();
            ReloadFeeds();
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            StatusText = "Not imported: " + ex.Message;
        }
        finally { Busy = false; }
    }

    // ---- update of a stored feed (step 4) ----

    private UpdatePreview? _update;

    [ObservableProperty] private string _updateText = "";
    [ObservableProperty] private string _updateTrust = "";
    [ObservableProperty] private bool _confirmedOlder;
    [ObservableProperty] private bool _confirmedUpdateNotVerified;

    public bool HasUpdate => _update is not null;
    public bool NeedsUpdateNotVerifiedConfirmation => _update?.Preview.TrustState == FeedTrust.NotVerified;
    public bool NeedsOlderConfirmation => _update?.OlderThanStored == true;

    partial void OnConfirmedOlderChanged(bool value) => ApplyUpdateCommand.NotifyCanExecuteChanged();
    partial void OnConfirmedUpdateNotVerifiedChanged(bool value) => ApplyUpdateCommand.NotifyCanExecuteChanged();

    private bool CanCheckForUpdate() => SelectedFeed is not null && !Busy;

    /// <summary>Fetches the feed again at its stored address (or at the address in the box), with the same limits and trust rules, and shows the diff. Nothing is written.</summary>
    [RelayCommand(CanExecute = nameof(CanCheckForUpdate))]
    private async Task CheckForUpdateAsync()
    {
        var feed = SelectedFeed!;
        ClearUpdate();
        Uri? address = null;
        if (Address.Trim().Length > 0)
        {
            if (!Uri.TryCreate(Address.Trim(), UriKind.Absolute, out address) || address.Scheme != Uri.UriSchemeHttps)
            {
                StatusText = "The address must be an absolute https:// address. Other schemes are refused.";
                return;
            }
        }
        var fetching = new CancellationTokenSource();
        _fetching = fetching;
        Busy = true;
        StatusText = $"Checking {feed.FeedId} for an update...";
        try
        {
            var update = await _service.PreviewUpdateAsync(feed.FeedId, address, PinnedDigest, fetching.Token, PinnedPublisher);
            _update = update;
            UpdateTrust = update.Preview.TrustLabel;
            UpdateText = DescribeUpdate(update);
            OnPropertyChanged(nameof(HasUpdate));
            OnPropertyChanged(nameof(NeedsUpdateNotVerifiedConfirmation));
            OnPropertyChanged(nameof(NeedsOlderConfirmation));
            StatusText = update.Preview.Refused
                ? "The update is refused. Nothing was written."
                : $"{update.Diffs.Count(d => d.Change != DefinitionChange.Unchanged)} change(s) found. Nothing is stored until you select Apply Update.";
        }
        catch (FeedFetchException ex) { StatusText = ex.Message + " Nothing was written."; }
        catch (OperationCanceledException) { StatusText = "Cancelled. Nothing was written."; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            StatusText = "The update could not be checked: " + ex.Message + " Nothing was written.";
        }
        finally
        {
            Busy = false;
            fetching.Dispose();
            if (ReferenceEquals(_fetching, fetching)) _fetching = null;
        }
        ApplyUpdateCommand.NotifyCanExecuteChanged();
    }

    /// <summary>The diff against the stored definitions: added and removed definitions, the changed properties, and what needs a look.</summary>
    public static string DescribeUpdate(UpdatePreview update)
    {
        var text = new StringBuilder();
        var preview = update.Preview;
        text.AppendLine($"Feed {update.Old.FeedId} ({update.Old.Vendor}); trust state of the new document: {preview.TrustLabel}");
        text.AppendLine($"Fetched from: {preview.Document.SafeAddress} (stored address: {update.Old.FinalAddress})");
        text.AppendLine($"SHA-256 of the new document: {preview.Document.Sha256}");
        if (update.PinNote is not null) text.AppendLine("[Pin] " + update.PinNote);
        if (preview.Signer is not null) text.AppendLine("Signed by: " + preview.Signer);
        if (preview.Refusal is not null) text.AppendLine("[Blocking] " + preview.Refusal);
        foreach (var finding in preview.Feed.Findings) text.AppendLine($"[{finding.Severity}] {finding.Message}");
        if (update.OlderThanStored) text.AppendLine("The document is OLDER than the stored one (its Updated is earlier).");
        foreach (var diff in update.Diffs.Where(d => d.Change != DefinitionChange.Unchanged))
        {
            var parts = new List<string>();
            if (diff.ChangedProperties.Count > 0) parts.Add("changed: " + string.Join(", ", diff.ChangedProperties));
            if (diff.HostChanged) parts.Add("ADDRESS HOST CHANGED");
            if (diff.HashSourceChanged) parts.Add("HASH SOURCE CHANGED");
            if (diff.RequirementChanged) parts.Add("HASH OR SIGNATURE REQUIREMENT CHANGED");
            if (diff.CodeChanged) parts.Add("CODE NEW OR CHANGED");
            if (diff.UnblockLost) parts.Add("UNBLOCK LOST: blocked again until you unblock the new text");
            if (diff.UnblockKept) parts.Add("unblock kept: the code and its settings are unchanged");
            if (diff.Change == DefinitionChange.Removed) parts.Add("not in the new document; it stays unless you confirm its removal");
            text.AppendLine($"{diff.Change}: {diff.Id}" + (parts.Count == 0 ? "" : " (" + string.Join("; ", parts) + ")"));
        }
        if (update.Diffs.All(d => d.Change == DefinitionChange.Unchanged)) text.AppendLine("No definition changed.");
        foreach (var reason in VendorFeedUpdater.NotApplied(update)) text.AppendLine("Not applied: " + reason);
        return text.ToString();
    }

    private bool CanApplyUpdate() =>
        !Busy && _update is { Preview.Refused: false }
        && (!NeedsUpdateNotVerifiedConfirmation || ConfirmedUpdateNotVerified) && (!NeedsOlderConfirmation || ConfirmedOlder);

    [RelayCommand(CanExecute = nameof(CanApplyUpdate))]
    private async Task ApplyUpdateAsync()
    {
        var update = _update!;
        Busy = true;
        try
        {
            var removed = update.Removed.Select(d => d.Id).ToList();
            var confirmRemoval = false;
            if (removed.Count > 0)
                confirmRemoval = await _confirm("Remove definitions",
                    $"The new document no longer has {string.Join(", ", removed)}. Remove {(removed.Count == 1 ? "it" : "them")} from the feed? Choose No to keep {(removed.Count == 1 ? "it" : "them")} as stored.");
            if (!await _confirm("Apply update",
                    $"Apply the update to the feed {update.Old.FeedId} in the state: {update.Preview.TrustLabel}? {update.Diffs.Count(d => d.Change != DefinitionChange.Unchanged)} definition(s) change."))
                return;
            var source = _service.ApplyUpdate(update, new UpdateConfirmation(
                ConfirmedUpdateNotVerified, ConfirmedOlder, new HashSet<string>(confirmRemoval ? removed : [], StringComparer.OrdinalIgnoreCase),
                new HashSet<string>(update.Old.ReplacedShippedIds ?? [], StringComparer.OrdinalIgnoreCase), new HashSet<string>(StringComparer.OrdinalIgnoreCase)));
            ImportedAny = true;
            var skipped = VendorFeedUpdater.NotApplied(update).Count;
            StatusText = $"Updated the feed {source.FeedId}: {source.Ids.Count} definition(s) ({source.TrustState})." + (skipped > 0 ? $" {skipped} definition(s) of the new document were not applied (see the list)." : "");
            Address = "";
            ClearUpdate();
            ReloadFeeds();
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            StatusText = "Not updated: " + ex.Message;
        }
        finally { Busy = false; }
    }

    private void ClearUpdate()
    {
        _update = null;
        UpdateText = "";
        UpdateTrust = "";
        ConfirmedOlder = false;
        ConfirmedUpdateNotVerified = false;
        OnPropertyChanged(nameof(HasUpdate));
        OnPropertyChanged(nameof(NeedsUpdateNotVerifiedConfirmation));
        OnPropertyChanged(nameof(NeedsOlderConfirmation));
        ApplyUpdateCommand.NotifyCanExecuteChanged();
    }

    // ---- access to an authenticated feed (step 5): a key in a header, or Basic ----

    /// <summary>One credential entry on the list: the bound host and the method. The secret is never shown.</summary>
    public sealed record AccessRow(string Id, string Host, string Method, string Detail);

    public ObservableCollection<AccessRow> Access { get; } = [];

    [ObservableProperty] private AccessRow? _selectedAccess;
    [ObservableProperty] private string _accessId = "";
    [ObservableProperty] private string _accessAddress = "";
    [ObservableProperty] private int _accessMethod;
    [ObservableProperty] private string _accessHeader = "Authorization";
    [ObservableProperty] private string _accessUser = "";
    [ObservableProperty] private string _accessSecret = "";
    [ObservableProperty] private string _accessStatus = "A secret is stored in the Windows Credential Manager, bound to one host, and shown as dots. It is never shown again after it is saved.";

    public bool AccessIsBasic => AccessMethod == 1;
    public bool AccessIsHeader => AccessMethod != 1;

    partial void OnAccessMethodChanged(int value)
    {
        OnPropertyChanged(nameof(AccessIsBasic));
        OnPropertyChanged(nameof(AccessIsHeader));
    }

    partial void OnSelectedAccessChanged(AccessRow? value)
    {
        RotateAccessCommand.NotifyCanExecuteChanged();
        RemoveAccessCommand.NotifyCanExecuteChanged();
    }

    private void ReloadAccess()
    {
        Access.Clear();
        foreach (var e in _service.Credentials())
            Access.Add(new AccessRow(e.Id, e.HostText, e.Method == FeedAuthMethod.Basic ? "Basic" : "Key in a header",
                e.Method == FeedAuthMethod.Basic ? "user " + e.UserName : "header " + e.HeaderName));
    }

    private bool CanAddAccess() => !Busy && AccessId.Trim().Length > 0 && AccessAddress.Trim().Length > 0 && AccessSecret.Length > 0;

    partial void OnAccessIdChanged(string value) => AddAccessCommand.NotifyCanExecuteChanged();
    partial void OnAccessAddressChanged(string value) => AddAccessCommand.NotifyCanExecuteChanged();
    partial void OnAccessSecretChanged(string value)
    {
        AddAccessCommand.NotifyCanExecuteChanged();
        RotateAccessCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Saves the entry and its secret, then runs Test (one request to the address). The secret is cleared from the window.</summary>
    [RelayCommand(CanExecute = nameof(CanAddAccess))]
    private async Task AddAccessAsync()
    {
        if (!Uri.TryCreate(AccessAddress.Trim(), UriKind.Absolute, out var address) || address.Scheme != Uri.UriSchemeHttps)
        {
            AccessStatus = "The address must be an absolute https:// address. HTTP is refused for every method.";
            return;
        }
        Busy = true;
        try
        {
            _service.AddCredential(AccessId.Trim(), address, AccessIsBasic ? FeedAuthMethod.Basic : FeedAuthMethod.HeaderKey,
                AccessIsBasic ? null : AccessHeader.Trim(), AccessIsBasic ? AccessUser.Trim() : null, AccessSecret);
            AccessSecret = "";
            ReloadAccess();
            AccessStatus = "Saved. Testing...";
            using var cts = new CancellationTokenSource();
            try { AccessStatus = "Saved. Test: " + await _service.TestAsync(address, cts.Token); }
            catch (FeedFetchException ex) { AccessStatus = "Saved. Test failed: " + ex.Message; }
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            AccessStatus = "Not saved: " + ex.Message;
        }
        finally { Busy = false; }
    }

    private bool CanRotateAccess() => !Busy && SelectedAccess is not null && AccessSecret.Length > 0;

    [RelayCommand(CanExecute = nameof(CanRotateAccess))]
    private void RotateAccess()
    {
        try
        {
            _service.RotateCredential(SelectedAccess!.Id, AccessSecret);
            AccessSecret = "";
            AccessStatus = $"The secret of {SelectedAccess!.Id} is replaced. The next fetch uses the new one.";
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            AccessStatus = "Not rotated: " + ex.Message;
        }
    }

    private bool CanRemoveAccess() => !Busy && SelectedAccess is not null;

    [RelayCommand(CanExecute = nameof(CanRemoveAccess))]
    private async Task RemoveAccessAsync()
    {
        var row = SelectedAccess!;
        Busy = true;
        try
        {
            if (!await _confirm("Remove access", $"Remove the entry {row.Id} for {row.Host} and its secret from the Windows Credential Manager? The imported definitions stay.")) return;
            _service.RemoveCredential(row.Id);
            AccessStatus = $"Removed the entry {row.Id}.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AccessStatus = "Not removed: " + ex.Message;
        }
        finally { Busy = false; }
        ReloadAccess();
    }

    private bool CanRemoveFeed() => SelectedFeed is not null && !Busy;

    [RelayCommand(CanExecute = nameof(CanRemoveFeed))]
    private async Task RemoveFeedAsync()
    {
        var feed = SelectedFeed!;
        Busy = true;
        try
        {
            if (!await _confirm("Remove feed",
                    $"Remove the feed {feed.FeedId} ({feed.Vendor}) and its {feed.Count} definition(s)? Stages that already exist stay. This cannot be undone.")) return;
            _service.RemoveFeed(feed.FeedId);
            ImportedAny = true;
            StatusText = $"Removed the feed {feed.FeedId} and its {feed.Count} definition(s).";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            StatusText = "Not removed: " + ex.Message;
        }
        finally { Busy = false; }
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
