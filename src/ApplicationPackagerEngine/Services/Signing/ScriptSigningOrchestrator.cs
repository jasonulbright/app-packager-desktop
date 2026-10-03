using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Models.Signing;

namespace ApplicationPackagerEngine.Services.Signing;

/// <summary>One requirement script to stage under scripts\requirements\&lt;RuleId&gt;.ps1.</summary>
public sealed record RequirementScriptSource(string? RuleId, string ScriptText, string? ScriptEncoding = null);

/// <summary>The manifest data stage signing reads.</summary>
public sealed class ScriptSigningInput
{
    /// <summary>
    /// The manifest's detection. Its ScriptText is replaced with the exact
    /// finalized text when a detection script is written.
    /// </summary>
    public StageDetection? Detection { get; init; }
    /// <summary>Encoding name of pre-signed detection text (UTF8BOM, UTF16LE, ...); empty for UTF-8 without a BOM.</summary>
    public string? DetectionScriptEncoding { get; init; }
    public IReadOnlyList<RequirementScriptSource> Requirements { get; init; } = [];
    /// <summary>Stage-relative paths of vendor scripts whose signatures are preserved.</summary>
    public IReadOnlyList<string> ThirdPartyScripts { get; init; } = [];
    /// <summary>The resolved install and uninstall commands the deployment types run.</summary>
    public IReadOnlyList<LauncherCommand> Commands { get; init; } = [];

    /// <summary>
    /// Collects the signing inputs from a stage manifest: its detection, its
    /// command overrides and per-deployment-type commands, and one
    /// requirement script per Script-kind condition template the manifest's
    /// deployment types or <paramref name="extraRequirements"/> reference.
    /// </summary>
    public static ScriptSigningInput FromManifest(StageManifest manifest,
        ConditionTemplateDocument? templates = null,
        IEnumerable<StageRequirement>? extraRequirements = null,
        IEnumerable<string>? thirdPartyScripts = null)
    {
        var commands = new List<LauncherCommand>();
        void Add(string source, string? text)
        {
            if (!string.IsNullOrWhiteSpace(text)) commands.Add(new LauncherCommand(source, text));
        }
        Add("InstallCommandLine", manifest.CommandOverrides?.Install);
        Add("UninstallCommandLine", manifest.CommandOverrides?.Uninstall);
        var dts = manifest.DeploymentTypes ?? [];
        for (var i = 0; i < dts.Length; i++)
        {
            Add($"DeploymentTypes[{i + 1}].InstallCommandLine", dts[i].InstallCommandLine);
            Add($"DeploymentTypes[{i + 1}].UninstallCommandLine", dts[i].UninstallCommandLine);
        }

        var requirements = new List<RequirementScriptSource>();
        if (templates is not null)
        {
            var specs = dts.SelectMany(d => d.Requirements ?? [])
                .Concat(extraRequirements ?? [])
                .Where(r => r is not null);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var spec in specs)
            {
                var template = templates.Conditions.FirstOrDefault(
                    c => string.Equals(c.Id, spec.ConditionId, StringComparison.OrdinalIgnoreCase));
                if (template is null || !string.Equals(template.Kind, "Script", StringComparison.Ordinal)) continue;
                if (!seen.Add(template.Id)) continue;
                requirements.Add(new RequirementScriptSource(template.Id, ConditionTemplateService.ResolveScriptText(template)));
            }
        }

        return new ScriptSigningInput
        {
            Detection = manifest.Detection,
            Requirements = requirements,
            ThirdPartyScripts = thirdPartyScripts?.ToList() ?? [],
            Commands = commands
        };
    }
}

/// <summary>
/// Signs every selected category of a staged build in a fixed order: resolve
/// one certificate when any Sign switch is on, Detection, Requirements,
/// Deployment, the launcher-chain audit, then every Require switch. A Require
/// switch whose category did not reach SignedAndVerified or
/// ExistingSignatureValid throws, so a partially signed build never publishes.
/// </summary>
public static class ScriptSigningOrchestrator
{
    public const string DetectionFile = @"scripts\detect.ps1";
    public const string RequirementsFolder = @"scripts\requirements";

