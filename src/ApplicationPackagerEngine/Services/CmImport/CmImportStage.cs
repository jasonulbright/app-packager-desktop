using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ApplicationPackagerEngine.Services.CmImport;

public sealed record CmStagedApp(string Name, string IntuneName, string Folder, string ContentFolder, string ManifestPath, string SetupFile);

public sealed record CmStageResult(string ApplicationFolder, IReadOnlyList<CmStagedApp> Staged);

/// <summary>
/// Writes a conversion plan to disk: one folder per converted deployment type with Content (a copy of the
/// deployment type's content) and stage-manifest.json, and in the application folder conversion-report.txt,
/// conversion-plan.json and relationships.json. The site is not changed.
/// </summary>
public static class CmImportStage
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, MaxDepth = 32 };

    /// <summary>A name as a folder name: characters outside word, dot, hyphen and space become underscores.</summary>
    public static string SafeName(string? name)
    {
        var safe = Regex.Replace(name ?? "", @"[^\w.\- ]", "_").Trim().TrimEnd('.');
        return string.IsNullOrWhiteSpace(safe) ? "item" : safe;
    }

    /// <summary>
    /// Mirrors the source into the destination so a repeat run never keeps a file the site content no
    /// longer holds. Only a folder named Content is mirrored.
    /// </summary>
    public static void CopyContent(string source, string destination)
    {
        if (!string.Equals(Path.GetFileName(destination.TrimEnd('\\', '/')), "Content", StringComparison.Ordinal))
            throw new InvalidOperationException($"The content copy refuses the destination '{destination}': its folder is not named Content.");
        Directory.CreateDirectory(destination);

        var start = new ProcessStartInfo("robocopy.exe") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in new[] { source, destination, "/MIR", "/R:2", "/W:2", "/NFL", "/NDL", "/NJH", "/NJS", "/NP" }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("robocopy.exe did not start.");
        var output = new StringBuilder();
        void Collect(object sender, DataReceivedEventArgs e)
        {
            if (e.Data is null) return;
            lock (output) output.AppendLine(e.Data);
        }
        process.OutputDataReceived += Collect;
        process.ErrorDataReceived += Collect;
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        // The parameterless WaitForExit also waits until both redirected streams are complete.
        process.WaitForExit();
        if (process.ExitCode >= 8)
        {
            string text;
            lock (output) text = output.ToString();
            var tail = string.Join(" ", text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).TakeLast(3));
            throw new InvalidOperationException($"Copying the content of '{source}' failed (robocopy exit {process.ExitCode}): {tail}");
        }
    }

    public static CmStageResult Export(CmImportPlan plan, string outputRoot, bool skipContent = false, string iconBase64 = "")
    {
        var utf8 = new UTF8Encoding(false);
        var appFolder = Path.Combine(outputRoot, SafeName(plan.ApplicationName));
        Directory.CreateDirectory(appFolder);
        var staged = new List<CmStagedApp>();
        var owners = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var dt in plan.DeploymentTypes)
        {
            if (!dt.Convert || dt.Manifest is null) continue;
            var folder = Path.Combine(appFolder, SafeName(dt.Name));
            if (owners.TryGetValue(folder, out var other))
                throw new InvalidOperationException($"Deployment types '{other}' and '{dt.Name}' map to the same folder '{folder}'. Rename one in ConfigMgr.");
            owners[folder] = dt.Name;

            var contentFolder = Path.Combine(folder, "Content");
            if (!skipContent)
            {
                if (dt.ContentLocation.Length == 0) throw new InvalidOperationException($"Deployment type '{dt.Name}' has no content location.");
                CopyContent(dt.ContentLocation, contentFolder);
                if (!File.Exists(Path.Combine(contentFolder, dt.Manifest.SetupFile)))
                    throw new InvalidOperationException($"The setup file '{dt.Manifest.SetupFile}' is not in the copied content of '{dt.Name}'.");
            }
            else Directory.CreateDirectory(contentFolder);

            dt.Manifest.ContentRoot = contentFolder;
            if (iconBase64.Length > 0)
            {
                File.WriteAllBytes(Path.Combine(folder, "app-icon.png"), Convert.FromBase64String(iconBase64));
                dt.Manifest.Icon = "app-icon.png";
            }
            var manifestPath = Path.Combine(folder, "stage-manifest.json");
            File.WriteAllText(manifestPath, JsonSerializer.Serialize(dt.Manifest, Json), utf8);
            staged.Add(new CmStagedApp(dt.Name, dt.IntuneName, folder, contentFolder, manifestPath, dt.Manifest.SetupFile));
        }

        File.WriteAllText(Path.Combine(appFolder, "conversion-report.txt"), CmImportPlanner.Report(plan), utf8);
        File.WriteAllText(Path.Combine(appFolder, "relationships.json"), JsonSerializer.Serialize(plan.Relationships, Json), utf8);
        var summary = new
        {
            plan.ApplicationName, plan.Publisher, plan.Version, plan.LogicalName, plan.Strategy, plan.Findings,
            DeploymentTypes = plan.DeploymentTypes.Select(d => new { d.Name, d.LogicalName, d.Priority, d.Technology, d.IntuneName, d.Convert, d.Exclusion, d.DetectionMode, d.Findings })
        };
        File.WriteAllText(Path.Combine(appFolder, "conversion-plan.json"), JsonSerializer.Serialize(summary, Json), utf8);
        return new CmStageResult(appFolder, staged);
    }
}
