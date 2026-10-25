using System.Text;
using System.Text.Json;
using ApplicationPackagerEngine.Models;

namespace ApplicationPackagerEngine.Services;

/// <summary>
/// Tests a detection on this computer through <see cref="DetectionEvaluator"/>. Script
/// clauses keep the output rule of the standalone tester (output means detected) and
/// carry a warning in their detail where ConfigMgr, which also reads the exit code,
/// decides otherwise.
/// </summary>
public sealed class DetectionTestService(IRegistryProvider? registry = null, IDetectionScriptHost? scriptHost = null)
    : IDetectionTestService
{
    public static readonly TimeSpan DefaultScriptTimeout = TimeSpan.FromSeconds(30);

    public const string LocalRunContext = "This app, current user, not SYSTEM";

    public const string ScriptNotStarted = "Not started: the earlier scripts of this test used the whole timeout.";

    private readonly IRegistryProvider _registry = registry ?? WindowsRegistryProvider.Instance;
    private readonly IDetectionScriptHost _scriptHost = scriptHost ?? new WindowsPowerShellDetectionScriptHost();

    public async Task<DetectionTestResult> RunAsync(DetectionTestRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Detection);
        ct.ThrowIfCancellationRequested();

        var detection = WithView(request.Detection, request.View);
        var timeout = request.ScriptTimeout > TimeSpan.Zero ? request.ScriptTimeout : DefaultScriptTimeout;
        var runs = new List<DetectionScriptRun>();
        // One budget for all script clauses of the test, counted from the first script start.
        System.Diagnostics.Stopwatch? clock = null;

        var context = new DetectionEvaluationContext
        {
            Registry = _registry,
            ScriptRule = ScriptVerdictRule.OutputOnly,
            RunScript = text =>
            {
                var remaining = clock is null ? timeout : timeout - clock.Elapsed;
                clock ??= System.Diagnostics.Stopwatch.StartNew();
                DetectionScriptRun run;
                if (remaining <= TimeSpan.Zero)
                    run = new DetectionScriptRun(null, "", ScriptNotStarted, TimedOut: true);
                else
                    // The evaluator is synchronous; this runs on a pool thread, never the caller's.
                    run = _scriptHost.RunAsync(text, remaining, ct).GetAwaiter().GetResult();
                lock (runs) runs.Add(run);
                return run;
            }
        };

        var trace = await Task.Run(() => DetectionEvaluator.Trace(detection, context), ct).ConfigureAwait(false);

        var clauses = new List<DetectionClauseResult>();
        Flatten(trace, 0, clauses);

        var detected = trace.Verdict == DetectionEvaluation.Detected;
        var evaluated = trace.Verdict != DetectionEvaluation.Error;
        var passed = evaluated && (request.Negative ? !detected : detected);

        return new DetectionTestResult(
            detected,
            passed,
            Summarize(trace, request.Negative, passed),
            clauses,
            runs.Count == 0 ? null : Join(runs, r => r.StdOut),
            runs.Count == 0 ? null : Join(runs, r => r.StdErr),
            runs.Count == 0 ? null : runs[^1].ExitCode,
            runs.Count == 0 ? LocalRunContext : WindowsPowerShellDetectionScriptHost.RunContext);
    }

    /// <summary>The request's view replaces the clause's own, on a copy; compound clauses keep their views.</summary>
    private static StageDetection WithView(StageDetection detection, RegistryViewChoice view)
    {
        var copy = JsonSerializer.Deserialize<StageDetection>(JsonSerializer.Serialize(detection))!;
        if (!string.Equals(copy.Type, "Compound", StringComparison.Ordinal))
            copy.Is64Bit = view == RegistryViewChoice.Registry64;
        return copy;
    }

    private static void Flatten(DetectionTrace trace, int depth, List<DetectionClauseResult> into)
    {
        into.Add(new DetectionClauseResult(
            new string(' ', depth * 2) + DetectionEvaluator.Describe(trace.Clause),
            trace.Verdict == DetectionEvaluation.Detected,
            trace.Verdict == DetectionEvaluation.Error ? "Could not be evaluated: " + trace.Detail : trace.Detail));
        foreach (var child in trace.Children) Flatten(child, depth + 1, into);
    }

    private static string Summarize(DetectionTrace trace, bool negative, bool passed)
    {
        if (trace.Verdict == DetectionEvaluation.Error)
            return $"Failed: the detection could not be evaluated. {FirstError(trace)}";
        var detected = trace.Verdict == DetectionEvaluation.Detected;
        var state = detected ? "detected" : "not detected";
        if (!negative) return passed ? $"Passed: {state}." : $"Failed: {state}.";
        return passed
            ? $"Passed: {state}, as the negative test expects."
            : $"Failed: {state}; the negative test expects not detected.";
    }

    private static string FirstError(DetectionTrace trace)
    {
        if (trace.Children.Count == 0) return trace.Detail;
        var child = trace.Children.FirstOrDefault(c => c.Verdict == DetectionEvaluation.Error);
        return child is null ? trace.Detail : FirstError(child);
    }

    private static string Join(List<DetectionScriptRun> runs, Func<DetectionScriptRun, string> pick)
    {
        if (runs.Count == 1) return pick(runs[0]);
        var text = new StringBuilder();
        for (var i = 0; i < runs.Count; i++)
        {
            if (i > 0) text.AppendLine();
            text.Append("[Script ").Append(i + 1).AppendLine("]").Append(pick(runs[i]));
        }
        return text.ToString();
    }
}
