using System.Collections.Concurrent;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.Logging;
using ApplicationPackagerUI.Models;
using ApplicationPackagerUI.Services;
using ApplicationPackagerUI.ViewModels.Pages;

namespace ApplicationPackagerEngine.Tests.InstallerAnalysis;

/// <summary>An analysis service whose calls can be held open, fail or return fixed results.</summary>
internal sealed class FakeInstallerAnalysisService : IInstallerAnalysisService
{
    public ConcurrentQueue<string> Calls { get; } = new();
    public Dictionary<string, AnalysisReport> Reports { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, TaskCompletionSource> Gates { get; } = new(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyList<PayloadEntry> Payload { get; set; } = [];
    public IReadOnlyList<RegistryWrite> Registry { get; set; } = [];
    public IReadOnlyList<InterestingString> Strings { get; set; } = [];
    public Exception? AnalyzeFailure { get; set; }
    public Exception? PayloadFailure { get; set; }
    public TaskCompletionSource? PayloadGate { get; set; }
    public TaskCompletionSource? HashGate { get; set; }
    public TaskCompletionSource? ExtractGate { get; set; }
    public List<(string Path, PayloadEntry Entry, string Folder)> Extractions { get; } = [];
    public List<string> Progress { get; } = [];
    public Exception? ExtractFailure { get; set; }

    /// <summary>Where an extracted entry lands; the default is a real file in the folder.</summary>
    public Func<PayloadEntry, string, string> ExtractTo { get; set; } = (entry, folder) =>
    {
        var target = Path.Combine(folder, InstallerAnalysisText.EntryName(entry.Path));
        File.WriteAllText(target, "extracted " + entry.Path);
        return target;
    };

    private int Count(string kind) => Calls.Count(c => c.StartsWith(kind + ":", StringComparison.Ordinal));
    public int AnalyzeCalls => Count("analyze");
    public int PayloadCalls => Count("payload");
    public int RegistryCalls => Count("registry");
    public int StringsCalls => Count("strings");

    public static AnalysisReport Report(string path, string type = "NSIS", IReadOnlyList<string>? notes = null, EffectiveDetection? postPatch = null) => new(
        Path: path,
        FileName: Path.GetFileName(path),
        Size: 1_234_567,
        Sha256: new string('A', 64),
        Type: type,
        Architecture: "x64",
        Signature: new SignatureFact(Status: "Valid", Subject: "CN=Contoso Ltd", Issuer: "CN=Contoso CA", Thumbprint: "0123456789ABCDEF0123456789ABCDEF01234567", Timestamped: true),
        Deployment:
        [
            new("DisplayName", "Contoso Tool", "Confirmed", "NSIS script"),
            new("DisplayVersion", "1.2.3", "Predicted", "File version"),
            new("Publisher", "", "None", "")
        ],
        Modes:
        [
            new InstallMode(Name: "PerMachine", Switch: "/S /AllUsers", InstallFolder: @"C:\Program Files\Contoso", UninstallCommand: @"""C:\Program Files\Contoso\uninst.exe"" /S",
                ArpKey: @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Contoso", Hive: "HKLM", Context: "System")
            { IsDefault = true, View = "64", InstallCommand = "setup.exe /S /AllUsers" },
            new InstallMode(Name: "PerUser", Switch: "/S /CurrentUser", InstallFolder: @"%LOCALAPPDATA%\Programs\Contoso", UninstallCommand: @"""%LOCALAPPDATA%\Programs\Contoso\uninst.exe"" /S",
                ArpKey: @"HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Contoso", Hive: "HKCU", Context: "User")
            { InstallCommand = "setup.exe /S /CurrentUser" }
        ],
        Metadata: [new MetadataBlock(Title: "NSIS script", Facts: [new("Compression", "LZMA", "Confirmed", "Header"), new("Unicode", "Yes", "Confirmed", "Header")])],
        MsiProperties:
        [
            new("ProductName", "Contoso Tool", "Confirmed", "Property table"), new("ProductCode", "{11111111-2222-3333-4444-555555555555}", "Confirmed", "Property table"),
            new("ALLUSERS", "1", "Confirmed", "Property table")
        ],
        PostPatch: postPatch,
        Notes: notes ?? [])
    {
        TypeName = "Nullsoft Scriptable Install System",
        DetectionHint = @"Registry: HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Contoso DisplayVersion >= 1.2.3",
        RequestedExecutionLevel = "requireAdministrator"
    };

    public static PayloadEntry Entry(string path, long size, bool isDirectory = false, string kind = "File", string innerType = "") =>
        new(Path: path, Size: size, IsDirectory: isDirectory, Kind: kind, InnerType: innerType);

    public static RegistryWrite Write(string origin, string hive, string key, string name, string value, string valueType = "REG_SZ", string condition = "") =>
        new(Origin: origin, Hive: hive, Key: key, Name: name, Value: value, ValueType: valueType, Condition: condition);

    public static InterestingString Found(string text, string encoding, long offset, string category) =>
        new(Text: text, Encoding: encoding, Offset: offset, Category: category);

    public Task<AnalysisReport> AnalyzeAsync(string path, AnalysisLimits limits, IProgress<string> progress, CancellationToken ct) => Run("analyze", path, ct, async () =>
    {
        progress.Report("Reading the header.");
        lock (Progress) Progress.Add("Reading the header.");
        if (Gates.TryGetValue(path, out var gate)) await gate.Task.WaitAsync(ct);
        if (AnalyzeFailure is not null) throw AnalyzeFailure;
        return Reports.TryGetValue(path, out var report) ? report : Report(path);
    });

    public Task<string?> PayloadUnavailableReasonAsync(string path, AnalysisLimits limits, CancellationToken ct) =>
        Task.FromResult(PayloadFailure is SevenZipNotFoundException or PayloadNotListedException ? PayloadFailure.Message : null);

    public Task<IReadOnlyList<PayloadEntry>> ListPayloadAsync(string path, AnalysisLimits limits, CancellationToken ct) => Run("payload", path, ct, async () =>
    {
        if (PayloadGate is not null) await PayloadGate.Task.WaitAsync(ct);
        if (PayloadFailure is not null) throw PayloadFailure;
        return PayloadByPath.TryGetValue(path, out var own) ? own : Payload;
    });

    public Task<IReadOnlyList<RegistryWrite>> ReadRegistryWritesAsync(string path, AnalysisLimits limits, CancellationToken ct) =>
        Run("registry", path, ct, () => Task.FromResult(Registry));

    public IReadOnlyList<string> RegistryNotes { get; set; } = [];
    public Exception? RegistryFailure { get; set; }

    public Task<RegistryReadResult> ReadRegistryAsync(string path, AnalysisLimits limits, CancellationToken ct) => Run("registry", path, ct, () =>
        RegistryFailure is not null ? Task.FromException<RegistryReadResult>(RegistryFailure) : Task.FromResult(new RegistryReadResult(Writes: Registry, Notes: RegistryNotes)));

    public List<(string Path, IReadOnlyList<PayloadEntry>? Entries, string Target)> PayloadExtractions { get; } = [];
    public Exception? PayloadExtractFailure { get; set; }
    public TaskCompletionSource? PayloadExtractGate { get; set; }
    public IReadOnlyList<string> PayloadExtractSkipped { get; set; } = [];
    public bool PayloadExtractWritesNothing { get; set; }

    /// <summary>Creates the payload folder and one file before the gate and the failure, as a run stopped part way.</summary>
    public bool PayloadExtractLeavesPartial { get; set; }

    /// <summary>Writes each wanted file under a new '&lt;file name&gt; payload' folder of the target, as the real service does.</summary>
    public Task<PayloadExtractResult> ExtractPayloadAsync(string path, IReadOnlyList<PayloadEntry>? entries, string targetFolder, AnalysisLimits limits, CancellationToken ct) =>
        Run("extractpayload", path, ct, async () =>
        {
            lock (PayloadExtractions) PayloadExtractions.Add((path, entries, targetFolder));
            var baseName = Path.GetFileNameWithoutExtension(path);
            var folder = Path.Combine(targetFolder, baseName + " payload");
            for (var n = 2; Directory.Exists(folder); n++) folder = Path.Combine(targetFolder, $"{baseName} payload ({n})");
            if (PayloadExtractLeavesPartial)
            {
                Directory.CreateDirectory(folder);
                File.WriteAllText(Path.Combine(folder, "first.bin"), "written before the stop");
            }
            if (PayloadExtractGate is not null) await PayloadExtractGate.Task.WaitAsync(ct);
            if (PayloadExtractFailure is not null) throw PayloadExtractFailure;
            Directory.CreateDirectory(folder);
            File.Delete(Path.Combine(folder, "first.bin"));
            var files = PayloadExtractWritesNothing ? new List<PayloadEntry>() : (entries ?? Payload).Where(e => !e.IsDirectory).ToList();
            foreach (var entry in files)
            {
                var file = Path.Combine(folder, entry.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                File.WriteAllText(file, "extracted " + entry.Path);
            }
            return new PayloadExtractResult(Folder: folder, Files: files.Count, Bytes: files.Sum(f => f.Size), Skipped: PayloadExtractSkipped);
        });

    public Task<IReadOnlyList<InterestingString>> ReadStringsAsync(string path, AnalysisLimits limits, CancellationToken ct) =>
        Run("strings", path, ct, () => Task.FromResult(Strings));

    public Task<string> ExtractEntryAsync(string path, PayloadEntry entry, string sessionFolder, AnalysisLimits limits, CancellationToken ct) => Run("extract", path, ct, async () =>
    {
        if (ExtractGate is not null) await ExtractGate.Task.WaitAsync(ct);
        lock (Extractions) Extractions.Add((path, entry, sessionFolder));
        if (ExtractFailure is not null) throw ExtractFailure;
        return ExtractTo(entry, sessionFolder);
    });

    public Task<string> ComputeSha256Async(string path, CancellationToken ct) => Run("hash", path, ct, async () =>
    {
        if (HashGate is not null) await HashGate.Task.WaitAsync(ct);
        return new string('F', 64);
    });

    /// <summary>Calls held open by "kind:path" until the source completes.</summary>
    public ConcurrentDictionary<string, TaskCompletionSource> Holds { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A held call ignores the cancel and returns its result late, as a service that does not stop in time.</summary>
    public bool HoldsIgnoreCancel { get; set; }

    public Dictionary<string, IReadOnlyList<PayloadEntry>> PayloadByPath { get; } = new(StringComparer.OrdinalIgnoreCase);

    public TaskCompletionSource Hold(string kind, string path) => Holds[$"{kind}:{path}"] = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

    private async Task<T> Run<T>(string kind, string path, CancellationToken ct, Func<Task<T>> body)
    {
        Calls.Enqueue($"{kind}:{path}");
        ct.ThrowIfCancellationRequested();
        if (Holds.TryGetValue($"{kind}:{path}", out var hold))
        {
            if (HoldsIgnoreCancel) await hold.Task;
            else await hold.Task.WaitAsync(ct);
        }
        return await body();
    }
}

internal sealed class FakeAnalysisSandbox(string root) : IAnalysisSandbox
{
    public List<string> Created { get; } = [];
    public List<string> Released { get; } = [];
    public int ReleaseAllCalls { get; private set; }
    public List<TimeSpan> Cleans { get; } = [];

    public string NewSession()
    {
        var folder = Directory.CreateDirectory(Path.Combine(root, "s" + Created.Count)).FullName;
        lock (Created) Created.Add(folder);
        return folder;
    }

    public void Release(string session)
    {
        if (Directory.Exists(session)) Directory.Delete(session, true);
        lock (Released) Released.Add(session);
    }

    public void ReleaseAll() => ReleaseAllCalls++;
    public void CleanOldSessions(TimeSpan olderThan) => Cleans.Add(olderThan);
    public string Root => root;
}

internal sealed class FakeInstallerReportWriter(string reportsFolder) : IInstallerReportWriter
{
    public List<bool> JsonKeyMaterial { get; } = [];
    public List<(string Format, int Payload, int Registry)> Written { get; } = [];
    public Exception? Failure { get; set; }

    /// <summary>Blocks every write until set.</summary>
    public ManualResetEventSlim? Gate { get; set; }

    public string Summary(AnalysisReport r) => $"Summary of {r.FileName}";

    public string Json(AnalysisReport r, bool includeKeyMaterial)
    {
        JsonKeyMaterial.Add(includeKeyMaterial);
        return "{\"FileName\":\"" + r.FileName + "\"}";
    }

    public string WriteCsv(AnalysisReport r, IReadOnlyList<RegistryWrite> reg) => Write("csv", 0, reg.Count);

    public string WriteHtml(AnalysisReport r, IReadOnlyList<PayloadEntry> payload, IReadOnlyList<RegistryWrite> reg) => Write("html", payload.Count, reg.Count);

    /// <summary>The not-listed reason of each HTML write; a null payload is recorded as a payload count of -1.</summary>
    public List<string> HtmlReasons { get; } = [];

    public string WriteHtml(AnalysisReport r, IReadOnlyList<PayloadEntry>? payload, IReadOnlyList<RegistryWrite> reg, string notListedReason)
    {
        lock (HtmlReasons) HtmlReasons.Add(notListedReason);
        return Write("html", payload?.Count ?? -1, reg.Count);
    }

    public int WritesStarted;

    private string Write(string format, int payload, int registry)
    {
        Interlocked.Increment(ref WritesStarted);
        Gate?.Wait(TimeSpan.FromSeconds(10));
        if (Failure is not null) throw Failure;
        lock (Written) Written.Add((format, payload, registry));
        Directory.CreateDirectory(reportsFolder);
        return Path.Combine(reportsFolder, $"report-{Written.Count}.{format}");
    }
}

internal sealed class FakeInstallerAnalysisPrompts : IInstallerAnalysisPrompts
{
    public string? Installer { get; set; }
    public string? Folder { get; set; }
    public List<string> FolderTitles { get; } = [];
    public List<string> Copied { get; } = [];
    public List<string> OpenedFolders { get; } = [];
    public bool CopyFails { get; set; }

    public string? PickInstaller() => Installer;

    public string? PickFolder(string title)
    {
        FolderTitles.Add(title);
        return Folder;
    }

    public bool CopyText(string text)
    {
        if (CopyFails) return false;
        Copied.Add(text);
        return true;
    }

    public void OpenFolder(string folder) => OpenedFolders.Add(folder);
}

internal sealed class RecordingAnalysisLog : IAppLog
{
    public List<AppLogEntry> Entries { get; } = [];
    public string? SessionFile => null;
    public AppLogLevel MinimumLevel { get; set; }
    public event EventHandler<AppLogEntry>? Written;

    public void Write(AppLogLevel level, string area, string message)
    {
        var entry = new AppLogEntry(DateTimeOffset.Now, level, area, message);
        lock (Entries) Entries.Add(entry);
        Written?.Invoke(this, entry);
    }

    public void RegisterSecret(string value) { }

    public IReadOnlyList<AppLogEntry> Tail()
    {
        lock (Entries) return Entries.ToList();
    }
}

/// <summary>The page over fake services, a temporary folder with real installer files and a sandbox folder.</summary>
internal sealed class InstallerAnalysisHarness : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "apkg-ia-" + Guid.NewGuid().ToString("N"));
    public string Files => Path.Combine(Root, "files");
    public string SandboxRoot => Path.Combine(Root, "sandbox");
    public string Reports => Path.Combine(Root, "reports");

    public FakeInstallerAnalysisService Service { get; } = new();
    public FakeAnalysisSandbox Sandbox { get; }
    public FakeInstallerReportWriter Writer { get; }
    public FakeInstallerAnalysisPrompts Prompts { get; } = new();
    public RecordingAnalysisLog Log { get; } = new();
    public InstallerAnalysisSettings? Saved { get; set; }
    public int PreferenceReads { get; private set; }
    public int SaveCount { get; private set; }
    public AnalysisLimits Limits { get; set; } = AnalysisLimits.Default;

    /// <summary>Blocks the read of the saved choices until set.</summary>
    public ManualResetEventSlim? PreferencesGate { get; set; }

    public InstallerAnalysisHarness()
    {
        Directory.CreateDirectory(Files);
        Directory.CreateDirectory(SandboxRoot);
        Sandbox = new FakeAnalysisSandbox(SandboxRoot);
        Writer = new FakeInstallerReportWriter(Reports);
    }

    /// <summary>A real file the page can open; the fake service gives its report.</summary>
    public string Installer(string name = "setup.exe")
    {
        var path = Path.Combine(Files, name);
        File.WriteAllText(path, "MZ fake installer");
        return path;
    }

    public InstallerAnalysisViewModel Create() => new(new InstallerAnalysisPageServices(
        Service, Sandbox, Writer, Prompts,
        () =>
        {
            PreferenceReads++;
            PreferencesGate?.Wait(TimeSpan.FromSeconds(10));
            return Saved;
        },
        settings =>
        {
            SaveCount++;
            Saved = settings;
        },
        Log, Limits, () => SandboxRoot));

    public async Task<InstallerAnalysisViewModel> OpenPageAsync()
    {
        var viewModel = Create();
        await viewModel.OnNavigatedToAsync();
        return viewModel;
    }

    public static readonly PayloadEntry[] SamplePayload =
    [
        FakeInstallerAnalysisService.Entry("app", 0, isDirectory: true),
        FakeInstallerAnalysisService.Entry(@"app\contoso.exe", 2048),
        FakeInstallerAnalysisService.Entry(@"app\readme.txt", 10),
        FakeInstallerAnalysisService.Entry(@"redist\vc_redist.x64.exe", 25_000_000, kind: "InnerInstaller", innerType: "WiX Burn"),
        FakeInstallerAnalysisService.Entry("setup.msi", 4_000_000, kind: "InnerInstaller", innerType: "MSI")
    ];

    public void Dispose()
    {
        try { Directory.Delete(Root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

internal static class Wait
{
    public static async Task UntilAsync(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Waited ten seconds for " + what + ".");
            await Task.Delay(5);
        }
    }
}
