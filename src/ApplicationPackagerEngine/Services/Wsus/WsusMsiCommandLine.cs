using System.Text;
using System.Text.RegularExpressions;

namespace ApplicationPackagerEngine.Services.Wsus;

/// <summary>
/// Reduces an msiexec argument string to the property assignments WSUS passes
/// to Windows Installer. WSUS runs the package itself, quietly: msiexec
/// switches have no meaning there, and a switch on the client fails the
/// install with 0x80070667.
/// </summary>
public static partial class WsusMsiCommandLine
{
    [GeneratedRegex(@"^[/-](norestart|promptrestart)$", RegexOptions.IgnoreCase)]
    private static partial Regex RestartSwitch();

    [GeneratedRegex(@"^[/-](l[a-z\*\+!]*|log)$", RegexOptions.IgnoreCase)]
    private static partial Regex LogSwitch();

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_.]*=")]
    private static partial Regex PropertyAssignment();

    /// <summary>
    /// Keeps PROPERTY=value tokens, drops every /switch and the file after
    /// /l* or /log, and turns /norestart or /promptrestart into
    /// REBOOT=ReallySuppress unless a REBOOT property is already present.
    /// </summary>
    public static string Convert(string? installArgs)
    {
        var properties = new List<string>();
        var suppressReboot = false;
        var skipNext = false;
        foreach (var token in Split(installArgs))
        {
            if (skipNext) { skipNext = false; continue; }
            if (token.StartsWith('/') || token.StartsWith('-'))
            {
                if (RestartSwitch().IsMatch(token)) suppressReboot = true;
                if (LogSwitch().IsMatch(token)) skipNext = true;
                continue;
            }
            if (PropertyAssignment().IsMatch(token)) properties.Add(token);
        }
        if (suppressReboot && !properties.Any(p => p.StartsWith("REBOOT=", StringComparison.OrdinalIgnoreCase)))
            properties.Add("REBOOT=ReallySuppress");
        return string.Join(" ", properties);
    }

    /// <summary>
    /// Splits an argument string the way the Windows command-line parser groups
    /// it: whitespace separates, double quotes group, and a quoted segment stays
    /// attached to the text before it (PROPERTY="a b"). Quotes are kept.
    /// </summary>
    public static IReadOnlyList<string> Split(string? commandLine)
    {
        var tokens = new List<string>();
        if (string.IsNullOrWhiteSpace(commandLine)) return tokens;
        var current = new StringBuilder();
        var quoted = false;
        foreach (var character in commandLine)
        {
            if (character == '"') { quoted = !quoted; current.Append(character); continue; }
            if (char.IsWhiteSpace(character) && !quoted)
            {
                if (current.Length > 0) { tokens.Add(current.ToString()); current.Clear(); }
                continue;
            }
            current.Append(character);
        }
        if (current.Length > 0) tokens.Add(current.ToString());
        return tokens;
    }
}
