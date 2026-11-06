using System.Text.Json.Nodes;

namespace ApplicationPackagerEngine.Services.CmImport;

/// <summary>
/// One note about a conversion. Severity is Blocking, Review or Info; the code names the case.
/// </summary>
public sealed record CmFinding(string Severity, string Code, string Message);

/// <summary>A node of a stored detection expression: a group or a leaf comparison.</summary>
public abstract record DetectionNode;

/// <summary>An And or Or over children.</summary>
public sealed record DetectionGroup(string Operator, IReadOnlyList<DetectionNode> Children) : DetectionNode;

/// <summary>One comparison of a stored setting with a constant.</summary>
public sealed record DetectionLeaf(
    string Source, string Property, string Operator, string Expected, string DataType,
    string Path, string Name, string Hive, string Key, bool Is64Bit, bool Supported, string Reason) : DetectionNode;

/// <summary>The detection of one deployment type: Script, ProductCode, Enhanced or another method.</summary>
public sealed record CmDetection(
    string Method, DetectionNode? Tree, string ScriptText, string ScriptLanguage, bool RunAs32Bit,
    string ProductCode, string ProductVersion);

/// <summary>One requirement rule: an operating system platform, a global condition or an unsupported rule.</summary>
public sealed record CmRequirementRule(
    string Kind, string DisplayName, string Operator, IReadOnlyList<string> PlatformIds,
    string LogicalName, IReadOnlyList<string> Values, string DataType, string Reason);

/// <summary>One referenced deployment type of a dependency or supersedence rule.</summary>
public sealed class CmReferenceTarget
{
    public string DesiredState { get; init; } = "";
    public string ApplicationLogical { get; init; } = "";
    public string DeploymentTypeLogical { get; init; } = "";
    public string ApplicationName { get; set; } = "";
    public string DeploymentTypeName { get; set; } = "";
}

public sealed record CmReferenceGroup(string GroupName, string Operator, IReadOnlyList<CmReferenceTarget> Targets);

public sealed record CmExitCode(int Code, string Class);

/// <summary>One deployment type of a stored application.</summary>
public sealed class CmDeploymentType
{
    public string Name { get; init; } = "";
    public string LogicalName { get; init; } = "";
    public int Priority { get; init; }
    public string Technology { get; init; } = "";
    public string UnsupportedReason { get; init; } = "";
    public string ExecutionContext { get; init; } = "System";
    public string RequiresLogOn { get; init; } = "";
    public bool RequiresUserInteraction { get; init; }
    public string PostInstallBehavior { get; init; } = "BasedOnExitCode";
    public int MaxRuntimeMins { get; init; }
    public int EstimatedRuntimeMins { get; init; }
    public bool RunAs32Bit { get; init; }
    public string InstallCommand { get; init; } = "";
    public string UninstallCommand { get; init; } = "";
    public string UninstallSetting { get; init; } = "";
    public string ContentLocation { get; set; } = "";
    public string ContentId { get; init; } = "";
    public string UninstallContentId { get; init; } = "";
    public IReadOnlyList<string> ContentFiles { get; init; } = [];
    public IReadOnlyList<CmExitCode> ExitCodes { get; init; } = [];
    public CmDetection? Detection { get; init; }
    public IReadOnlyList<CmRequirementRule> Requirements { get; init; } = [];
    public IReadOnlyList<CmReferenceGroup> Dependencies { get; init; } = [];
    public IReadOnlyList<CmReferenceGroup> Supersedes { get; init; } = [];
}

/// <summary>A stored ConfigMgr application with its deployment types in priority order.</summary>
public sealed class CmApplication
{
    public string Name { get; init; } = "";
    public string Publisher { get; init; } = "";
    public string Version { get; init; } = "";
    public string Description { get; init; } = "";
    public string LogicalName { get; init; } = "";
    public string IconBase64 { get; init; } = "";
    public IReadOnlyList<CmDeploymentType> DeploymentTypes { get; init; } = [];
}

/// <summary>A requirement that a generated script evaluates.</summary>
public abstract record CmPredicate;

public sealed record CmPlatformPair(string Architecture, int MinimumBuild);

public sealed record CmOsPlatformPredicate(IReadOnlyList<CmPlatformPair> Pairs, bool Negate) : CmPredicate;

public sealed record CmMemoryPredicate(string Comparison, long Value) : CmPredicate;

public sealed record CmProcessorsPredicate(string Comparison, int Value) : CmPredicate;

public sealed record CmLanguagePredicate(string Comparison, IReadOnlyList<int> Values) : CmPredicate;

