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
    public int ExtractFailAfter { get; set; } = int.MaxValue;

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
        path, Path.GetFileName(path), 1_234_567, new string('A', 64), type, "x64",
        new SignatureFact("Valid", "CN=Contoso Ltd", "CN=Contoso CA", "0123456789ABCDEF0123456789ABCDEF01234567", true),
        [
            new("DisplayName", "Contoso Tool", "Confirmed", "NSIS script"),
            new("DisplayVersion", "1.2.3", "Predicted", "File version"),
            new("Publisher", "", "None", "")
        ],
        [
            new("PerMachine", "/S /AllUsers", @"C:\Program Files\Contoso", @"""C:\Program Files\Contoso\uninst.exe"" /S", @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Contoso", "HKLM", "System"),
            new("PerUser", "/S /CurrentUser", @"%LOCALAPPDATA%\Programs\Contoso", @"""%LOCALAPPDATA%\Programs\Contoso\uninst.exe"" /S", @"HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Contoso", "HKCU", "User")
        ],
        [new MetadataBlock("NSIS script", [new("Compression", "LZMA", "Confirmed", "Header"), new("Unicode", "Yes", "Confirmed", "Header")])],
        [new("ProductName", "Contoso Tool", "Confirmed", "Property table"), new("ProductCode", "{11111111-2222-3333-4444-555555555555}", "Confirmed", "Property table"),
         new("ALLUSERS", "1", "Confirmed", "Property table")],
        postPatch,
        notes ?? []);

    public Task<AnalysisReport> AnalyzeAsync(string path, AnalysisLimits limits, IProgress<string> progress, CancellationToken ct) => Run("analyze", path, ct, async () =>
    {
        progress.Report("Reading the header.");
        lock (Progress) Progress.Add("Reading the header.");
        if (Gates.TryGetValue(path, out var gate)) await gate.Task.WaitAsync(ct);
        if (AnalyzeFailure is not null) throw AnalyzeFailure;
        return Reports.TryGetValue(path, out var report) ? report : Report(path);
    });

    public Task<IReadOnlyList<PayloadEntry>> ListPayloadAsync(string path, AnalysisLimits limits, CancellationToken ct) => Run("payload", path, ct, async () =>
    {
        if (PayloadGate is not null) await PayloadGate.Task.WaitAsync(ct);
        if (PayloadFailure is not null) throw PayloadFailure;
        return Payload;
    });

    public Task<IReadOnlyList<RegistryWrite>> ReadRegistryWritesAsync(string path, AnalysisLimits limits, CancellationToken ct) =>
        Run("registry", path, ct, () => Task.FromResult(Registry));

    public Task<IReadOnlyList<InterestingString>> ReadStringsAsync(string path, AnalysisLimits limits, CancellationToken ct) =>
        Run("strings", path, ct, () => Task.FromResult(Strings));

    public Task<string> ExtractEntryAsync(string path, PayloadEntry entry, string sessionFolder, AnalysisLimits limits, CancellationToken ct) => Run("extract", path, ct, async () =>
    {
        if (ExtractGate is not null) await ExtractGate.Task.WaitAsync(ct);
        int done;
        lock (Extractions)
        {
            done = Extractions.Count;
            Extractions.Add((path, entry, sessionFolder));
        }
        if (done >= ExtractFailAfter) throw new IOException("disk full at " + sessionFolder);
        return ExtractTo(entry, sessionFolder);
    });

    public Task<string> ComputeSha256Async(string path, CancellationToken ct) => Run("hash", path, ct, async () =>
    {
        if (HashGate is not null) await HashGate.Task.WaitAsync(ct);
        return new string('F', 64);
    });

    private async Task<T> Run<T>(string kind, string path, CancellationToken ct, Func<Task<T>> body)
    {
        Calls.Enqueue($"{kind}:{path}");
        ct.ThrowIfCancellationRequested();
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
        lock (Released) Released.Add(session);
        if (Directory.Exists(session)) Directory.Delete(session, true);
    }

    public void ReleaseAll() => ReleaseAllCalls++;
    public void CleanOldSessions(TimeSpan olderThan) => Cleans.Add(olderThan);
}

internal sealed class FakeInstallerReportWriter(string reportsFolder) : IInstallerReportWriter
{
    public List<bool> JsonKeyMaterial { get; } = [];
    public List<(string Format, int Payload, int Registry)> Written { get; } = [];
    public Exception? Failure { get; set; }

    public string Summary(AnalysisReport r) => $"Summary of {r.FileName}";

    public string Json(AnalysisReport r, bool includeKeyMaterial)
    {
        JsonKeyMaterial.Add(includeKeyMaterial);
        return "{\"FileName\":\"" + r.FileName + "\"}";
    }

    public string WriteCsv(AnalysisReport r, IReadOnlyList<RegistryWrite> reg) => Write("csv", 0, reg.Count);

    public string WriteHtml(AnalysisReport r, IReadOnlyList<PayloadEntry> payload, IReadOnlyList<RegistryWrite> reg) => Write("html", payload.Count, reg.Count);

    private string Write(string format, int payload, int registry)
    {
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
    public AnalysisLimits Limits { get; set; } = InstallerAnalysisViewModel.DefaultLimits;

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
        new("app", 0, true, "File", ""),
        new(@"app\contoso.exe", 2048, false, "File", ""),
        new(@"app\readme.txt", 10, false, "File", ""),
        new(@"redist\vc_redist.x64.exe", 25_000_000, false, "InnerInstaller", "WiX Burn"),
        new("setup.msi", 4_000_000, false, "InnerInstaller", "MSI")
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
