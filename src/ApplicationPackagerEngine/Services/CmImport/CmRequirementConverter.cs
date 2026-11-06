using System.Globalization;
using System.Text.RegularExpressions;

namespace ApplicationPackagerEngine.Services.CmImport;

/// <summary>
/// Reads the requirement rules of a deployment type. Native values go to properties that Graph
/// v1.0 carries (architecture, memory, processors). A predicate is a rule a generated script can
/// evaluate exactly. Rules that are neither are unrecognized: their meaning cannot be carried to
/// Intune, and they stop a later variant from proving that it excludes this one.
/// </summary>
public static class CmRequirementConverter
{
    /// <summary>'Windows/All_x64_Windows_10_higher' to architecture and build floor; null for an unsupported platform.</summary>
    public static CmPlatformPair? ParsePlatformId(string platformId)
    {
        var arch = "";
        var match = Regex.Match(platformId, "_(x64|x86|ARM64)_");
        if (match.Success) arch = match.Groups[1].Value.ToLowerInvariant();
        else if (platformId.Contains("_AMD64_", StringComparison.Ordinal)) arch = "x64";
        var build = 0;
        if (platformId.Contains("Windows_11", StringComparison.Ordinal)) build = 22000;
        else if (platformId.Contains("Windows_10", StringComparison.Ordinal)) build = 10240;
        if (platformId.Contains("ARM64_Windows_10", StringComparison.Ordinal) && arch.Length == 0) arch = "arm64";
        if (build == 0) return null;
        if (!platformId.Contains("_higher", StringComparison.Ordinal)) return null;
        return new CmPlatformPair(arch, build);
    }

    private static string Comparison(string op) => op switch
    {
        "Equals" => "-eq", "NotEquals" => "-ne", "GreaterThan" => "-gt", "GreaterEquals" => "-ge",
        "LessThan" => "-lt", "LessEquals" => "-le", "OneOf" => "-in", "NoneOf" => "-notin", _ => ""
    };

    public static CmRequirementModel Convert(IReadOnlyList<CmRequirementRule> requirements)
    {
        var findings = new List<CmFinding>();
        var native = new Dictionary<string, int>(StringComparer.Ordinal);
        var predicates = new List<CmPredicate>();
        var scriptOnly = new List<CmPredicate>();
        var unrecognized = new List<CmRequirementRule>();
        List<string>? architectures = null;
        var constrained = false;

        foreach (var rule in requirements)
        {
            switch (rule.Kind)
            {
                case "OsPlatform":
                {
                    var pairs = rule.PlatformIds.Select(ParsePlatformId).ToList();
                    if (pairs.Count == 0 || pairs.Any(p => p is null))
                    {
                        unrecognized.Add(rule);
                        findings.Add(new CmFinding("Review", "OsPlatformNotConverted",
                            $"The operating system requirement '{rule.DisplayName}' names a platform that Intune Win32 apps do not run on, or one this importer does not read."));
                        continue;
                    }
                    var negate = rule.Operator == "NoneOf";
                    var predicate = new CmOsPlatformPredicate(pairs.Select(p => p!).ToList(), negate);
                    predicates.Add(predicate);
                    if (negate) { scriptOnly.Add(predicate); continue; }

                    var archSet = predicate.Pairs.Select(p => p.Architecture).Distinct().ToList();
                    if (archSet.Contains("")) archSet = [];
                    var builds = predicate.Pairs.Select(p => p.MinimumBuild).Distinct().ToList();
                    if (archSet.Count > 0)
                    {
                        constrained = true;
                        architectures = architectures is null ? archSet : architectures.Where(archSet.Contains).ToList();
                    }
                    // A uniform Windows 10 floor is the Intune baseline. Mixed floors or a Windows 11 floor need the script to be exact.
                    if (builds.Any(b => b > 10240) || builds.Count > 1) scriptOnly.Add(predicate);
                    break;
                }
                case "Global":
                {
                    var comparison = Comparison(rule.Operator);
                    if (comparison.Length == 0) { unrecognized.Add(rule); break; }
                    switch (rule.LogicalName)
                    {
                        case "TotalPhysicalMemory":
                        {
                            var bytes = long.Parse(rule.Values[0], CultureInfo.InvariantCulture);
                            var predicate = new CmMemoryPredicate(comparison, bytes);
                            predicates.Add(predicate);
                            if (rule.Operator == "GreaterEquals")
                            {
                                var megabytes = (int)Math.Ceiling(bytes / 1048576d);
                                if (!native.TryGetValue("MinimumMemoryInMB", out var current) || megabytes > current) native["MinimumMemoryInMB"] = megabytes;
                            }
                            else scriptOnly.Add(predicate);
                            break;
                        }
                        case "NumberOfProcessors":
                        {
                            var count = int.Parse(rule.Values[0], CultureInfo.InvariantCulture);
                            var predicate = new CmProcessorsPredicate(comparison, count);
                            predicates.Add(predicate);
                            if (rule.Operator == "GreaterEquals")
                            {
                                if (!native.TryGetValue("MinimumNumberOfProcessors", out var current) || count > current) native["MinimumNumberOfProcessors"] = count;
                            }
                            else scriptOnly.Add(predicate);
                            break;
                        }
                        case "OSLanguage":
                        {
                            var predicate = new CmLanguagePredicate(comparison, rule.Values.Select(v => int.Parse(v, CultureInfo.InvariantCulture)).ToList());
                            predicates.Add(predicate);
                            scriptOnly.Add(predicate);
                            break;
                        }
                        default:
                            unrecognized.Add(rule);
                            findings.Add(new CmFinding("Review", "RequirementNotConverted",
                                $"The requirement '{rule.DisplayName}' (condition {rule.LogicalName}) has no Intune equivalent in this importer."));
                            break;
                    }
                    break;
                }
                default:
                {
                    unrecognized.Add(rule);
                    var reason = rule.Reason.Length > 0 ? rule.Reason : "The requirement kind is not converted.";
                    findings.Add(new CmFinding("Review", "RequirementNotConverted", $"The requirement '{rule.DisplayName}' is not converted. {reason}"));
                    break;
                }
            }
        }

        return new CmRequirementModel
        {
            Architectures = architectures,
            ArchitectureConstrained = constrained,
            Native = native,
            Predicates = predicates,
            ScriptOnly = scriptOnly,
            Unrecognized = unrecognized,
            HasRules = requirements.Count > 0,
            Evaluable = unrecognized.Count == 0,
            Findings = findings
        };
    }

