using System.Globalization;
using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerUI.ViewModels.Pages;

/// <summary>A name and a value as the Overview grids show them.</summary>
public sealed record NameValueRow(string Name, string Value)
{
    public override string ToString() => $"{Name}, {Value}";
}

/// <summary>A deployment field with its confidence in plain words.</summary>
public sealed record FactRow(string Name, string Value, string Confidence, string Source)
{
    public static FactRow From(AnalysisFact fact) => new(fact.Name, fact.Value, InstallerAnalysisText.Confidence(fact.Confidence), fact.Source);

    public override string ToString() => $"{Name}, {Value}, {Confidence}";
}

public sealed record ModeRow(string Mode, string Default, string Switch, string InstallCommand, string InstallFolder, string UninstallCommand, string ArpKey,
    string Hive, string View, string Context)
{
    public static ModeRow From(InstallMode mode) => new(
        Mode: InstallerAnalysisText.ModeName(mode.Name),
        Default: mode.IsDefault ? "Yes" : "No",
        Switch: mode.Switch,
        InstallCommand: mode.InstallCommand,
        InstallFolder: mode.InstallFolder,
        UninstallCommand: mode.UninstallCommand,
        ArpKey: mode.ArpKey,
        Hive: mode.Hive,
        View: InstallerAnalysisText.View(mode.View),
        Context: mode.Context);

    public override string ToString() => $"{Mode}, {Switch}";
}

public sealed record MetadataRow(string Section, string Name, string Value)
{
    public override string ToString() => $"{Section}, {Name}, {Value}";
}

/// <summary>One payload entry as the Payload, Inner Installers and File Browser grids show it.</summary>
public sealed class PayloadRow(PayloadEntry entry)
{
    public PayloadEntry Entry { get; } = entry;
    public string Path => Entry.Path;
    public string Name => InstallerAnalysisText.EntryName(Entry.Path);
    public string Folder => InstallerAnalysisText.EntryFolder(Entry.Path);
    public string Size => Entry.IsDirectory ? "" : InstallerAnalysisText.Size(Entry.Size);
    public string Kind => Entry.IsDirectory ? "Folder" : IsInnerInstaller ? "Inner installer" : "File";
    public string Type => Entry.InnerType;
    public bool IsInnerInstaller => string.Equals(Entry.Kind, "InnerInstaller", StringComparison.OrdinalIgnoreCase);

    public override string ToString() => $"{Name}, {Kind}";
}

public sealed record RegistryRow(RegistryWrite Write)
{
    public string Origin => InstallerAnalysisText.Origin(Write.Origin);
    public string Hive => Write.Hive;
    public string Key => Write.Key;
    public string Name => string.IsNullOrEmpty(Write.Name) ? "(Default)" : Write.Name;
    public string Value => Write.Value;
    public string Type => Write.ValueType;
    public string Condition => Write.Condition;
    public string Action => InstallerAnalysisText.Sentence(Write.Action);
    public string View => InstallerAnalysisText.View(Write.View);
    public string Section => Write.Section;
    public string FullKey => string.IsNullOrEmpty(Write.Hive) ? Write.Key : $@"{Write.Hive}\{Write.Key}";

    public override string ToString() => $@"{FullKey}, {Name}";
}

public sealed record StringRow(InterestingString Item)
{
    public string Text => Item.Text;
    public string Encoding => string.Equals(Item.Encoding, "Utf16", StringComparison.OrdinalIgnoreCase) ? "UTF-16" : "ASCII";
    public string Offset => "0x" + Item.Offset.ToString("X8", CultureInfo.InvariantCulture);
    public string Category => Item.Category;

    public override string ToString() => $"{Category}, {Text}";
}

/// <summary>One level of the drill-down; level 0 is the installer the user opened.</summary>
public sealed record BreadcrumbSegment(int Level, string Name, bool IsCurrent)
{
    public bool CanGo => !IsCurrent;

    public override string ToString() => Name;
}

/// <summary>The plain words the page uses for the values of an analysis.</summary>
public static class InstallerAnalysisText
{
    public static string Confidence(string? value) => value?.Trim() switch
    {
        var v when string.Equals(v, "Confirmed", StringComparison.OrdinalIgnoreCase) => "Confirmed",
        var v when string.Equals(v, "Predicted", StringComparison.OrdinalIgnoreCase) => "Predicted",
        _ => "Not known"
    };

    public static string ModeName(string? value) => value?.Trim() switch
    {
        var v when string.Equals(v, "PerMachine", StringComparison.OrdinalIgnoreCase) => "Per machine",
        var v when string.Equals(v, "PerUser", StringComparison.OrdinalIgnoreCase) => "Per user",
        var v => v ?? ""
    };

    public static string Origin(string? value) => value?.Trim() switch
    {
        var v when string.Equals(v, "MsiRegistryTable", StringComparison.OrdinalIgnoreCase) => "MSI Registry table",
        var v when string.Equals(v, "NsisScript", StringComparison.OrdinalIgnoreCase) => "NSIS script",
        var v when string.Equals(v, "InnoSetup", StringComparison.OrdinalIgnoreCase) => "Inno Setup",
        var v => v ?? ""
    };

    public static string View(string? value) => value?.Trim() switch
    {
        "64" => "64-bit",
        "32" => "32-bit",
        var v => v ?? ""
    };

    /// <summary>The value with its first letter in capitals: "delete key" shows as "Delete key".</summary>
    public static string Sentence(string? value)
    {
        var text = value?.Trim() ?? "";
        return text.Length == 0 ? "" : char.ToUpperInvariant(text[0]) + text[1..];
    }

    private static string Normalized(string path) => path.Replace('/', '\\').TrimEnd('\\');

    public static string EntryName(string path)
    {
        var normalized = Normalized(path);
        var slash = normalized.LastIndexOf('\\');
        return slash < 0 ? normalized : normalized[(slash + 1)..];
    }

    public const string TopLevel = "(top level)";

    public static string EntryFolder(string path)
    {
        var normalized = Normalized(path);
        var slash = normalized.LastIndexOf('\\');
        return slash <= 0 ? TopLevel : normalized[..slash];
    }

    public static string Size(long bytes)
    {
        if (bytes < 1024) return bytes == 1 ? "1 byte" : bytes.ToString(CultureInfo.InvariantCulture) + " bytes";
        string[] units = ["KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = -1;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return value.ToString(value < 10 ? "0.0" : "0", CultureInfo.InvariantCulture) + " " + units[unit];
    }

    public static string Count(int count, string singular, string? plural = null) =>
        count == 1 ? $"1 {singular}" : $"{count.ToString("N0", CultureInfo.InvariantCulture)} {plural ?? singular + "s"}";
}
