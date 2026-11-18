using System.Text.Json;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Services.Supersedence;

namespace ApplicationPackagerEngine.Services.OneClick;

/// <summary>
/// How the supersedence-only pass of One Click ended for one application. Status is NotRun, NoOlderVersion, Applied or NotApplied;
/// Lines are the lines of the supersedence plan for the log.
/// </summary>
public sealed record OneClickSupersedencePassResult(
    SupersedenceStepStatus Status, string Message, IReadOnlyList<string> Warnings, IReadOnlyList<string> Lines)
{
    public static OneClickSupersedencePassResult Of(SupersedenceStepStatus status, string message,
        IReadOnlyList<string>? warnings = null, IReadOnlyList<string>? lines = null) =>
        new(status, message, warnings ?? [], lines ?? []);

    /// <summary>True when nothing is left to do for this version, so the pending state ends.</summary>
    public bool EndsPending => Status is SupersedenceStepStatus.Applied or SupersedenceStepStatus.NoOlderVersion;
}

/// <summary>
/// The three things the pass asks of its environment, so the pass itself can run against fakes: reading the stage manifest, resolving
/// the supersedence plan of one publish (settings, command line and the MSI ProductVersion source), and running the supersedence step.
/// </summary>
/// <param name="ReadManifest">Reads the stage manifest at a path.</param>
/// <param name="Plan">The supersedence plan of the manifest (manifest, folder of the staged content).</param>
/// <param name="RunStep">
/// The lookup and the supersedence step: application name, version, identity marker, mode, progress sink. The ConfigMgr connection is
/// made by the script of the step, so a site that cannot be reached comes back as a step that is not applied.
/// </param>
public sealed record OneClickSupersedencePassSteps(
    Func<string, StageManifest> ReadManifest,
    Func<StageManifest, string, SupersedencePlan> Plan,
    Func<string, string, string, SupersedenceMode, Action<string>, CancellationToken, Task<SupersedenceStepResult>> RunStep);

/// <summary>
/// The supersedence-only pass of One Click, for one application whose version is on the site and whose supersedence step failed at the
/// publish. It runs the lookup of the older application and the supersedence step, and nothing else: it stages nothing and publishes
/// nothing. The identity marker comes from the manifest of the last stage. The mode and the version rule resolve as at a publish. The
/// step runs with the published-only requirement, so the application of this title and version must be on the site, and the step reads
/// the relations that exist and adds only the missing ones, so a second run is safe. The pass never throws, except for a cancellation.
/// </summary>
public static class OneClickSupersedencePass
{
    public const string ManifestMissing =
        "stage-manifest.json not found, so the identity of the application line cannot be read. Stage the application once.";

    public static async Task<OneClickSupersedencePassResult> RunAsync(
        string? manifestPath, string applicationName, string version, string siteCode,
        OneClickSupersedencePassSteps steps, Action<string>? progress, CancellationToken ct)
    {
        var lines = new List<string>();
        OneClickSupersedencePassResult Result(SupersedenceStepStatus status, string message, IReadOnlyList<string>? warnings = null) =>
            OneClickSupersedencePassResult.Of(status, message, warnings, lines.ToList());
        try
        {
            if (string.IsNullOrWhiteSpace(manifestPath) || !File.Exists(manifestPath))
                return Result(SupersedenceStepStatus.NotApplied, ManifestMissing);

            StageManifest manifest;
            try { manifest = steps.ReadManifest(manifestPath); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
            {
                return Result(SupersedenceStepStatus.NotApplied, $"The stage manifest {manifestPath} could not be read: {ex.Message}");
            }

            var plan = steps.Plan(manifest, Path.GetDirectoryName(manifestPath) ?? "");
            lines.AddRange(plan.Log);
            if (plan.Refusal is not null) return Result(SupersedenceStepStatus.NotApplied, plan.Refusal);
            if (plan.Mode == SupersedenceMode.Off) return Result(SupersedenceStepStatus.NotRun, "the supersedence mode is Off.");
            if (string.IsNullOrWhiteSpace(plan.Marker)) return Result(SupersedenceStepStatus.NotRun, "the application has no identity marker.");
            if (string.IsNullOrWhiteSpace(siteCode))
                return Result(SupersedenceStepStatus.NotApplied, "Supersedence needs a ConfigMgr site. Set the site code in Options.");

            var step = await steps.RunStep(applicationName, version, plan.Marker, plan.Mode, progress ?? (_ => { }), ct);
            return Result(step.Status, step.Message, step.Warnings);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Result(SupersedenceStepStatus.NotApplied, string.IsNullOrWhiteSpace(ex.Message) ? "the supersedence step failed. See the log file." : ex.Message);
        }
    }
}
