using System.Text;
using ApplicationPackagerEngine.Services.ConfigMgrSession;

namespace ApplicationPackagerEngine.Services.Logging;

/// <summary>
/// The text that a log line holds for an exception, so that a message such as "the log has the details" is true: the type, the message
/// and the HResult of every exception of the chain (the inner exceptions and the exceptions of an aggregate), the failure kind of a
/// ConfigMgr session failure, and the first stack frames. The log masks secrets in the text when it writes the line.
/// </summary>
public static partial class ExceptionText
{
    public const int MaxMessage = 2000;
    public const int MaxFrames = 6;
    private const int MaxDepth = 8;

    [System.Text.RegularExpressions.GeneratedRegex(@"At (line:[0-9]+|[A-Za-z]:[^']*?:[0-9]+) char:[0-9]+.*", System.Text.RegularExpressions.RegexOptions.Singleline)]
    private static partial System.Text.RegularExpressions.Regex ScriptPosition();

    public static string Describe(Exception? ex)
    {
        if (ex is null) return "(no exception)";
        var text = new StringBuilder();
        Append(text, ex, 0);
        var stack = (ex.StackTrace ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(MaxFrames).ToList();
        if (stack.Count > 0) text.Append(" Stack: ").Append(string.Join(" <- ", stack)).Append('.');
        // The log masks the secrets it knows; the text is masked here as well, so a logger that does not mask still holds no password.
        return SecretMask.Apply(text.ToString());
    }

    private static void Append(StringBuilder text, Exception ex, int depth)
    {
        if (depth > 0) text.Append(" Inner exception: ");
        text.Append(ex.GetType().FullName);
        // PowerShell puts the line of the script that failed into the message; a script can hold a password, so that part is left out.
        var message = ScriptPosition().Replace(ex.Message ?? "", "(script position left out)");
        text.Append(": ").Append(message.Length > MaxMessage ? message[..MaxMessage] + "..." : message);
        if (ex is ConfigMgrSessionException session) text.Append(" (failure ").Append(session.Failure).Append(')');
        if (ex.HResult != 0) text.Append(" (HResult 0x").Append(ex.HResult.ToString("X8", System.Globalization.CultureInfo.InvariantCulture)).Append(')');
        if (depth >= MaxDepth) return;
        if (ex is AggregateException aggregate)
        {
            foreach (var inner in aggregate.InnerExceptions) Append(text, inner, depth + 1);
            return;
        }
        if (ex.InnerException is { } next) Append(text, next, depth + 1);
    }
}