    private static string PredicateExpression(CmPredicate predicate)
    {
        switch (predicate)
        {
            case CmOsPlatformPredicate os:
            {
                var alternatives = os.Pairs.Select(p =>
                {
                    var parts = new List<string>();
                    if (p.Architecture.Length > 0) parts.Add($"$arch -eq '{p.Architecture}'");
                    parts.Add($"[int]$os.BuildNumber -ge {p.MinimumBuild}");
                    return "(" + string.Join(" -and ", parts) + ")";
                });
                var expression = "(" + string.Join(" -or ", alternatives) + ")";
                return os.Negate ? "(-not " + expression + ")" : expression;
            }
            case CmMemoryPredicate m: return $"([int64]$cs.TotalPhysicalMemory {m.Comparison} {m.Value.ToString(CultureInfo.InvariantCulture)})";
            case CmProcessorsPredicate c: return $"([int]$cs.NumberOfProcessors {c.Comparison} {c.Value.ToString(CultureInfo.InvariantCulture)})";
            case CmLanguagePredicate l: return $"([int]$os.OSLanguage {l.Comparison} @({string.Join(",", l.Values)}))";
            default: throw new InvalidOperationException($"Unknown predicate type '{predicate.GetType().Name}'.");
        }
    }

    /// <summary>
    /// The requirement script that keeps "first match wins": it prints true when the device passes
    /// the predicates of this variant that no native property carries, and passes none of the
    /// requirement sets of the higher-priority variants. Each earlier set is the AND of its predicates.
    /// </summary>
    public static string VariantScript(IReadOnlyList<CmPredicate>? ownPredicates = null, IReadOnlyList<IReadOnlyList<CmPredicate>>? earlierSets = null)
    {
        var lines = new List<string>
        {
            "$ErrorActionPreference = 'Stop'",
            "$os = Get-CimInstance -ClassName Win32_OperatingSystem",
            "$cs = Get-CimInstance -ClassName Win32_ComputerSystem",
            "$cpu = Get-CimInstance -ClassName Win32_Processor | Select-Object -First 1",
            "$arch = 'x86'",
            "if ([Environment]::Is64BitOperatingSystem) { $arch = $(if ([int]$cpu.Architecture -eq 12) { 'arm64' } else { 'x64' }) }"
        };
        var terms = new List<string>();
        if (ownPredicates is { Count: > 0 })
            terms.Add("(" + string.Join(" -and ", ownPredicates.Select(PredicateExpression)) + ")");
        foreach (var set in earlierSets ?? [])
            terms.Add("(-not (" + string.Join(" -and ", set.Select(PredicateExpression)) + "))");
        if (terms.Count == 0) terms.Add("$true");
        lines.Add("try { $selected = " + string.Join(" -and ", terms) + " } catch { $selected = $false }");
        lines.Add("Write-Output ([string]([bool]$selected)).ToLowerInvariant()");
        return string.Join("\r\n", lines);
    }

    /// <summary>
    /// How a variant excludes the variants that rank above it. ConfigMgr installs the first
    /// deployment type whose requirements pass, so a later variant is selected only when every
    /// earlier variant is not. An earlier variant without requirements always passes (Unreachable).
    /// An earlier variant with a requirement that cannot be evaluated makes the exclusion unprovable.
    /// Two variants with disjoint architecture sets exclude each other without a script.
    /// </summary>
    public static CmExclusion Exclusion(CmRequirementModel own, IReadOnlyList<CmRequirementModel> earlier)
    {
        if (earlier.Count == 0) return CmExclusion.None;

        var sets = new List<IReadOnlyList<CmPredicate>>();
        foreach (var model in earlier)
        {
            if (!model.HasRules)
                return new CmExclusion("Unreachable", [], "A higher-priority deployment type has no requirements, so ConfigMgr always chooses it first.");
            if (!model.Evaluable)
                return new CmExclusion("Unprovable", [], "A higher-priority deployment type has a requirement that cannot be evaluated outside ConfigMgr.");
            var disjoint = false;
            if (own.Architectures is { Count: > 0 } mine && model.Architectures is { Count: > 0 } theirs)
                disjoint = !mine.Any(theirs.Contains);
            if (!disjoint) sets.Add(model.Predicates);
        }
        return sets.Count == 0 ? new CmExclusion("Native", [], "") : new CmExclusion("Script", sets, "");
    }
}
