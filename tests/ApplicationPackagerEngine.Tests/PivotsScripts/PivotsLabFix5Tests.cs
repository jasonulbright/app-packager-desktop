using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerUI.ViewModels.Pages;

namespace ApplicationPackagerEngine.Tests.PivotsScripts;

/// <summary>The hashes of a session file of an earlier version leave the disk on the first read, and the Copy as PowerShell text gives one object per row.</summary>
public sealed class PivotsLabFix5Tests
{
    private static string Sha256Hex(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private sealed class LegacyFolder : IDisposable
    {
        public string Folder { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "apd-pivots-legacy-" + Guid.NewGuid().ToString("N"));
        public PivotsSessionStore Store { get; }
        public string Id { get; } = PivotsSessionStore.NewId();
        public string SessionPath => System.IO.Path.Combine(Folder, Id + ".json");
        public DateTime WrittenUtc { get; }

        public LegacyFolder()
        {
            Store = new PivotsSessionStore(folder: Folder);
            Store.Save(new PivotsSessionRecord(Id: Id, CreatedUtc: DateTime.UtcNow, User: "u", Site: "MCM", Provider: "cm01.lab.local", Kind: PivotsChangeKind.RunScript,
                TargetDescription: "Device CLIENT01", CollectionId: null, Devices: [new ResolvedDevice(16777221, "CLIENT01")], QueryText: null, ScriptGuid: "4DF43201-1896-4A7C-BF3F-D518072C2B39",
                ScriptName: "x", ScriptHash: "abc", ParameterNames: ["Count", "Label"]) { Status = "Completed", OperationId = "16784634" });
            var json = File.ReadAllText(SessionPath);
            var start = json.IndexOf("\"ParameterNames\":", StringComparison.Ordinal);
            Assert.True(start >= 0, json);
            var end = json.IndexOf(']', start) + 1;
            File.WriteAllText(SessionPath, json[..start] + "\"Parameters\":{\"Count\":\"sha256:" + Sha256Hex("17") + "\",\"Label\":\"sha256:" + Sha256Hex("PSL-Value-8842") + "\"}" + json[end..]);
            WrittenUtc = new DateTime(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(SessionPath, WrittenUtc);
            Assert.Contains("sha256:", File.ReadAllText(SessionPath), StringComparison.Ordinal);
        }

        public void Dispose()
        {
            try
            {
                if (File.Exists(SessionPath)) File.SetAttributes(SessionPath, FileAttributes.Normal);
                Directory.Delete(Folder, recursive: true);
            }
            catch (IOException) { }
        }
    }

    private static void AssertScrubbed(LegacyFolder f)
    {
        var text = File.ReadAllText(f.SessionPath);
        Assert.DoesNotContain("sha256:", text, StringComparison.Ordinal);
        Assert.DoesNotContain(Sha256Hex("17"), text, StringComparison.Ordinal);
        Assert.DoesNotContain("\"Parameters\"", text, StringComparison.Ordinal);
        Assert.Contains("\"ParameterNames\":[\"Count\",\"Label\"]", text, StringComparison.Ordinal);
        Assert.Equal(f.WrittenUtc, File.GetLastWriteTimeUtc(f.SessionPath));
        Assert.Empty(Directory.GetFiles(f.Folder, "*.tmp"));
    }

    [Fact]
    public void AListOfTheSessionsRewritesASessionFileOfAnEarlierVersionWithoutItsHashes()
    {
        using var f = new LegacyFolder();
        var summary = Assert.Single(f.Store.List(10));
        Assert.Equal(f.Id, summary.Id);
        Assert.Equal("Completed", summary.Status);
        AssertScrubbed(f);
        Assert.Equal(["Count", "Label"], f.Store.Read(f.Id)!.ParameterNames);
    }

    [Fact]
    public void AReadOfASessionFileOfAnEarlierVersionRewritesItWithoutItsHashes()
    {
        using var f = new LegacyFolder();
        var record = f.Store.Read(f.Id);
        Assert.NotNull(record);
        Assert.Equal(["Count", "Label"], record.ParameterNames);
        Assert.Null(record.LegacyParameters);
        AssertScrubbed(f);
        Assert.Equal("16784634", f.Store.FindByOperation("16784634", 10)!.OperationId);
        AssertScrubbed(f);
    }

    [Fact]
    public void ASessionFileThatCannotBeRewrittenIsStillReadWithItsNames()
    {
        using var f = new LegacyFolder();
        File.SetAttributes(f.SessionPath, FileAttributes.ReadOnly);
        var record = f.Store.Read(f.Id);
        Assert.NotNull(record);
        Assert.Equal(["Count", "Label"], record.ParameterNames);
        Assert.Equal(f.Id, Assert.Single(f.Store.List(10)).Id);
        Assert.Contains("sha256:", File.ReadAllText(f.SessionPath), StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(f.Folder, "*.tmp"));
    }

    // ---- Copy as PowerShell in Windows PowerShell 5.1 and PowerShell 7 --------------------------------

    private static readonly string WindowsPowerShell = Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");

    private static string? PowerShell7()
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"PowerShell\7\pwsh.exe");
        return File.Exists(path) ? path : null;
    }

    private static string RunSnippet(string shell, string script)
    {
        var start = new ProcessStartInfo(shell) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) }) start.ArgumentList.Add(a);
        using var process = Process.Start(start)!;
        var error = process.StandardError.ReadToEndAsync();
        var output = process.StandardOutput.ReadToEnd();
        Assert.True(process.WaitForExit(60_000), $"{shell} did not end.");
        Assert.True(process.ExitCode == 0, $"{shell} exit {process.ExitCode}: {error.Result}");
        return output.Trim();
    }

    [Fact]
    public void TheCopyAsPowerShellTextGivesOneObjectPerRowInWindowsPowerShellAndPowerShell7()
    {
        var two = PivotsScriptsText.CopyAsPowerShell(["Device", "Count"], [["WKS-001", "1"], ["WKS-002", "2"]]);
        var one = PivotsScriptsText.CopyAsPowerShell(["Device", "Count"], [["WKS-001", "1"]]);
        Assert.EndsWith(" | ConvertFrom-Json | ForEach-Object { $_ }", two, StringComparison.Ordinal);
        var check = $$"""
            $first = {{two}} | Select-Object -First 1
            $where = @({{two}} | Where-Object { $_.Count -eq '2' })
            $all = @({{two}})
            $single = @({{one}})
            "first=$($first.GetType().Name):$($first.Device) where=$($where.Count):$($where[0].Device) all=$($all.Count) single=$($single.Count):$($single[0].Device)"
            """;
        const string expected = "first=PSCustomObject:WKS-001 where=1:WKS-002 all=2 single=1:WKS-001";
        var shells = new[] { File.Exists(WindowsPowerShell) ? WindowsPowerShell : null, PowerShell7() }.OfType<string>().ToList();
        foreach (var shell in shells) Assert.Equal(expected, RunSnippet(shell, check));
    }
}
