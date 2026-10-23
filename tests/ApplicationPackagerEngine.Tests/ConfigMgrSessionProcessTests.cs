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

    private static async Task<List<JsonElement>> Exchange(IConfigMgrSessionProcess process, string id, string request, TimeSpan? limit = null)
    {
        await process.WriteLineAsync(request);
        var lines = new List<JsonElement>();
        while (true)
        {
            var line = await process.ReadLineAsync().WaitAsync(limit ?? TimeSpan.FromSeconds(60));
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
    public async Task AParameterValueIsNeverRunAsScriptText()
    {
        var marker = Path.Combine(Path.GetTempPath(), "ap-cmsession-marker-" + Guid.NewGuid().ToString("N"));
        var values = new[]
        {
            "$(New-Item -ItemType File -Path '" + marker + "')",
            "'; New-Item -ItemType File -Path '" + marker + "'; '",
            "\"; New-Item -ItemType File -Path \"" + marker + "\"; #",
            "@(New-Item -ItemType File -Path '" + marker + "')"
        };
        await using var process = PowerShellSessionProcess.Start();
        var parameters = new JsonObject();
        for (var i = 0; i < values.Length; i++) parameters["V" + i] = values[i];

        var lines = await Exchange(process, "e1", Request("e1", "param($V0, $V1, $V2, $V3) $V0; $V1; $V2; $V3; \"$V0\"", parameters));

        Assert.False(File.Exists(marker));
        Assert.Equal(values.Append(values[0]), lines.Select(l => l.GetProperty("v").GetString()));
    }

    [Fact]
    public async Task AMalformedRequestGetsAnErrorReplyWithinSecondsAndTheSessionGoesOn()
    {
        await using var process = PowerShellSessionProcess.Start();
        var truncated = "{\"id\":\"m1\",\"op\":\"run\",\"name\":\"T\",\"script\":\"'x'\",\"parameters\":{\"V\":";
        var lines = await Exchange(process, "m1", truncated, TimeSpan.FromSeconds(15));
        var error = Assert.Single(lines);
        Assert.Equal("error", error.GetProperty("t").GetString());
        Assert.Equal("script", error.GetProperty("kind").GetString());
        Assert.Equal("The request could not be read.", error.GetProperty("m").GetString());

        // Valid JSON nested deeper than Windows PowerShell 5.1 reads fails the same way.
        var deep = new string('[', 150) + new string(']', 150);
        var nested = "{\"id\":\"m2\",\"op\":\"run\",\"name\":\"T\",\"script\":\"'x'\",\"parameters\":{\"V\":" + deep + "}}";
        Assert.Equal("error", Assert.Single(await Exchange(process, "m2", nested, TimeSpan.FromSeconds(15))).GetProperty("t").GetString());

        await process.WriteLineAsync("not json and no id");
        var next = await Exchange(process, "m3", Request("m3", "'still here'"));
        Assert.Equal("still here", next.Single().GetProperty("v").GetString());
    }

    [Fact]
    public async Task NonFiniteNumbersComeBackAsNullAndStringsKeepTheirText()
    {
        await using var process = PowerShellSessionProcess.Start();
        var lines = await Exchange(process, "n1", Request("n1",
            "[double]::NaN; [double]::PositiveInfinity; [single]::NegativeInfinity; 1.5; [pscustomobject]@{ A = [double]::NaN; B = [double]::NegativeInfinity; S = 'NaN, -Infinity: Infinity'; N = 2.5 }"));

        var values = lines.Select(l => l.GetProperty("v")).ToList();
        Assert.Equal(5, values.Count);
        Assert.All(values.Take(3), v => Assert.Equal(JsonValueKind.Null, v.ValueKind));
        Assert.Equal(1.5, values[3].GetDouble());
        Assert.Equal(JsonValueKind.Null, values[4].GetProperty("A").ValueKind);
        Assert.Equal(JsonValueKind.Null, values[4].GetProperty("B").ValueKind);
        Assert.Equal("NaN, -Infinity: Infinity", values[4].GetProperty("S").GetString());
        Assert.Equal(2.5, values[4].GetProperty("N").GetDouble());
    }

    [Fact]
    public async Task AOnePointFiveMegabyteParameterIsRefusedByTheHostAndTheRealSessionKeepsWorking()
    {
        var factory = new ConnectSkippingFactory();
        await using var host = new ConfigMgrSessionHost(
            () => new ConfigMgrConnection("ABC", "unused"), factory, null, new ConfigMgrSessionOptions(), () => null);
        var echo = ConfigMgrScript.FromText("Echo", "param($V) $V.Length");

        var failure = await Assert.ThrowsAsync<ConfigMgrSessionException>(() =>
            host.InvokeAsync(echo, new ConfigMgrParameters { ["V"] = new string('x', 1_500_000) }).WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.Equal(ConfigMgrFailure.RequestTooLarge, failure.Failure);

        var result = await host.InvokeAsync(echo, new ConfigMgrParameters { ["V"] = new string('x', 500_000) }).WaitAsync(TimeSpan.FromSeconds(60));
        Assert.Equal(["500000"], result.Lines);
        Assert.Single(factory.Processes);
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
    public async Task TheHostScriptFileIsGoneOnceTheSessionAnswersAndTheSessionGoesOn()
    {
        await using var process = PowerShellSessionProcess.Start();
        Assert.StartsWith(PowerShellSessionProcess.ScriptPrefix, Path.GetFileName(process.ScriptPath));

        Assert.Equal("one", (await Exchange(process, "s1", Request("s1", "'one'"))).Single().GetProperty("v").GetString());

        Assert.False(File.Exists(process.ScriptPath));
        Assert.Equal("two", (await Exchange(process, "s2", Request("s2", "'two'"))).Single().GetProperty("v").GetString());
    }

    [Fact]
    public void AStartingSessionDeletesHostScriptsLeftByEndedProcessesOnly()
    {
        var folder = Path.Combine(Path.GetTempPath(), "apkg-cmsweep-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var now = new DateTime(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc);
            string File(string name, TimeSpan age)
            {
                var path = Path.Combine(folder, name);
                System.IO.File.WriteAllText(path, "x");
                System.IO.File.SetLastWriteTimeUtc(path, now - age);
                return path;
            }
            var stale = File("ap-cmsession-old.ps1", TimeSpan.FromHours(3));
            var running = File("ap-cmsession-new.ps1", TimeSpan.FromMinutes(5));
            var other = File("other-old.ps1", TimeSpan.FromHours(3));

            Assert.Equal(1, PowerShellSessionProcess.SweepStaleScripts(folder, now));

            Assert.False(System.IO.File.Exists(stale));
            Assert.True(System.IO.File.Exists(running));
            Assert.True(System.IO.File.Exists(other));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
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
        // The command timeout also covers the start of the new PowerShell process, which takes
        // several seconds while the rest of the suite loads the machine.
        var factory = new ConnectSkippingFactory();
        await using var host = new ConfigMgrSessionHost(
            () => new ConfigMgrConnection("ABC", "unused"), factory, null,
            new ConfigMgrSessionOptions { CommandTimeout = TimeSpan.FromSeconds(10) }, () => null);

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