/// <summary>
/// The requirement rules of a deployment type, split into what Graph v1.0 carries natively,
/// what a generated script evaluates, and what cannot be carried.
/// </summary>
public sealed class CmRequirementModel
{
    /// <summary>Null when no operating system rule names an architecture; an empty list when the rules share none.</summary>
    public IReadOnlyList<string>? Architectures { get; init; }
    public bool ArchitectureConstrained { get; init; }
    public IReadOnlyDictionary<string, int> Native { get; init; } = new Dictionary<string, int>();
    public IReadOnlyList<CmPredicate> Predicates { get; init; } = [];
    public IReadOnlyList<CmPredicate> ScriptOnly { get; init; } = [];
    public IReadOnlyList<CmRequirementRule> Unrecognized { get; init; } = [];
    public bool HasRules { get; init; }
    public bool Evaluable { get; init; }
    public IReadOnlyList<CmFinding> Findings { get; init; } = [];
}

/// <summary>How a variant excludes the variants that rank above it: None, Native, Script, Unreachable or Unprovable.</summary>
public sealed record CmExclusion(string Status, IReadOnlyList<IReadOnlyList<CmPredicate>> EarlierSets, string Reason)
{
    public static CmExclusion None { get; } = new("None", [], "");
}

/// <summary>The Detection rules of one deployment type: Native, Script, ExpressionScript, ProductCode or None.</summary>
public sealed record CmDetectionResult(IReadOnlyList<JsonObject> Rules, string Mode, IReadOnlyList<CmFinding> Findings);

public sealed record CmTiming(int EstimatedRuntimeMins, int MaximumRuntimeMins, bool IntuneApplied);

public sealed record CmSource(string Application, string DeploymentType, int Priority, string ContentLocation);

/// <summary>
/// The flattened description of one Intune app, written as stage-manifest.json. The property
/// names match the PowerShell app, so a manifest from either app reads in both.
/// </summary>
public sealed class CmIntuneManifest
{
    public string AppName { get; init; } = "";
    public string Publisher { get; init; } = "";
    public string SoftwareVersion { get; init; } = "";
    public string Description { get; init; } = "";
    public string ApplicationId { get; init; } = "";
    public string ProfileId { get; init; } = "";
    public string InstallCommandLine { get; init; } = "";
    public string UninstallCommandLine { get; init; } = "";
    public string SetupFile { get; init; } = "";
    public string InstallationBehaviorType { get; init; } = "InstallForSystem";
    public bool RequireUserInteraction { get; init; }
    public string Architecture { get; init; } = "";
    public CmTiming Timing { get; init; } = new(0, 0, false);
    public IReadOnlyList<JsonObject> IntuneRules { get; init; } = [];
    public IReadOnlyList<JsonObject> IntuneReturnCodes { get; init; } = [];
    public string IntuneRestartBehavior { get; init; } = "basedOnReturnCode";
    public IReadOnlyDictionary<string, int> IntuneMinimums { get; init; } = new Dictionary<string, int>();
    public string IntuneAllowedArchitectures { get; init; } = "";
    public CmSource Source { get; init; } = new("", "", 0, "");
    public string? ContentRoot { get; set; }
    public string? Icon { get; set; }
}

/// <summary>The plan of one deployment type.</summary>
public sealed class CmDeploymentTypePlan
{
    public string Name { get; set; } = "";
    public string LogicalName { get; init; } = "";
    public int Priority { get; init; }
    public string Technology { get; init; } = "";
    public string IntuneName { get; init; } = "";
    public bool Convert { get; init; }
    public string ContentLocation { get; set; } = "";
    public IReadOnlyList<string> ContentFiles { get; init; } = [];
    public string Exclusion { get; init; } = "None";
    public string DetectionMode { get; init; } = "";
    public CmIntuneManifest? Manifest { get; init; }
    public IReadOnlyList<CmFinding> Findings { get; init; } = [];
}

/// <summary>A dependency or supersedence that is listed and never created.</summary>
public sealed class CmRelationship
{
    public string Type { get; init; } = "";
    public string FromDeploymentType { get; init; } = "";
    public string FromApp { get; init; } = "";
    public string GroupName { get; init; } = "";
    public string GroupOperator { get; init; } = "";
    public string DesiredState { get; init; } = "";
    public string TargetApplication { get; init; } = "";
    public string TargetDeploymentType { get; init; } = "";
    public string TargetApplicationName { get; set; } = "";
    public string TargetDeploymentTypeName { get; set; } = "";
}

public sealed class CmImportPlan
{
    public string ApplicationName { get; init; } = "";
    public string Publisher { get; init; } = "";
    public string Version { get; init; } = "";
    public string LogicalName { get; init; } = "";
    public string Strategy { get; init; } = CmImportPlanner.PerDeploymentType;
    public IReadOnlyList<CmDeploymentTypePlan> DeploymentTypes { get; init; } = [];
    public IReadOnlyList<CmRelationship> Relationships { get; init; } = [];
    public IReadOnlyList<CmFinding> Findings { get; init; } = [];
}

/// <summary>What the planner takes besides the application.</summary>
public sealed record CmImportOptions(
    string VariantStrategy = CmImportPlanner.PerDeploymentType,
    bool NativeDetectionOnly = false,
    bool AllowUntranslatedRequirements = false);
