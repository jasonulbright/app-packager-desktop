using System.Text.Json;
using System.Text.Json.Nodes;
using ApplicationPackagerEngine.Services.ConfigMgrSession;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// The embedded host script in a real powershell.exe, through the run request only: a
/// connect would reach a site, so the CMSite part is covered by the lab check.
/// </summary>
public sealed class ConfigMgrSessionProcessTests
{
    private static string Request(string id, string script, JsonObject? parameters = null) => new JsonObject
    {
        ["id"] = id,
        ["op"] = "run",
        ["name"] = "T",
        ["script"] = script,
        ["parameters"] = parameters ?? new JsonObject()
    }.ToJsonString();

    private static async Task<List<JsonElement>> Exchange(IConfigMgrSessionProcess process, string id, string request)
    {
        await process.WriteLineAsync(request);
        var lines = new List<JsonElement>();
        while (true)
        {
            var line = await process.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(60));
            Assert.NotNull(line);
            var start = line!.IndexOf("{\"id\":\"" + id + "\"", StringComparison.Ordinal);
            if (start < 0) continue;
            var element = JsonDocument.Parse(line[start..]).RootElement.Clone();
            if (element.GetProperty("t").GetString() == "end") return lines;
            lines.Add(element);
        }
    }

    [Fact]
    public async Task ParametersArriveAsValuesAndOutputComesBackAsJsonLines()
    {
        await using var process = PowerShellSessionProcess.Start();
        var hostile = "O'Brien \"$(Remove-Item C:\\nothing)\" `n ‘quoted’ Ü 漢字";
        var lines = await Exchange(process, "a1", Request("a1",
            "param($Name, $Count, $Flag, $Items) $Name; $Count + 1; $Flag; $Items.Count; [pscustomobject]@{ Upper = $Name.ToUpperInvariant(); N = 3 }",
            new JsonObject { ["Name"] = hostile, ["Count"] = 41, ["Flag"] = true, ["Items"] = new JsonArray("a", "b") }));

        var values = lines.Where(l => l.GetProperty("t").GetString() == "out").Select(l => l.GetProperty("v")).ToList();
        Assert.Equal(hostile, values[0].GetString());
        Assert.Equal(42, values[1].GetInt32());
        Assert.True(values[2].GetBoolean());
        Assert.Equal(2, values[3].GetInt32());
        Assert.Equal(hostile.ToUpperInvariant(), values[4].GetProperty("Upper").GetString());
        Assert.Equal(3, values[4].GetProperty("N").GetInt32());
    }

    [Fact]
    public async Task HostTextIsKeptOffTheChannelAndWarningsAndErrorsAreTyped()
    {
        await using var process = PowerShellSessionProcess.Start();
        var lines = await Exchange(process, "b1", Request("b1",
            "Write-Host 'host text' -NoNewline; Write-Warning 'careful'; 'value'; throw 'broken thing'"));
        Assert.Equal(["warn", "out", "error"], lines.Select(l => l.GetProperty("t").GetString()));
        Assert.Equal("careful", lines[0].GetProperty("m").GetString());
        Assert.Equal("script", lines[2].GetProperty("kind").GetString());
        Assert.Equal("broken thing", lines[2].GetProperty("m").GetString());

        // The session survives a failed command.
        var next = await Exchange(process, "b2", Request("b2", "'still here'"));
        Assert.Equal("still here", next.Single().GetProperty("v").GetString());
    }

    [Fact]
    public async Task AnAccessDeniedFailureIsClassified()
    {
        await using var process = PowerShellSessionProcess.Start();
        var lines = await Exchange(process, "c1", Request("c1", "throw (New-Object System.UnauthorizedAccessException 'no')"));
        Assert.Equal("access", lines.Single().GetProperty("kind").GetString());
    }

    [Fact]
    public async Task KillEndsALongCommandAndTheOutputEnds()
    {
        var process = PowerShellSessionProcess.Start();
        try
        {
            await process.WriteLineAsync(Request("d1", "Start-Sleep -Seconds 60"));
            await Task.Delay(500);
            process.Kill();
            Assert.True(process.HasExited);
            string? line;
            do { line = await process.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)); } while (line is not null);
        }
        finally { await process.DisposeAsync(); }
    }

    [Fact]
    public async Task TheSessionHostTimesOutAndRestartsARealProcess()
    {
        // No connect is possible without a site, so the host is driven through a factory
        // that skips the connect by answering it in the test.
        var factory = new ConnectSkippingFactory();
        await using var host = new ConfigMgrSessionHost(
            () => new ConfigMgrConnection("ABC", "unused"), factory, null,
            new ConfigMgrSessionOptions { CommandTimeout = TimeSpan.FromSeconds(2) }, () => null);

        var failure = await Assert.ThrowsAsync<ConfigMgrSessionException>(() => host.InvokeAsync(ConfigMgrScript.FromText("Sleep", "Start-Sleep -Seconds 60")));
        Assert.Equal(ConfigMgrFailure.Timeout, failure.Failure);
        Assert.True(factory.Processes[0].Inner.HasExited);

        var result = await host.InvokeAsync(ConfigMgrScript.FromText("Echo", "param($V) $V"), new ConfigMgrParameters { ["V"] = "after restart" });
        Assert.Equal(["after restart"], result.Lines);
        Assert.Equal(2, factory.Processes.Count);
    }

    private sealed class ConnectSkippingFactory : IConfigMgrSessionProcessFactory
    {
        public List<ConnectSkippingProcess> Processes { get; } = [];

        public IConfigMgrSessionProcess Start()
        {
            var process = new ConnectSkippingProcess(PowerShellSessionProcess.Start());
            Processes.Add(process);
            return process;
        }
    }

    private sealed class ConnectSkippingProcess(PowerShellSessionProcess inner) : IConfigMgrSessionProcess
    {
        private readonly Queue<string> _pending = new();
        public PowerShellSessionProcess Inner { get; } = inner;
        public bool HasExited => Inner.HasExited;

        public Task WriteLineAsync(string line)
        {
            var request = JsonDocument.Parse(line).RootElement;
            if (request.GetProperty("op").GetString() == "connect")
            {
                _pending.Enqueue("{\"id\":\"" + request.GetProperty("id").GetString() + "\",\"t\":\"end\"}");
                return Task.CompletedTask;
            }
            return Inner.WriteLineAsync(line);
        }

        public Task<string?> ReadLineAsync() =>
            _pending.Count > 0 ? Task.FromResult<string?>(_pending.Dequeue()) : Inner.ReadLineAsync();

        public void Kill() => Inner.Kill();
        public ValueTask DisposeAsync() => Inner.DisposeAsync();
    }
}
