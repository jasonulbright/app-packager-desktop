using System.Diagnostics;
using ApplicationPackagerEngine.Services.InstallerScripts;

namespace ApplicationPackagerEngine.Tests.InstallerAnalysis;

/// <summary>The script readers stay bounded on files far larger than any header.</summary>
public sealed class ReaderBoundsTests : IDisposable
{
    private readonly InstallerAnalysisTempFolder _temp = new();

    public void Dispose() => _temp.Dispose();

    /// <summary>A sparse file of the given length: it takes no disk space and reads as zeros.</summary>
    private string Sparse(string name, long length)
    {
        var path = _temp.PathOf(name);
        File.WriteAllBytes(path, [(byte)'M', (byte)'Z']);
        using (var p = Process.Start(new ProcessStartInfo("fsutil.exe", $"sparse setflag \"{path}\"") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!)
            p.WaitForExit(10_000);
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Write))
            stream.SetLength(length);
        return path;
    }

    [Fact]
    public void AnNsisFileOverTwoGigabytesWithoutAHeaderGivesANoteInsteadOfAnException()
    {
        var path = Sparse("huge.exe", (long)int.MaxValue + 4096);

        var table = NsisScriptReader.ReadHeaderTable(path);

        Assert.Null(table.Header);
        Assert.Equal("NSIS firstheader not found.", table.Note);
    }

    [Fact]
    public void TheWholeFileFallbackStopsAtItsLimit()
    {
        var path = Sparse("large.exe", 40L * 1024 * 1024);

        var table = NsisScriptReader.ReadHeaderTable(path, maxWholeFileBytes: 20L * 1024 * 1024);

        Assert.Null(table.Header);
        Assert.Equal("NSIS firstheader not found.", table.Note);
    }
}
