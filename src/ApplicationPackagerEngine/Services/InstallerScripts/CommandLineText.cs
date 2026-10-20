namespace ApplicationPackagerEngine.Services.InstallerScripts;

/// <summary>Checks text read from an installer before it is placed inside double quotes on a command line.</summary>
public static class CommandLineText
{
    /// <summary>
    /// True when the path can stand inside double quotes: no double quote (it would end the quoted
    /// part), no control character and no character Windows does not allow in a path.
    /// </summary>
    public static bool IsQuotablePath(string path) =>
        path.Length > 0 && !path.Any(c => c < 0x20 || c is '"' or '<' or '>' or '|' or '*' or '?');
}
