using System.Text.Json;
using System.Text.Json.Serialization;
using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Models.Workbench;

/// <summary>
/// One installer revision of a BYO application, stored in application.json under
/// Sources. The installer itself lives at applications\&lt;key&gt;\sources\&lt;Revision&gt;\&lt;FileName&gt;.
/// </summary>
public sealed class ByoSourceRevision
{
    public int Revision { get; set; }
    public string FileName { get; set; } = "";

    /// <summary>
    /// Where the installer was stored when the revision was added or imported.
    /// Informational: the installer is always read from the revision folder under the
    /// current data root, so a moved or copied data root still resolves it.
    /// </summary>
    public string? Path { get; set; }

    public string Sha256 { get; set; } = "";
    public long Size { get; set; }
    public string? SoftwareVersion { get; set; }
    public string? InstallerType { get; set; }
    public string? ProductCode { get; set; }
    public string? Architecture { get; set; }
    public string? AppName { get; set; }
    public string? Publisher { get; set; }
    public string? AddedAt { get; set; }

    /// <summary>The Blocking fields that changed from the previous revision.</summary>
    public List<string> IncompatibleOverrides { get; set; } = [];

    /// <summary>The installer engine the analysis identified.</summary>
    public string? Engine { get; set; }

    /// <summary>The operator-confirmed values the ad-hoc stage of this revision uses.</summary>
    public ByoStageValues? StageValues { get; set; }

    /// <summary>Fields written by a newer build, kept so a save does not drop them.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>The drop review values a revision stages with, beyond its name, publisher and version.</summary>
public sealed class ByoStageValues
{
    public string? InstallArgs { get; set; }
    public string? UninstallArgs { get; set; }
    public string? UninstallCommand { get; set; }
    public string? InstallMode { get; set; }

    /// <summary>True when the operator confirmed predicted values; a predicted analysis does not stage without it.</summary>
    public bool Confirmed { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }

    public static ByoStageValues From(DroppedInstallerOverrides? overrides, bool confirmed) => new()
    {
        InstallArgs = overrides?.InstallArgs,
        UninstallArgs = overrides?.UninstallArgs,
        UninstallCommand = overrides?.UninstallCommand,
        InstallMode = overrides?.InstallMode,
        Confirmed = confirmed
    };
}

/// <summary>The identity of one installer, as its analysis reports it.</summary>
public sealed record ByoInstallerIdentity(
    string? InstallerType,
    string? Architecture,
    string? ProductCode,
    string? AppName,
    string? Publisher,
    string? SoftwareVersion,
    string? Engine = null)
{
    public static ByoInstallerIdentity From(DroppedInstallerAnalysis analysis) => new(
        analysis.InstallerType, analysis.Architecture, analysis.ProductCode.Value,
        analysis.AppName.Value, analysis.Publisher.Value, analysis.SoftwareVersion.Value, analysis.Engine);
}

/// <summary>Blocking invalidates the existing package; Review is worth a look and not fatal.</summary>
public enum ByoChangeSeverity
{
    Blocking,
    Review
}

/// <summary>One identity field that differs between the previous and the new revision.</summary>
public sealed record ByoIdentityChange(string Field, string Previous, string Current, ByoChangeSeverity Severity)
{
    /// <summary>InstallerType and Architecture drive detection and content shape; the rest are informational.</summary>
    public static readonly IReadOnlyList<string> BlockingFields = ["InstallerType", "Architecture"];

    /// <summary>The compared fields, in report order.</summary>
    public static readonly IReadOnlyList<string> ComparedFields = ["InstallerType", "Architecture", "ProductCode", "AppName", "Publisher"];

    public string SeverityText => Severity.ToString();

    public string Message => Severity == ByoChangeSeverity.Blocking
        ? $"{Field} changed from '{Show(Previous)}' to '{Show(Current)}'. The existing package no longer matches this installer; overrides that depend on it are flagged."
        : $"{Field} changed from '{Show(Previous)}' to '{Show(Current)}'. Worth a look before the next build.";

    private static string Show(string value) => string.IsNullOrEmpty(value) ? "(none)" : value;
}

/// <summary>What adding one source revision did, or would do.</summary>
public sealed record ByoSourceUpdate(
    string ApplicationId,
    int Revision,
    ByoSourceRevision Source,
    IReadOnlyList<ByoIdentityChange> IdentityChanges)
{
    public IReadOnlyList<string> IncompatibleOverrides => Source.IncompatibleOverrides;
}
