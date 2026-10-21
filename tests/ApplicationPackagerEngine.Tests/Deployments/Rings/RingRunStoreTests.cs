using System.Diagnostics;
using System.Text.Json.Nodes;
using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests.Deployments.Rings;

public sealed class RingRunStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "apkg-ringruns-" + Guid.NewGuid().ToString("N"));
    private DateTime _utc = new(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TimeZoneInfo Lab = TimeZoneInfo.CreateCustomTimeZone("Lab", TimeSpan.FromHours(-4), "Lab", "Lab");

    private string Folder => Path.Combine(_root, "runs");
    private RingRunStore Store() => new(() => Folder, utcNow: () => _utc, local: Lab, user: () => @"CONTOSO\Admin");

    public void Dispose()
    {
        try { foreach (var d in Directory.GetDirectories(_root)) if (new DirectoryInfo(d).Attributes.HasFlag(FileAttributes.ReparsePoint)) Directory.Delete(d); Directory.Delete(_root, recursive: true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    internal static RingRun NewRun(string plan = "Office Rings", DeploymentObjectType type = DeploymentObjectType.Package, int rings = 3) =>
        new(Guid.NewGuid().ToString(), "", plan, true, "LocalTime", type, type is DeploymentObjectType.Application or DeploymentObjectType.SoftwareUpdateGroup ? "16779607" : "MCM0001B",
            "CMCO-CANARY-Package", type == DeploymentObjectType.Package ? "CMCO-CANARY-Run" : "", @"CONTOSO\Admin", new DateTime(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc),
            Enumerable.Range(1, rings).Select(i => new RingRunRing(i, "R" + i, "MCM0009" + i, "DH-Test-" + i, "Required", new DateTime(2026, 10, 20 + i, 9, 0, 0), new DateTime(2026, 10, 21 + i, 9, 0, 0),
                RingStatuses.Held, null, null, 0, "", i == 1 ? 90 : null)
            { Definition = RingPlanRulesTests.Ring("R" + i, "MCM0009" + i, threshold: i == 1 ? 90 : null) }).ToList(), false);

    [Fact]
    public void ANewRunGetsTheSuiteNameShapeAndTheSuiteFields()
    {
        var run = Store().Create(NewRun(), "MCM0001B");
        Assert.Equal("Office_Rings_MCM0001B_20261020-080000.json", run.FileName);
        var json = JsonNode.Parse(File.ReadAllText(Path.Combine(Folder, run.FileName)))!.AsObject();
        Assert.Equal(1, json["SchemaVersion"]!.GetValue<int>());
        Assert.Equal("2026-10-20T08:00:00", json["CreatedAt"]!.GetValue<string>());
        Assert.Equal("Package", json["Object"]!["Type"]!.GetValue<string>());
        Assert.Equal("CMCO-CANARY-Run", json["Object"]!["ProgramName"]!.GetValue<string>());
        var ring = json["Rings"]![0]!.AsObject();
        foreach (var field in new[] { "Index", "Name", "CollectionID", "Purpose", "AvailableOffsetDays", "DeadlineOffsetDays", "UserNotification", "DPGroup", "SuccessThresholdPercent",
                     "AvailableDateTime", "DeadlineDateTime", "Status", "DeploymentID", "AssignmentID", "CreatingAt", "CreatedAt", "RemovedAt", "ShiftedMinutes" })
            Assert.True(ring.ContainsKey(field), field);
        Assert.Equal("2026-10-21T09:00:00", ring["AvailableDateTime"]!.GetValue<string>());
        Assert.Equal(run.Rings, Store().Read(run.FileName).Rings);
    }

    [Fact]
    public void NamesNeverCollideAndAClosedNameIsNeverTaken()
    {
        var store = Store();
        var first = store.Create(NewRun(), "MCM0001B");
        var second = store.Create(NewRun(), "MCM0001B");
        Assert.Equal("Office_Rings_MCM0001B_20261020-080000-2.json", second.FileName);
        store.Close(first);
        var third = store.Create(NewRun(), "MCM0001B");
        Assert.Equal("Office_Rings_MCM0001B_20261020-080000-3.json", third.FileName);
        Assert.Equal("../evil", NewRun("../evil").PlanName);
        Assert.StartsWith("___evil_", store.Create(NewRun("../evil"), "a/b").FileName, StringComparison.Ordinal);
    }

    [Fact]
    public void ClosingNeverReplacesAClosedFile()
    {
        var store = Store();
        var run = store.Create(NewRun(), "MCM0001B");
        Directory.CreateDirectory(store.ClosedFolder);
        File.WriteAllText(Path.Combine(store.ClosedFolder, run.FileName), "{ \"keep\": true }");
        var closed = store.Close(run);
        Assert.Equal("Office_Rings_MCM0001B_20261020-080000-2.json", closed.FileName);
        Assert.True(closed.IsClosed);
        Assert.Contains("keep", File.ReadAllText(Path.Combine(store.ClosedFolder, run.FileName)));
        Assert.Empty(store.List(false, out _));
        Assert.Single(store.List(true, out _));
    }

    [Fact]
    public void ASaveKeepsUnknownFieldsReplacesAtomicallyAndChangesTheFingerprint()
    {
        var store = Store();
        var run = store.Create(NewRun(), "MCM0001B");
        var path = Path.Combine(Folder, run.FileName);
        var json = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        json["Foreign"] = "kept";
        json["Rings"]![0]!["MemberCount"] = 7;
        File.WriteAllText(path, json.ToJsonString());
        var read = store.Read(run.FileName);
        Assert.NotEqual(run.Fingerprint, read.Fingerprint);
        var rings = read.Rings.ToList();
        rings[0] = rings[0] with { Status = RingStatuses.Created, DeploymentId = "MCM20040" };
        var saved = store.Save(read with { Rings = rings });
        Assert.Equal(RingStatuses.Created, saved.Rings[0].Status);
        var after = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        Assert.Equal("kept", after["Foreign"]!.GetValue<string>());
        Assert.Equal(7, after["Rings"]![0]!["MemberCount"]!.GetValue<int>());
        Assert.Equal("2026-10-20T08:00:00", after["Rings"]![0]!["CreatedAt"]!.GetValue<string>());
        Assert.Empty(Directory.GetFiles(Folder, "*.tmp"));
        Assert.NotEqual(read.Fingerprint, saved.Fingerprint);
    }

    [Fact]
    public void ALockRefusesASecondLockerAndNamesTheFileTheOwnerAndTheAge()
    {
        var store = Store();
        var run = store.Create(NewRun(), "MCM0001B");
        using (store.Lock(run.FileName))
        {
            _utc = _utc.AddMinutes(3);
            File.SetLastWriteTimeUtc(Path.Combine(Folder, run.FileName + ".lock"), _utc.AddMinutes(-3));
            var refused = Assert.Throws<DeploymentServiceException>(() => store.Lock(run.FileName));
            Assert.Contains(run.FileName + ".lock", refused.Message);
            Assert.Contains(@"CONTOSO\Admin on " + Environment.MachineName, refused.Message);
            Assert.Contains("3 minutes ago", refused.Message);
            Assert.DoesNotContain("lock ", refused.Message.Split("names")[1].Split("and was")[0], StringComparison.Ordinal);
            Assert.NotNull(store.List(false, out _).Single().Lock);
        }
        Assert.False(File.Exists(Path.Combine(Folder, run.FileName + ".lock")));
        using (store.Lock(run.FileName)) { }
    }

    [Fact]
    public void AnOldLockIsReportedStaleAndNeverRemoved()
    {
        var store = Store();
        var run = store.Create(NewRun(), "MCM0001B");
        var lockPath = Path.Combine(Folder, run.FileName + ".lock");
        File.WriteAllText(lockPath, @"CONTOSO\Other on CLIENT09 at 2026-10-20 05:00:00");
        File.SetLastWriteTimeUtc(lockPath, _utc.AddHours(-3));
        var refused = Assert.Throws<DeploymentServiceException>(() => store.Lock(run.FileName));
        Assert.Contains(@"CONTOSO\Other on CLIENT09", refused.Message);
        Assert.Contains("180 minutes ago", refused.Message);
        Assert.Contains("older than 60 minutes", refused.Message);
        Assert.Contains("delete that file by hand", refused.Message);
        Assert.True(File.Exists(lockPath));
        Assert.True(store.List(false, out _).Single().Lock!.Stale);
    }

    [Fact]
    public void ALockMadeByAnotherSessionAfterAHandDeletionIsNotRemoved()
    {
        var store = Store();
        var run = store.Create(NewRun(), "MCM0001B");
        var lockPath = Path.Combine(Folder, run.FileName + ".lock");
        var held = store.Lock(run.FileName);
        File.Delete(lockPath);
        File.WriteAllText(lockPath, "other session");
        held.Dispose();
        Assert.True(File.Exists(lockPath));
    }

    [Fact]
    public void TwoThreadsRacingForTheLockNeverBothHoldIt()
    {
        var store = Store();
        var run = store.Create(NewRun(), "MCM0001B");
        var holders = 0;
        var max = 0;
        var wins = 0;
        var refusals = 0;
        Parallel.For(0, 400, new ParallelOptions { MaxDegreeOfParallelism = 8 }, _ =>
        {
            try
            {
                using var held = Store().Lock(run.FileName);
                var now = Interlocked.Increment(ref holders);
                lock (store) max = Math.Max(max, now);
                Interlocked.Increment(ref wins);
                Thread.SpinWait(2000);
                Interlocked.Decrement(ref holders);
            }
            catch (DeploymentServiceException)
            {
                Interlocked.Increment(ref refusals);
            }
        });
        Assert.Equal(1, max);
        Assert.True(wins > 0 && refusals > 0, $"wins {wins}, refusals {refusals}");
        Assert.False(File.Exists(Path.Combine(Folder, run.FileName + ".lock")));
    }

    [Fact]
    public void ALockHeldByASecondProcessRefusesThisOneAndTheSuiteFormOfTheLockIsRead()
    {
        var store = Store();
        var run = store.Create(NewRun(), "MCM0001B");
        var runPath = Path.Combine(Folder, run.FileName);
        var signal = Path.Combine(_root, "taken");
        var release = Path.Combine(_root, "release");
        var script = Path.Combine(_root, "locker.ps1");
        File.WriteAllText(script, """
            param([string]$Path, [string]$Signal, [string]$Release)
            $lock = $Path + '.lock'
            $fs = [System.IO.File]::Open($lock, [System.IO.FileMode]::CreateNew, [System.IO.FileAccess]::Write, [System.IO.FileShare]::None)
            $bytes = [System.Text.Encoding]::UTF8.GetBytes(("{0}\{1} on {2} at {3}" -f 'CONTOSO', 'Suite', 'CM01', (Get-Date -Format 'yyyy-MM-dd HH:mm:ss')))
            $fs.Write($bytes, 0, $bytes.Length); $fs.Dispose()
            Set-Content -LiteralPath $Signal -Value 'x'
            while (-not (Test-Path -LiteralPath $Release)) { Start-Sleep -Milliseconds 50 }
            Remove-Item -LiteralPath $lock -Force
            """);
        var psi = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script, "-Path", runPath, "-Signal", signal, "-Release", release }) psi.ArgumentList.Add(a);
        using var process = Process.Start(psi)!;
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(60);
            while (!File.Exists(signal) && DateTime.UtcNow < deadline) Thread.Sleep(50);
            Assert.True(File.Exists(signal), "The second process did not take the lock.");
            var refused = Assert.Throws<DeploymentServiceException>(() => store.Lock(run.FileName));
            Assert.Contains(@"CONTOSO\Suite on CM01", refused.Message);
            File.WriteAllText(release, "x");
            Assert.True(process.WaitForExit(60_000));
            using (store.Lock(run.FileName)) { }
        }
        finally
        {
            if (!process.HasExited) process.Kill();
        }
    }

    [Fact]
    public void ARunFileOfTheSuiteToolIsReadAndForeignFilesAreSkippedAndNamed()
    {
        Directory.CreateDirectory(Folder);
        File.WriteAllText(Path.Combine(Folder, "Server-Rings_MCM00035_20260926-101500.json"), """
            {
                "SchemaVersion":  1,
                "RunId":  "9a7f53a6-6a0e-4a6e-9c1b-6b0f5d2c1e11",
                "PlanName":  "Server-Rings",
                "TimeBasedOn":  "LocalTime",
                "HoldLaterRings":  true,
                "CreatedAt":  "2026-09-26T10:15:00",
                "CreatedBy":  "CONTOSO\\labadmin",
                "ClosedAt":  null,
                "Object":  { "Type":  "SUG", "ID":  "16778893", "Name":  "202609-W11-25H2", "ProgramName":  null },
                "Rings":  [
                    { "Index":  1, "Name":  "Test", "CollectionID":  "MCM00098", "CollectionName":  "DH-Test-1", "Purpose":  "Required", "AvailableOffsetDays":  0, "DeadlineOffsetDays":  2,
                      "UserNotification":  "HideAll", "OverrideServiceWindow":  false, "RebootOutsideServiceWindow":  false, "AllowMeteredConnection":  false, "AllowBoundaryFallback":  true,
                      "AllowMicrosoftUpdate":  false, "RequirePostRebootFullScan":  true, "ShowTaskSequenceProgress":  true, "TaskSequenceAvailability":  "Clients",
                      "FastNetworkOption":  "DownloadContentFromDistributionPointAndRunLocally", "SlowNetworkOption":  "DoNotRunProgram", "RerunBehavior":  "NeverRerunDeployedProgram",
                      "DPGroup":  "", "SuccessThresholdPercent":  95, "MemberCount":  1, "AvailableDateTime":  "2026-09-26T10:15:00", "DeadlineDateTime":  "2026-09-28T10:15:00",
                      "Status":  "Created", "DeploymentID":  "{1F1E7A64-2A8C-4C59-9C8E-3D5A7B0C9E21}", "AssignmentID":  "16777503", "CreatingAt":  null, "CreatedAt":  "2026-09-26T10:15:20", "RemovedAt":  null, "ShiftedMinutes":  0 },
                    { "Index":  2, "Name":  "Prod", "CollectionID":  "MCM00099", "CollectionName":  "DH-Test-2", "Purpose":  "Required", "AvailableOffsetDays":  7, "DeadlineOffsetDays":  9,
                      "UserNotification":  "HideAll", "DPGroup":  "", "SuccessThresholdPercent":  null, "AvailableDateTime":  "2026-10-03T10:15:00", "DeadlineDateTime":  "2026-10-05T10:15:00",
                      "Status":  "Held", "DeploymentID":  null, "AssignmentID":  null, "CreatingAt":  null, "CreatedAt":  null, "RemovedAt":  null, "ShiftedMinutes":  0 }
                ]
            }
            """);
        File.WriteAllText(Path.Combine(Folder, "broken.json"), "{");
        File.WriteAllText(Path.Combine(Folder, "notarun.json"), "{ \"Name\": \"x\" }");
        File.WriteAllText(Path.Combine(Folder, "badtype.json"), "{ \"RunId\": \"x\", \"Object\": { \"Type\": \"Driver\" }, \"Rings\": [] }");
        var runs = Store().List(false, out var message);
        var run = Assert.Single(runs);
        Assert.Equal((DeploymentObjectType.SoftwareUpdateGroup, "16778893", "Server-Rings", true), (run.ObjectType, run.ObjectId, run.PlanName, run.HoldLaterRings));
        Assert.Equal(new DateTime(2026, 9, 26, 14, 15, 0, DateTimeKind.Utc), run.CreatedUtc);
        Assert.Equal([RingStatuses.Created, RingStatuses.Held], run.Rings.Select(r => r.Status));
        Assert.Equal("{1F1E7A64-2A8C-4C59-9C8E-3D5A7B0C9E21}", run.Rings[0].DeploymentId);
        Assert.Equal(95, run.Rings[0].SuccessThresholdPercent);
        Assert.Equal("HideAll", run.Rings[1].Definition!.UserNotification);
        Assert.Contains("3 run files are not listed", message);
        Assert.Contains("broken.json: the file is not valid JSON.", message);
        Assert.Contains("notarun.json: the file is not a ring run.", message);
        Assert.Contains("badtype.json: the run names an object type that is not known.", message);
    }

    [Fact]
    public void ALinkedRunsFolderIsRefused()
    {
        var target = Path.Combine(_root, "target");
        Directory.CreateDirectory(target);
        using (var mk = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{Folder}\" \"{target}\"") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!) mk.WaitForExit();
        Assert.Equal(RingRunStore.LinkMessage, Assert.Throws<DeploymentServiceException>(() => Store().List(false, out _)).Message);
        Assert.Throws<DeploymentServiceException>(() => Store().Create(NewRun(), "MCM0001B"));
        Assert.Empty(Directory.GetFiles(target));
    }

    [Fact]
    public void AnApplicationRunKeepsTheContentPackageAsTheSuiteIdAndTheCiIdForThisProduct()
    {
        var store = Store();
        var run = store.Create(NewRun(type: DeploymentObjectType.Application) with { ModelId = "16778866" }, "MCM00035");
        var json = JsonNode.Parse(File.ReadAllText(Path.Combine(Folder, run.FileName)))!.AsObject();
        Assert.Equal(("MCM00035", "16779607", "16778866"), (json["Object"]!["ID"]!.GetValue<string>(), json["Object"]!["CiId"]!.GetValue<string>(), json["Object"]!["ModelId"]!.GetValue<string>()));
        Assert.Equal(("16779607", "16778866"), (run.ObjectId, run.ModelId));
        Assert.Contains("_MCM00035_", run.FileName);
    }
}
