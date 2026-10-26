using System.Diagnostics;
using System.Text;
using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests.Compliance;

public sealed class CatalogBuilderRunnerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "apkg-catalogrun-" + Guid.NewGuid().ToString("N"));

    private string Folder => Path.Combine(_root, "Catalog");

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Folder) && File.GetAttributes(Folder).HasFlag(FileAttributes.ReparsePoint)) Directory.Delete(Folder);
            if (Directory.Exists(Folder))
            {
                foreach (var path in Directory.EnumerateFileSystemEntries(Folder))
                {
                    if (!File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint)) continue;
                    if (Directory.Exists(path)) Directory.Delete(path);
                    else File.Delete(path);
                }
            }
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch (IOException) { }
    }

    private const string EchoBuilder = """
        param([string]$Name, [string]$Other, [switch]$Flag)
        foreach ($v in @($Name, $Other)) { [Console]::Out.WriteLine('V:' + [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes([string]$v))) }
        [Console]::Out.WriteLine('F:' + [bool]$Flag)
        [Console]::Out.WriteLine('L:' + (Get-Location).Path)
        """;

    internal static (CatalogEntry Entry, byte[] Bytes) Builder(string text, params CatalogParameter[] parameters)
    {
        var bytes = new UTF8Encoding(false).GetBytes(text);
        var entry = new CatalogEntry("test-entry", "1.0.0", "Test", "Compliance", "Draft", "", "Warning", [], [], [], [], [], false, [],
            parameters.Length > 0 ? parameters : [new("Name", "String", false, null, ""), new("Other", "String", false, null, ""), new("Flag", "Switch", false, "False", "")],
            [], ["CI"], ["CB"], CatalogService.BuilderPrefix + "New-TestBaseline.ps1", CatalogService.Sha256(bytes));
        return (entry, bytes);
    }

    private static List<string> Decoded(CatalogBuilderRun run) =>
        run.Output.Where(l => l.StartsWith("V:", StringComparison.Ordinal)).Select(l => Encoding.UTF8.GetString(Convert.FromBase64String(l[2..]))).ToList();

    public static TheoryData<string> AcceptedHostileValues => new()
    {
        "a;b", "it's", "$(Get-Date)", "`n", @"C:\path\", @"C:\path\\", "x & y", "%PATH%", "a b  c", "@(1)", "a|b", "^caret", "Café ü", "x'; Remove-Item C:\\ -WhatIf; '",
        "{0}", "a-b", "<>", new string('x', ComplianceRules.MaxParameterLength)
    };

    [Theory]
    [MemberData(nameof(AcceptedHostileValues))]
    public async Task AnAcceptedValueReachesTheBuilderExactlyAsTyped(string value)
    {
        var (entry, bytes) = Builder(EchoBuilder);
        var (arguments, errors) = CatalogArguments.Build(entry, new Dictionary<string, string> { ["Name"] = value, ["Other"] = "second", ["Flag"] = "True" });
        Assert.Empty(errors);

        var run = await new CatalogBuilderRunner(Folder).RunAsync(entry, bytes, arguments, _ => { }, TimeSpan.FromMinutes(1), CancellationToken.None);

        Assert.Equal(0, run.ExitCode);
        Assert.Equal([value, "second"], Decoded(run));
        Assert.Contains("F:True", run.Output);
    }

    public static TheoryData<string, string> RefusedValues => new()
    {
        { "-Flag", "starts with a dash" },
        { "\u2013Flag", "starts with a dash" },
        { "a\"b", "double quote" },
        { "line\nbreak", "control character" },
        { "tab\there", "control character" },
        { " padded", "space" },
        { new string('x', ComplianceRules.MaxParameterLength + 1), "longer than" }
    };

    [Theory]
    [MemberData(nameof(RefusedValues))]
    public void AValueThatCannotTravelIntactIsRefusedWithAPlainReason(string value, string reason)
    {
        var (entry, _) = Builder(EchoBuilder);
        var (arguments, errors) = CatalogArguments.Build(entry, new Dictionary<string, string> { ["Name"] = value });

        Assert.Empty(arguments);
        Assert.Contains(errors, e => e.Contains(reason, StringComparison.Ordinal) && e.StartsWith("Name", StringComparison.Ordinal));
    }

    [Fact]
    public void ABrokenSurrogateIsRefused()
    {
        var (entry, _) = Builder(EchoBuilder);
        Assert.Contains(CatalogArguments.Build(entry, new Dictionary<string, string> { ["Name"] = "a" + '\ud800' }).Errors, e => e.Contains("control character", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("All*")]
    [InlineData("Pilot?")]
    [InlineData("[Lab] Devices")]
    public void ACollectionNameWithAWildcardIsRefused(string value)
    {
        var entry = new CatalogService().Find("llmnr-disable")!;
        var (_, errors) = CatalogArguments.Build(entry, new Dictionary<string, string> { ["SiteCode"] = "MCM", ["SiteServer"] = "cm01.contoso.com", ["CollectionName"] = value });

        Assert.Contains(errors, e => e.Contains("wildcards", StringComparison.Ordinal));
    }

    [Fact]
    public void TheArgumentsFollowTheParameterOrderAndDetectOnlyIsOnUnlessTurnedOff()
    {
        var entry = new CatalogService().Find("specexec-mitigations")!;
        var values = new Dictionary<string, string> { ["SiteServer"] = "cm01.contoso.com", ["SiteCode"] = "MCM", ["HtDisabledCollectionName"] = "CCM Test 2" };

        var (arguments, errors) = CatalogArguments.Build(entry, values);

        Assert.Empty(errors);
        Assert.Equal(["-SiteCode", "MCM", "-SiteServer", "cm01.contoso.com", "-HtEnabledCollectionName", "Devices: Hyperthreading Enabled",
            "-HtDisabledCollectionName", "CCM Test 2", "-DetectOnly"], arguments);

        values["DetectOnly"] = "False";
        values["SkipDeploy"] = "True";
        Assert.Equal(["-SiteCode", "MCM", "-SiteServer", "cm01.contoso.com", "-HtEnabledCollectionName", "Devices: Hyperthreading Enabled",
            "-HtDisabledCollectionName", "CCM Test 2", "-SkipDeploy"], CatalogArguments.Build(entry, values).Arguments);
    }

    [Theory]
    [InlineData("SiteCode", "MC", "three-character")]
    [InlineData("SiteServer", "cm01 contoso", "computer name")]
    [InlineData("DetectOnly", "maybe", "on or off")]
    [InlineData("Unknown", "x", "no parameter named")]
    public void SiteValuesSwitchesAndUnknownNamesAreChecked(string name, string value, string reason)
    {
        var entry = new CatalogService().Find("llmnr-disable")!;
        var values = new Dictionary<string, string> { ["SiteCode"] = "MCM", ["SiteServer"] = "cm01.contoso.com", [name] = value };

        Assert.Contains(CatalogArguments.Build(entry, values).Errors, e => e.Contains(reason, StringComparison.Ordinal));
    }

    [Fact]
    public void ARequiredValueThatIsMissingIsNamed()
    {
        var entry = new CatalogService().Find("llmnr-disable")!;

        Assert.Equal(["Enter a value for SiteCode.", "Enter a value for SiteServer."], CatalogArguments.Build(entry, new Dictionary<string, string>()).Errors);
    }

    [Fact]
    public async Task AnExtractedBuilderThatNoLongerMatchesTheHashIsNotStarted()
    {
        var (entry, bytes) = Builder("New-Item -ItemType File -Path $env:TEMP\\never.txt");
        var started = false;
        var runner = new CatalogBuilderRunner(Folder)
        {
            Extracted = path => File.AppendAllText(path, "\r\n# changed"),
            Started = _ => started = true
        };

        var error = await Assert.ThrowsAsync<CatalogBuilderRefusedException>(() =>
            runner.RunAsync(entry, bytes, [], _ => { }, TimeSpan.FromMinutes(1), CancellationToken.None));

        Assert.Equal(CatalogBuilderRunner.HashMessage, error.Message);
        Assert.False(started);
        Assert.Empty(Directory.GetDirectories(Folder));
    }

    [Fact]
    public async Task BytesThatDoNotMatchTheCatalogAreRefusedBeforeAnythingIsWritten()
    {
        var (entry, _) = Builder("Write-Output 1");

        var error = await Assert.ThrowsAsync<CatalogBuilderRefusedException>(() =>
            new CatalogBuilderRunner(Folder).RunAsync(entry, Encoding.UTF8.GetBytes("Write-Output 2"), [], _ => { }, TimeSpan.FromMinutes(1), CancellationToken.None));

        Assert.Equal(CatalogBuilderRunner.HashMessage, error.Message);
        Assert.False(Directory.Exists(Folder));
    }

    [Fact]
    public async Task ARunFolderThatIsAJunctionIsRefused()
    {
        var target = Path.Combine(_root, "elsewhere");
        Directory.CreateDirectory(target);
        Junction(Folder, target);
        var (entry, bytes) = Builder("Write-Output 1");

        var error = await Assert.ThrowsAsync<CatalogBuilderRefusedException>(() =>
            new CatalogBuilderRunner(Folder).RunAsync(entry, bytes, [], _ => { }, TimeSpan.FromMinutes(1), CancellationToken.None));

        Assert.Equal(CatalogBuilderRunner.LinkMessage, error.Message);
        Assert.Empty(Directory.GetFileSystemEntries(target));
    }

    [Fact]
    public async Task TheRunFolderIsRemovedAndAThrowGivesExitCodeOne()
    {
        var (entry, bytes) = Builder("Write-Host 'Creating Configuration Item: X'; throw 'stop'");
        var lines = new List<string>();

        var run = await new CatalogBuilderRunner(Folder).RunAsync(entry, bytes, [], lines.Add, TimeSpan.FromMinutes(1), CancellationToken.None);

        Assert.Equal(1, run.ExitCode);
        Assert.Contains("Creating Configuration Item: X", lines);
        Assert.Empty(Directory.GetDirectories(Folder));
    }

    [Fact]
    public async Task RunFoldersLeftByAnEarlierRunAreRemovedAfterADayWithoutFollowingAJunction()
    {
        Directory.CreateDirectory(Folder);
        string Left(int days)
        {
            var folder = Path.Combine(Folder, CatalogBuilderRunner.RunFolderPrefix + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "New-Old.ps1"), "1");
            Directory.SetCreationTimeUtc(folder, DateTime.UtcNow.AddDays(-days));
            return folder;
        }
        var old = Left(2);
        var recent = Left(0);
        var other = Path.Combine(Folder, "run-keep");
        Directory.CreateDirectory(other);
        Directory.SetCreationTimeUtc(other, DateTime.UtcNow.AddDays(-5));
        var target = Path.Combine(_root, "elsewhere");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "keep.txt"), "x");
        var linked = Path.Combine(Folder, CatalogBuilderRunner.RunFolderPrefix + Guid.NewGuid().ToString("N"));
        Junction(linked, target);
        var (entry, bytes) = Builder("Write-Output 1");

        var run = await new CatalogBuilderRunner(Folder).RunAsync(entry, bytes, [], _ => { }, TimeSpan.FromMinutes(1), CancellationToken.None);

        Assert.Equal(0, run.ExitCode);
        Assert.False(Directory.Exists(old));
        Assert.True(Directory.Exists(recent));
        Assert.True(Directory.Exists(other));
        Assert.True(Directory.Exists(linked));
        Assert.True(File.Exists(Path.Combine(target, "keep.txt")));
    }

    private const string TreeBuilder = """
        $child = Start-Process -FilePath powershell.exe -ArgumentList '-NoProfile','-Command','Start-Sleep -Seconds 120' -PassThru -WindowStyle Hidden
        [Console]::Out.WriteLine('CHILD:' + $child.Id)
        Start-Sleep -Seconds 120
        """;

    [Fact]
    public async Task TheTimeoutEndsTheWholeProcessTree()
    {
        var (entry, bytes) = Builder(TreeBuilder);
        var child = 0;
        var runner = new CatalogBuilderRunner(Folder);

        var run = await runner.RunAsync(entry, bytes, [], l => { if (l.StartsWith("CHILD:", StringComparison.Ordinal)) child = int.Parse(l[6..]); }, TimeSpan.FromSeconds(8), CancellationToken.None);

        Assert.True(run.TimedOut);
        Assert.False(run.Cancelled);
        Assert.Null(run.ExitCode);
        Assert.NotEqual(0, child);
        AssertGone(child);
        Assert.Empty(Directory.GetDirectories(Folder));
    }

    [Fact]
    public async Task CancelEndsTheWholeProcessTree()
    {
        var (entry, bytes) = Builder(TreeBuilder);
        using var cts = new CancellationTokenSource();
        var child = 0;

        var run = await new CatalogBuilderRunner(Folder).RunAsync(entry, bytes, [], l =>
        {
            if (!l.StartsWith("CHILD:", StringComparison.Ordinal)) return;
            child = int.Parse(l[6..]);
            cts.Cancel();
        }, TimeSpan.FromMinutes(2), cts.Token);

        Assert.True(run.Cancelled);
        Assert.False(run.TimedOut);
        AssertGone(child);
    }

    [Fact]
    public async Task ARunWithoutContainmentIsStoppedBeforeTheScriptRuns()
    {
        var (entry, bytes) = Builder("Start-Sleep -Seconds 30");
        var runner = new CatalogBuilderRunner(Folder) { AssignJob = _ => null };

        var error = await Assert.ThrowsAsync<CatalogBuilderRefusedException>(() =>
            runner.RunAsync(entry, bytes, [], _ => { }, TimeSpan.FromMinutes(1), CancellationToken.None));

        Assert.Equal(CatalogBuilderRunner.ContainmentMessage, error.Message);
    }

    private static void AssertGone(int processId)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var p = Process.GetProcessById(processId);
                if (p.HasExited) return;
            }
            catch (ArgumentException)
            {
                return;
            }
            Thread.Sleep(200);
        }
        Assert.Fail($"Process {processId} still runs.");
    }

    private static void Junction(string link, string target)
    {
        var psi = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        using var proc = Process.Start(psi)!;
        proc.WaitForExit(30_000);
        Assert.True(File.GetAttributes(link).HasFlag(FileAttributes.ReparsePoint), "the junction was not created");
    }
}
