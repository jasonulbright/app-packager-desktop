using System.Globalization;
using System.Text.RegularExpressions;

namespace ApplicationPackagerEngine.Services;

/// <summary>
/// Builds the argument list of a catalog builder from the form values, one array element per
/// argument, in the order of the entry's parameters: a String or Int parameter with a value
/// gives "-Name" and the value, a switch that is on gives "-Name". Every value is checked
/// first, because Windows PowerShell reads an argument that starts with a dash as a
/// parameter name and cannot take a double quote or a line break through -File intact.
/// </summary>
public static partial class CatalogArguments
{
    [GeneratedRegex("^[A-Za-z0-9]{3}$")]
    private static partial Regex SiteCodeRegex();

    [GeneratedRegex(@"^[A-Za-z0-9]([A-Za-z0-9.\-]{0,251}[A-Za-z0-9])?$")]
    private static partial Regex HostRegex();

    [GeneratedRegex(@"^-?[0-9]{1,9}$")]
    private static partial Regex IntRegex();

    /// <summary>The value the form starts with: the value given, else the entry's default; DetectOnly is on by default.</summary>
    public static string? Effective(CatalogParameter parameter, IReadOnlyDictionary<string, string>? values)
    {
        if (values is not null && values.TryGetValue(parameter.Name, out var value)) return value;
        if (parameter.Name == "DetectOnly" && parameter.Type == "Switch") return "True";
        return parameter.Default;
    }

    public static bool IsOn(string? value) =>
        string.Equals(value, "True", StringComparison.OrdinalIgnoreCase) || value == "1";

    /// <summary>
    /// The arguments for <paramref name="entry"/>, or the plain reasons the values are refused.
    /// Absent values take the defaults of <see cref="Effective"/>.
    /// </summary>
    public static (IReadOnlyList<string> Arguments, IReadOnlyList<string> Errors) Build(CatalogEntry entry, IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(values);
        var errors = new List<string>();
        var arguments = new List<string>();
        foreach (var unknown in values.Keys.Where(k => entry.Parameters.All(p => p.Name != k)))
            errors.Add($"The builder has no parameter named {Shown(unknown)}.");
        var collectionParameters = entry.CollectionParameters;
        foreach (var p in entry.Parameters)
        {
            var value = Effective(p, values);
            switch (p.Type)
            {
                case "Switch":
                    if (value is not null && !IsSwitchText(value)) errors.Add($"{p.Name} must be on or off.");
                    else if (IsOn(value)) arguments.Add("-" + p.Name);
                    break;
                case "String":
                case "Int":
                    if (string.IsNullOrEmpty(value))
                    {
                        if (p.Required) errors.Add($"Enter a value for {p.Name}.");
                        break;
                    }
                    if (CheckValue(p, value, collectionParameters.Contains(p.Name)) is { } reason) { errors.Add(reason); break; }
                    arguments.Add("-" + p.Name);
                    arguments.Add(value);
                    break;
                default:
                    if (!string.IsNullOrEmpty(value)) errors.Add($"{p.Name} has the type {p.Type}, which this page does not pass to a builder.");
                    else if (p.Required) errors.Add($"Enter a value for {p.Name}.");
                    break;
            }
        }
        return (errors.Count == 0 ? arguments : [], errors);
    }

    private static bool IsSwitchText(string value) =>
        value is "1" or "0" || value.Equals("True", StringComparison.OrdinalIgnoreCase) || value.Equals("False", StringComparison.OrdinalIgnoreCase);

    /// <summary>Null when the value can travel to the builder as it is; otherwise a plain reason.</summary>
    public static string? CheckValue(CatalogParameter parameter, string value, bool isCollection)
    {
        var name = parameter.Name;
        if (value.Length > ComplianceRules.MaxParameterLength)
            return $"{name} is longer than {ComplianceRules.MaxParameterLength.ToString(CultureInfo.InvariantCulture)} characters.";
        if (HasControlOrBrokenText(value)) return $"{name} contains a line break or another control character.";
        if (value.Contains('"')) return $"{name} contains a double quote, which a builder cannot receive intact.";
        // PowerShell reads an en dash, em dash and horizontal bar as a parameter dash too.
        if (value[0] is '-' or '–' or '—' or '―')
            return $"{name} starts with a dash, which Windows PowerShell reads as a parameter name.";
        if (value.Trim() != value) return $"{name} starts or ends with a space.";
        if (parameter.Type == "Int" && !IntRegex().IsMatch(value)) return $"{name} must be a whole number.";
        if (name == "SiteCode" && !SiteCodeRegex().IsMatch(value)) return "SiteCode must be the three-character site code.";
        if (name == "SiteServer" && !HostRegex().IsMatch(value)) return "SiteServer must be a computer name, for example cm01.contoso.com.";
        if (isCollection && value.IndexOfAny(['*', '?', '[', ']']) >= 0)
            return $"{name} contains *, ?, [ or ]. The deployment cmdlet reads these as wildcards and could target another collection.";
        return null;
    }

    private static bool HasControlOrBrokenText(string value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (char.IsControl(c) || char.IsLowSurrogate(c)) return true;
            if (char.IsHighSurrogate(c))
            {
                if (i + 1 >= value.Length || !char.IsLowSurrogate(value[i + 1])) return true;
                i++;
            }
        }
        return false;
    }

    private static string Shown(string text) => text.Length > 40 ? text[..40] + "..." : text;
}