    /// <summary>
    /// Signs the stage at <paramref name="stageRoot"/> and returns the
    /// ScriptSigning manifest record. Runs after every staged file (wrappers,
    /// icon) is written and before FileHashes are computed, so the hashes
    /// cover the signed bytes and nothing edits a signed file afterwards.
    /// </summary>
    public static ScriptSigningRecord SignStage(string stageRoot, ScriptSigningInput input, SigningPolicy policy,
        ScriptSigningService? signer = null)
    {
        var normalized = SigningPolicy.Normalize(policy);
        signer ??= new ScriptSigningService();

        // scripts\ is regenerated from the current input; a previous build's detector or
        // requirement script left there would otherwise be signed and shipped.
        var scriptsFolder = Path.Combine(stageRoot, "scripts");
        if (Directory.Exists(scriptsFolder)) Directory.Delete(scriptsFolder, recursive: true);

        X509Certificate2? cert = normalized.AnySign ? signer.Certificates.Resolve(normalized) : null;

        var record = new ScriptSigningRecord { PolicyDigest = normalized.PolicyDigest };

        var detection = input.Detection;
        if (detection is not null && !string.IsNullOrWhiteSpace(detection.ScriptText) && IsPowerShell(detection.ScriptLanguage))
        {
            var scriptsDir = Path.Combine(stageRoot, "scripts");
            Directory.CreateDirectory(scriptsDir);
            var detectPath = Path.Combine(stageRoot, DetectionFile);
            // Always rewritten from the manifest: a re-stage at the same version
            // finds the previous file in place, and reusing it would publish the
            // superseded detection script.
            WriteScriptFile(detectPath, detection.ScriptText!, input.DetectionScriptEncoding);

            var signed = signer.SignForCategory(detectPath, normalized, SigningCategory.Detection, certificate: cert);
            var representation = ScriptSignatureVerifier.Represent(detectPath);
            record.Detection = new DetectionSigningRecord
            {
                Status = signed.Status,
                Thumbprint = signed.Thumbprint,
                Sha256 = signed.Sha256,
                Timestamped = signed.Timestamped,
                File = DetectionFile,
                Reason = signed.Reason
            };
            if (representation.ExceedsCMLimit)
                throw new InvalidOperationException(
                    $"Detection script is {representation.Length} bytes after signing; ConfigMgr refuses a script larger than {representation.CMScriptMaxBytes} bytes.");
            detection.ScriptText = representation.Text;
        }

        var requirements = RequirementScripts(input.Requirements);
        if (requirements.Count > 0)
        {
            var reqDir = Path.Combine(stageRoot, RequirementsFolder);
            Directory.CreateDirectory(reqDir);
            var items = new List<RequirementSigningItem>();
            foreach (var req in requirements)
            {
                var file = Path.Combine(reqDir, req.RuleId + ".ps1");
                WriteScriptFile(file, req.ScriptText, req.ScriptEncoding);
                var signed = signer.SignForCategory(file, normalized, SigningCategory.Requirements, certificate: cert);
                items.Add(new RequirementSigningItem
                {
                    RuleId = req.RuleId,
                    File = $@"{RequirementsFolder}\{req.RuleId}.ps1",
                    Sha256 = signed.Sha256,
                    Thumbprint = signed.Thumbprint,
                    Status = signed.Status,
                    Reason = signed.Reason
                });
            }
            record.Requirements = new RequirementsSigningRecord
            {
                Items = [.. items],
                Status = SigningStatus.Aggregate(items.Select(i => i.Status))
            };
        }

        var (owned, thirdParty) = DeploymentInventory(stageRoot, input.ThirdPartyScripts);
        if (owned.Count > 0 || thirdParty.Count > 0)
        {
            var files = new List<DeploymentSigningFile>();
            foreach (var (fullName, relative) in owned)
                files.Add(DeploymentEntry(signer.SignForCategory(fullName, normalized, SigningCategory.Deployment, certificate: cert),
                    relative, DeploymentSigningFile.OwnerAppPackager));
            foreach (var (fullName, relative) in thirdParty)
                files.Add(DeploymentEntry(signer.SignForCategory(fullName, normalized, SigningCategory.Deployment, preserveExisting: true, certificate: cert),
                    relative, DeploymentSigningFile.OwnerThirdParty));
            record.Deployment.Files = [.. files];
            record.Deployment.Status = SigningStatus.Aggregate(files.Select(f => f.Status));
        }

        var chain = DeploymentLauncher.AuditChain(stageRoot, input.Commands, normalized, input.ThirdPartyScripts);
        record.Deployment.LaunchersBypassFree = chain.BypassFree;

        foreach (var category in new[] { SigningCategory.Detection, SigningCategory.Requirements, SigningCategory.Deployment })
        {
            if (!normalized.Require(category)) continue;
            var status = category switch
            {
                SigningCategory.Detection => record.Detection.Status,
                SigningCategory.Requirements => record.Requirements.Status,
                _ => record.Deployment.Status
            };
            if (!SigningStatus.IsSigned(status))
                throw new InvalidOperationException(
                    $"Require{category} is set but the {category} category status is '{status}'; refusing to publish.");
        }

        // The launchers were generated without an execution-policy argument, so an unsigned
        // deployment script cannot run on an AllSigned endpoint.
        if (normalized.SignDeployment && record.Deployment.Status == SigningStatus.Failed)
        {
            var reason = string.Join("; ", record.Deployment.Files
                .Where(f => f.Status == SigningStatus.Failed)
                .Select(f => $"{f.RelativePath}: {f.Reason}"));
            throw new InvalidOperationException(
                $"Deployment script signing failed and the launchers carry no execution-policy relaxation: {reason}");
        }

        return record;
    }

