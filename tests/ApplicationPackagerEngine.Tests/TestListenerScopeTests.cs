using System.Text.RegularExpressions;
using ApplicationPackagerEngine.Tests.Compliance;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// A test that listens beyond the loopback address makes Windows Firewall ask about testhost.exe. The rule is for one program path, so
/// each new copy of the tests asks again, and the prompt covers the desktop of a lab client.
/// </summary>
public sealed class TestListenerScopeTests
{
    private static readonly (string What, Regex Pattern)[] Wide =
    [
        ("TcpListener.Create listens on all addresses", new Regex(@"TcpListener\]?\s*(\.|::)\s*Create\s*\(", RegexOptions.IgnoreCase)),
        ("a PowerShell TcpListener with no loopback address", new Regex(@"New-Object\s+[\w.]*TcpListener(?![^
]*Loopback)", RegexOptions.IgnoreCase)),
        ("a PowerShell TcpListener with an address that is not the loopback address", new Regex(@"TcpListener\]::new\s*\((?!\s*\[?(System\.Net\.)?IPAddress\]?\s*::\s*(IPv6)?Loopback)", RegexOptions.IgnoreCase)),
        ("an any-address", new Regex(@"IPAddress\s*(\.|\]::)\s*(IPv6)?Any\b")),
        ("a TcpListener with an address that is not the loopback address",
            new Regex(@"(new\s+(System\.Net\.Sockets\.)?TcpListener\s*\(|TcpListener\]::new\s*\()(?!\s*(\[?(System\.Net\.)?IPAddress\]?\s*(\.|::)\s*(IPv6)?Loopback\b))")),
        ("a listener field with an address that is not the loopback address", new Regex(@"TcpListener\s+\w+\s*=\s*new\s*\((?!\s*IPAddress\.(IPv6)?Loopback\b)")),
        ("an HttpListener prefix for all names", new Regex(@"https?://(\+|\*|0\.0\.0\.0|\[::\])[:/]")),
        ("a UDP socket bound to a port", new Regex(@"new\s+(System\.Net\.Sockets\.)?UdpClient\s*\(\s*\d")),
    ];

    [Fact]
    public void NoTestListensBeyondTheLoopbackAddress()
    {
        var root = Path.Combine(CatalogServiceTests.RepositoryRoot(), "tests");
        var self = nameof(TestListenerScopeTests) + ".cs";
        var found = new List<string>();
        var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(f => Path.GetExtension(f) is ".cs" or ".ps1" or ".psm1")
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .Where(f => Path.GetFileName(f) != self)
            .ToList();

        Assert.NotEmpty(files);
        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            found.AddRange(Wide.Where(w => w.Pattern.IsMatch(text)).Select(w => $"{Path.GetRelativePath(root, file)}: {w.What}"));
        }

        Assert.True(found.Count == 0, "A test listens beyond the loopback address:\n" + string.Join("\n", found));
    }
}
