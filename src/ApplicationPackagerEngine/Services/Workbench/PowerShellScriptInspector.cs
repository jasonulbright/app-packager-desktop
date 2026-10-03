using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace ApplicationPackagerEngine.Services.Workbench;

/// <summary>One parse error: 1-based line and column, and the parser's message.</summary>
public sealed record ScriptParseError(int Line, int Column, string Message)
{
    public override string ToString() => $"Line {Line}, column {Column}: {Message}";
}

/// <summary>What the parser found in one script: its errors, whether it writes to STDOUT, or why it could not run.</summary>
public sealed record ScriptInspection(IReadOnlyList<ScriptParseError> Errors, bool WritesOutput, string? Failure = null)
{
    public static readonly ScriptInspection Empty = new([], false);

    /// <summary>The diagnostic lines the editors list: the parser failure, else one line per error.</summary>
    public IReadOnlyList<string> Diagnostics =>
        Failure is not null ? ["Parser failure: " + Failure] : Errors.Select(e => e.ToString()).ToList();
}

/// <summary>
/// Parses a PowerShell script with Windows PowerShell 5.1, the host that runs install,
/// uninstall and detection scripts on endpoints, so a construct only PowerShell 7 accepts
/// is reported. The script text travels through a temporary file, never the command line,
/// and only <c>[System.Management.Automation.Language.Parser]::ParseInput</c> reads it:
/// nothing in the script is executed.
/// </summary>
public static class PowerShellScriptInspector
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private const string InspectorTemplate = """
        $ErrorActionPreference = 'Stop'
        [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)
        $text = [System.IO.File]::ReadAllText('__PATH__', [System.Text.Encoding]::UTF8)
        $tokens = $null
        $errors = $null
        $ast = $null
        $failure = ''
        try { $ast = [System.Management.Automation.Language.Parser]::ParseInput($text, [ref]$tokens, [ref]$errors) }
        catch { $failure = $_.Exception.Message }
        $list = New-Object System.Collections.ArrayList
        foreach ($err in @($errors)) {
            if ($null -eq $err) { continue }
            [void]$list.Add(@{ Line = $err.Extent.StartLineNumber; Column = $err.Extent.StartColumnNumber; Message = [string]$err.Message })
        }
        $writes = $false
        if ($null -ne $ast) {
            $writers = $ast.FindAll({
                param($n)
                ($n -is [System.Management.Automation.Language.CommandAst]) -and
                (@('write-output', 'write-host', 'echo') -contains ([string]$n.GetCommandName()).ToLowerInvariant())
            }, $true)
            if (@($writers).Count -gt 0) { $writes = $true }
            else {
                $pipelines = $ast.FindAll({
                    param($n)
                    ($n -is [System.Management.Automation.Language.PipelineAst]) -and
                    ($n.Parent -is [System.Management.Automation.Language.NamedBlockAst])
                }, $true)
                foreach ($p in @($pipelines)) {
                    if ($p.PipelineElements.Count -eq 1 -and $p.PipelineElements[0] -is [System.Management.Automation.Language.CommandExpressionAst]) { $writes = $true }
                }
            }
        }
        $result = @{ Errors = $list.ToArray(); WritesOutput = $writes; Failure = $failure }
        [Console]::Out.Write((ConvertTo-Json -InputObject $result -Compress -Depth 4))
        """;

    /// <summary>The command that inspects the file at <paramref name="path"/>; the path is its only variable part.</summary>
    public static string InspectorCommand(string path) => InspectorTemplate.Replace("__PATH__", PowerShellLiteral.EscapeSingleQuoted(path));

    /// <summary>Parses <paramref name="text"/>. An empty script has no diagnostics and writes nothing.</summary>
    public static Task<ScriptInspection> InspectAsync(string? text, CancellationToken ct = default) =>
        InspectAsync(text, Path.GetTempPath(), ct);

    /// <summary>Parses <paramref name="text"/> through a temporary file in <paramref name="tempFolder"/>.</summary>
    internal static async Task<ScriptInspection> InspectAsync(string? text, string tempFolder, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(text)) return ScriptInspection.Empty;
        var path = Path.Combine(tempFolder, "apkg-parse-" + Guid.NewGuid().ToString("N") + ".ps1");
        try
        {
            await File.WriteAllTextAsync(path, text, new UTF8Encoding(false), ct).ConfigureAwait(false);
            var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(InspectorCommand(path)));
            var run = await BoundedProcessRunner.RunAsync(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + encoded,
                StandardOutputEncoding = Encoding.UTF8
            }, Timeout, ct).ConfigureAwait(false);
            if (run.TimedOut) return new ScriptInspection([], false, "Windows PowerShell did not finish parsing the script.");
            if (run.ExitCode != 0 || string.IsNullOrWhiteSpace(run.StdOut))
                return new ScriptInspection([], false, string.IsNullOrWhiteSpace(run.StdErr) ? $"Windows PowerShell exited {run.ExitCode}." : run.StdErr.Trim());
            return ReadOutput(run.StdOut);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            return new ScriptInspection([], false, ex.Message);
        }
        finally
        {
            try { File.Delete(path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>The inspector's output as an inspection; output that is not the expected JSON is the inspection's failure.</summary>
    public static ScriptInspection ReadOutput(string stdout)
    {
        try { return Parse(stdout); }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return new ScriptInspection([], false, "Windows PowerShell returned output the inspector cannot read: " + ex.Message);
        }
    }

    /// <summary>Reads the inspector's JSON; a single error arrives as an object rather than a one-element array.</summary>
    public static ScriptInspection Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var errors = new List<ScriptParseError>();
        if (root.TryGetProperty("Errors", out var list))
        {
            IEnumerable<JsonElement> items = list.ValueKind switch
            {
                JsonValueKind.Array => list.EnumerateArray(),
                JsonValueKind.Object => [list],
                _ => []
            };
            foreach (var item in items)
                errors.Add(new ScriptParseError(
                    item.TryGetProperty("Line", out var line) && line.TryGetInt32(out var l) ? l : 0,
                    item.TryGetProperty("Column", out var column) && column.TryGetInt32(out var c) ? c : 0,
                    item.TryGetProperty("Message", out var message) ? message.GetString() ?? "" : ""));
        }
        var writes = root.TryGetProperty("WritesOutput", out var w) && w.ValueKind == JsonValueKind.True;
        var failure = root.TryGetProperty("Failure", out var f) ? f.GetString() : null;
        return new ScriptInspection(errors, writes, string.IsNullOrEmpty(failure) ? null : failure);
    }
}