    /// <summary>
    /// Every .ps1/.psm1 under the stage root except scripts\ (owned by the
    /// Detection and Requirements categories), split into AppPackager-owned
    /// files and third-party files (PSADT layouts and declared scripts).
    /// </summary>
    public static (List<(string FullName, string RelativePath)> Owned, List<(string FullName, string RelativePath)> ThirdParty)
        DeploymentInventory(string stageRoot, IEnumerable<string>? declaredThirdPartyScripts)
    {
        var owned = new List<(string, string)>();
        var thirdParty = new List<(string, string)>();
        if (!Directory.Exists(stageRoot)) return (owned, thirdParty);

        var roots = DeploymentLauncher.ThirdPartyRoots(stageRoot, declaredThirdPartyScripts);
        var rootFull = Path.GetFullPath(stageRoot).TrimEnd('\\');
        foreach (var file in Directory.EnumerateFiles(rootFull, "*", SearchOption.AllDirectories)
                     .Where(f => Regex.IsMatch(Path.GetExtension(f), @"^(?i)\.(ps1|psm1)$"))
                     .OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            var relative = file[rootFull.Length..].TrimStart('\\');
            if (relative.StartsWith(@"scripts\", StringComparison.OrdinalIgnoreCase)) continue;
            if (DeploymentLauncher.IsThirdPartyFile(file, relative, roots)) thirdParty.Add((file, relative));
            else owned.Add((file, relative));
        }
        return (owned, thirdParty);
    }

    /// <summary>
    /// Writes script text under the stage root. Text that already carries a
    /// signature block verifies only when its original bytes are reproduced,
    /// so each candidate encoding is written and verified and the first that
    /// keeps the signature intact wins. Unsigned text is written as UTF-8
    /// without a byte-order mark.
    /// </summary>
    public static void WriteScriptFile(string path, string text, string? encodingName = null)
    {
        var utf8NoBom = new UTF8Encoding(false);
        var candidates = new List<Encoding>();
        if (!string.IsNullOrWhiteSpace(encodingName))
            candidates.Add(ScriptSignatureVerifier.EncodingByName(encodingName));
        candidates.Add(utf8NoBom);

        if (text.Contains(ScriptSignatureVerifier.BeginMarker, StringComparison.OrdinalIgnoreCase))
        {
            candidates.Add(new UTF8Encoding(true));
            candidates.Add(new UnicodeEncoding(false, true));
            candidates.Add(new UnicodeEncoding(true, true));
            foreach (var encoding in candidates)
            {
                File.WriteAllText(path, text, encoding);
                if (ScriptSignatureVerifier.Verify(path).SignatureIntact) return;
            }
        }
        File.WriteAllText(path, text, candidates[0]);
    }

    /// <summary>
    /// Requirement scripts with text, each with a file-safe RuleId; a rule
    /// with no RuleId is named rule&lt;n&gt; by its 1-based position.
    /// </summary>
    public static List<(string RuleId, string ScriptText, string? ScriptEncoding)> RequirementScripts(
        IEnumerable<RequirementScriptSource>? sources)
    {
        var results = new List<(string, string, string?)>();
        var index = 0;
        foreach (var rule in sources ?? [])
        {
            index++;
            if (string.IsNullOrWhiteSpace(rule.ScriptText)) continue;
            var ruleId = string.IsNullOrEmpty(rule.RuleId) ? $"rule{index}" : rule.RuleId;
            results.Add((Regex.Replace(ruleId, "[^0-9A-Za-z._-]", "-"), rule.ScriptText, rule.ScriptEncoding));
        }
        return results;
    }

    private static DeploymentSigningFile DeploymentEntry(ScriptSigningResult signed, string relative, string owner) => new()
    {
        RelativePath = relative,
        Sha256 = signed.Sha256,
        Thumbprint = signed.Thumbprint,
        Status = signed.Status,
        Owner = owner,
        Reason = signed.Reason
    };

    private static bool IsPowerShell(string? language) =>
        string.IsNullOrWhiteSpace(language) || language.Equals("PowerShell", StringComparison.OrdinalIgnoreCase);
}
