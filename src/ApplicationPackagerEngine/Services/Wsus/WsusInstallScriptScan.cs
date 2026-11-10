using System.Text;
using System.Text.RegularExpressions;

namespace ApplicationPackagerEngine.Services.Wsus;

/// <summary>
/// The steps an install.ps1 takes besides reads, messages, closing a running
/// copy, one installer process, and its exit code. WSUS runs the installer
/// itself, so each step listed here never reaches the client.
/// <para>
/// The scan reads PowerShell tokens without a full parser: strings, here-strings
/// and comments are skipped as a whole, and a command is a bare word at the
/// start of a statement, after a pipe, an assignment, or an opening bracket.
/// The content of a double-quoted string, including a $( ) subexpression in
/// it, is not scanned. Unbalanced brackets or an unterminated string report
/// the script as not parsing.
/// </para>
/// </summary>
public static partial class WsusInstallScriptScan
{
    public const string DoesNotParse = "install.ps1 does not parse";

    private static readonly HashSet<string> Neutral = new(StringComparer.OrdinalIgnoreCase)
    {
        "Join-Path", "Split-Path", "Test-Path", "Resolve-Path", "Get-ChildItem", "Get-Item", "Get-ItemProperty", "Get-Date",
        "Where-Object", "Select-Object", "Sort-Object", "ForEach-Object", "Set-Location", "Push-Location", "Pop-Location",
        "Out-Null", "Write-Output", "Write-Host", "Write-Verbose", "Write-Warning", "Write-Error", "Write-Debug", "Write-Information",
        "Get-Process", "Stop-Process", "Wait-Process", "Start-Sleep"
    };

    /// <summary>The Windows PowerShell 5.1 default aliases of the commands a wrapper uses.</summary>
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["gps"] = "Get-Process", ["ps"] = "Get-Process", ["kill"] = "Stop-Process", ["spps"] = "Stop-Process",
        ["sleep"] = "Start-Sleep", ["saps"] = "Start-Process", ["start"] = "Start-Process",
        ["gci"] = "Get-ChildItem", ["ls"] = "Get-ChildItem", ["dir"] = "Get-ChildItem", ["gi"] = "Get-Item",
        ["gp"] = "Get-ItemProperty", ["rvpa"] = "Resolve-Path", ["cd"] = "Set-Location", ["sl"] = "Set-Location",
        ["chdir"] = "Set-Location", ["pushd"] = "Push-Location", ["popd"] = "Pop-Location",
        ["where"] = "Where-Object", ["?"] = "Where-Object", ["select"] = "Select-Object", ["sort"] = "Sort-Object",
        ["foreach"] = "ForEach-Object", ["%"] = "ForEach-Object", ["echo"] = "Write-Output", ["write"] = "Write-Output",
        ["cp"] = "Copy-Item", ["copy"] = "Copy-Item", ["cpi"] = "Copy-Item", ["mv"] = "Move-Item", ["move"] = "Move-Item",
        ["mi"] = "Move-Item", ["rm"] = "Remove-Item", ["del"] = "Remove-Item", ["erase"] = "Remove-Item", ["rd"] = "Remove-Item",
        ["ri"] = "Remove-Item", ["rmdir"] = "Remove-Item", ["ni"] = "New-Item", ["md"] = "New-Item", ["sp"] = "Set-ItemProperty",
        ["si"] = "Set-Item", ["sc"] = "Set-Content", ["ac"] = "Add-Content", ["iex"] = "Invoke-Expression",
        ["icm"] = "Invoke-Command", ["ii"] = "Invoke-Item", ["wait"] = "Wait-Process"
    };

    private static readonly HashSet<string> Keywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "if", "elseif", "else", "foreach", "for", "while", "do", "until", "switch", "exit", "return", "throw", "break",
        "continue", "try", "catch", "finally", "trap", "param", "function", "filter", "begin", "process", "end", "in"
    };

    private static readonly HashSet<string> PathTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "IO.Path", "System.IO.Path", "string", "System.String"
    };

    // An object from an allowed read, such as a file from Get-Item, can still
    // change the computer through its own methods and properties.
    private static readonly HashSet<string> HarmlessMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        "WaitForExit", "Refresh", "Kill", "CloseMainWindow", "ToString", "Trim", "TrimStart", "TrimEnd", "Replace", "Split",
        "Substring", "ToLower", "ToUpper", "ToLowerInvariant", "ToUpperInvariant", "StartsWith", "EndsWith", "Contains", "IndexOf",
        "Equals", "GetType", "Where", "ForEach"
    };

    // The settings that the wrapper which stops launched processes gives to its start object before the one launch.
    private static readonly HashSet<string> StartSettings = new(StringComparer.OrdinalIgnoreCase)
    {
        "$startInfo.FileName", "$startInfo.Arguments", "$startInfo.UseShellExecute"
    };

    [GeneratedRegex(@"\.(exe|com|cmd|bat)$", RegexOptions.IgnoreCase)]
    private static partial Regex NativeExtension();

    /// <summary>Scans the script at <paramref name="path"/>.</summary>
    public static IReadOnlyList<string> ScanFile(string path) => Scan(File.ReadAllText(path));

    /// <summary>The extra steps, each named once in the order found; empty for a plain installer launch.</summary>
    public static IReadOnlyList<string> Scan(string script)
    {
        if (!Tokenize(script, out var tokens)) return [DoesNotParse];

        var extras = new List<string>();
        var launches = 0;
        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            var previous = i > 0 ? tokens[i - 1] : null;

            if (token.Kind == TokenKind.Operator && token.Text == "&" && IsCommandPosition(previous))
            {
                launches++;
                continue;
            }

            if (token.Kind == TokenKind.Word && IsCommandPosition(previous))
            {
                var name = token.Text;
                if (Keywords.Contains(name) || name.StartsWith('-')) continue;
                if (Aliases.TryGetValue(name, out var resolved)) name = resolved;
                if (Neutral.Contains(name)) continue;
                if (name.Equals("Out-File", StringComparison.OrdinalIgnoreCase) && StatementText(tokens, i).Contains("exitcode.txt", StringComparison.OrdinalIgnoreCase)) continue;
                // The wrapper that stops launched processes builds the start settings of its one installer launch in this object.
                if (name.Equals("New-Object", StringComparison.OrdinalIgnoreCase)
                    && StatementText(tokens, i).Trim().Equals("New-Object System.Diagnostics.ProcessStartInfo", StringComparison.OrdinalIgnoreCase)) continue;
                if (name.Equals("Start-Process", StringComparison.OrdinalIgnoreCase) || NativeExtension().IsMatch(name) || !name.Contains('-'))
                {
                    launches++;
                    continue;
                }
                extras.Add(name);
                continue;
            }

            if (token.Kind == TokenKind.TypeLiteral && i + 2 < tokens.Count
                && tokens[i + 1].Kind == TokenKind.Operator && tokens[i + 1].Text == "::" && tokens[i + 2].Kind == TokenKind.Word)
            {
                var typeName = token.Text;
                if (typeName.Equals("System.Diagnostics.Process", StringComparison.OrdinalIgnoreCase)
                    && tokens[i + 2].Text.Equals("Start", StringComparison.OrdinalIgnoreCase))
                {
                    launches++;
                    i += 2;
                    continue;
                }
                if (!PathTypes.Contains(typeName)) extras.Add($"[{typeName}]::{tokens[i + 2].Text}");
                i += 2;
                continue;
            }

            if (token.Kind == TokenKind.Operator && token.Text == "." && i + 2 < tokens.Count
                && tokens[i + 1].Kind == TokenKind.Word && tokens[i + 2].Kind == TokenKind.Open && tokens[i + 2].Text == "("
                && tokens[i + 2].Adjacent)
            {
                var method = tokens[i + 1].Text;
                if (!HarmlessMethods.Contains(method)) extras.Add($".{method}()");
                i += 1;
                continue;
            }

            if (token.Kind == TokenKind.Variable && IsCommandPosition(previous))
            {
                // $target = ... where the target is a member or an environment variable.
                var end = i;
                while (end + 2 < tokens.Count && tokens[end + 1].Kind == TokenKind.Operator && tokens[end + 1].Text == "."
                       && tokens[end + 2].Kind == TokenKind.Word)
                    end += 2;
                if (end + 1 < tokens.Count && tokens[end + 1].Kind == TokenKind.Assign)
                {
                    var isMember = end > i;
                    var isEnvironment = token.Text.StartsWith("$env:", StringComparison.OrdinalIgnoreCase);
                    if (isMember || isEnvironment)
                    {
                        var target = new StringBuilder(token.Text);
                        for (var k = i + 1; k <= end; k++) target.Append(tokens[k].Text);
                        if (!StartSettings.Contains(target.ToString())) extras.Add($"{target} assignment");
                    }
                }
            }
        }
        if (launches > 1) extras.Add($"{launches} process launches");
        extras.AddRange(ExitCodeMappings(tokens));
        return extras.Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// Each "exit 0" inside an if whose condition compares ExitCode with -eq or -in.
    /// The wrapper then reports success for a code that WSUS, which runs the installer
    /// itself, reports as a failure.
    /// </summary>
    private static IEnumerable<string> ExitCodeMappings(List<Token> tokens)
    {
        for (var i = 0; i + 1 < tokens.Count; i++)
        {
            if (tokens[i].Kind != TokenKind.Word || !tokens[i].Text.Equals("exit", StringComparison.OrdinalIgnoreCase)) continue;
            if (tokens[i + 1].Kind != TokenKind.Number || tokens[i + 1].Text != "0") continue;
            if (i + 2 < tokens.Count && tokens[i + 2].Kind is not (TokenKind.StatementEnd or TokenKind.Close)) continue;

            // The nearest enclosing if statement decides; its else block has no condition.
            var position = i;
            while (true)
            {
                var open = EnclosingOpen(tokens, position);
                if (open < 0) break;
                position = open;
                if (tokens[open].Text != "{" || open == 0) continue;
                var before = tokens[open - 1];
                if (before.Kind == TokenKind.Word && before.Text.Equals("else", StringComparison.OrdinalIgnoreCase)) break;
                if (before.Kind != TokenKind.Close || before.Text != ")") continue;
                var start = MatchingOpen(tokens, open - 1);
                if (start < 1 || tokens[start - 1].Kind != TokenKind.Word
                    || !(tokens[start - 1].Text.Equals("if", StringComparison.OrdinalIgnoreCase)
                         || tokens[start - 1].Text.Equals("elseif", StringComparison.OrdinalIgnoreCase)))
                    continue;

                var condition = tokens.GetRange(start + 1, open - start - 2);
                for (var k = 0; k + 1 < condition.Count; k++)
                {
                    if (condition[k].Kind == TokenKind.Word && condition[k].Text.Equals("ExitCode", StringComparison.OrdinalIgnoreCase)
                        && condition[k + 1].Kind == TokenKind.Word
                        && (condition[k + 1].Text.Equals("-eq", StringComparison.OrdinalIgnoreCase)
                            || condition[k + 1].Text.Equals("-in", StringComparison.OrdinalIgnoreCase)))
                    {
                        yield return $"installer exit code mapped to success ({ConditionText(condition)})";
                        break;
                    }
                }
                break;
            }
        }
    }

    /// <summary>The index of the bracket that encloses <paramref name="index"/>, or -1 at the top level.</summary>
    private static int EnclosingOpen(List<Token> tokens, int index)
    {
        var depth = 0;
        for (var k = index - 1; k >= 0; k--)
        {
            if (tokens[k].Kind == TokenKind.Close) depth++;
            else if (tokens[k].Kind == TokenKind.Open)
            {
                if (depth == 0) return k;
                depth--;
            }
        }
        return -1;
    }

    /// <summary>The index of the bracket that the closing bracket at <paramref name="close"/> ends, or -1.</summary>
    private static int MatchingOpen(List<Token> tokens, int close)
    {
        var depth = 0;
        for (var k = close - 1; k >= 0; k--)
        {
            if (tokens[k].Kind == TokenKind.Close) depth++;
            else if (tokens[k].Kind == TokenKind.Open)
            {
                if (depth == 0) return k;
                depth--;
            }
        }
        return -1;
    }

    private static string ConditionText(List<Token> condition)
    {
        var sb = new StringBuilder();
        for (var k = 0; k < condition.Count; k++)
        {
            var token = condition[k];
            var member = token.Kind == TokenKind.Operator && token.Text is "." or ",";
            var afterMember = k > 0 && condition[k - 1].Kind == TokenKind.Operator && condition[k - 1].Text == ".";
            if (sb.Length > 0 && !member && !afterMember) sb.Append(' ');
            sb.Append(token.Text);
        }
        return sb.ToString();
    }

    private static string StatementText(List<Token> tokens, int index)
    {
        var sb = new StringBuilder();
        for (var k = index; k < tokens.Count && tokens[k].Kind != TokenKind.StatementEnd; k++) sb.Append(tokens[k].Text).Append(' ');
        return sb.ToString();
    }

    /// <summary>True at the start of a statement, after a pipe, an assignment, or an opening bracket.</summary>
    private static bool IsCommandPosition(Token? previous) => previous is null
        || previous.Kind is TokenKind.StatementEnd or TokenKind.Assign or TokenKind.Open or TokenKind.Pipe
        || (previous.Kind == TokenKind.Close && previous.Text == "}" )
        || (previous.Kind == TokenKind.Word && previous.Text.Equals("else", StringComparison.OrdinalIgnoreCase));

    private enum TokenKind { Word, Variable, TypeLiteral, String, Number, Operator, Assign, Pipe, Open, Close, StatementEnd }

    private sealed record Token(TokenKind Kind, string Text, bool Adjacent = false);

    private static bool Tokenize(string text, out List<Token> tokens)
    {
        tokens = [];
        var brackets = new Stack<char>();
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (c == '\r' || c == '\n' || c == ';')
            {
                if (brackets.Count == 0 || brackets.Peek() == '{') AddEnd(tokens);
                i++;
                continue;
            }
            if (c == '`' && i + 1 < text.Length && (text[i + 1] == '\n' || text[i + 1] == '\r'))
            {
                i += 2;
                if (i < text.Length && text[i - 1] == '\r' && text[i] == '\n') i++;
                continue;
            }
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (c == '<' && At(text, i, "<#"))
            {
                var close = text.IndexOf("#>", i + 2, StringComparison.Ordinal);
                if (close < 0) return false;
                i = close + 2;
                continue;
            }
            if (c == '#')
            {
                while (i < text.Length && text[i] != '\n' && text[i] != '\r') i++;
                continue;
            }
            if (c == '@' && i + 1 < text.Length && (text[i + 1] == '\'' || text[i + 1] == '"') && LineEndsAfter(text, i + 2))
            {
                var terminator = "\n" + text[i + 1] + "@";
                var close = text.IndexOf(terminator, i + 2, StringComparison.Ordinal);
                if (close < 0) return false;
                tokens.Add(new Token(TokenKind.String, text[i..(close + terminator.Length)]));
                i = close + terminator.Length;
                continue;
            }
            if (PowerShellLiteral.IsSingleQuote(c))
            {
                var start = i;
                i++;
                while (true)
                {
                    if (i >= text.Length) return false;
                    if (PowerShellLiteral.IsSingleQuote(text[i]))
                    {
                        if (i + 1 < text.Length && PowerShellLiteral.IsSingleQuote(text[i + 1])) { i += 2; continue; }
                        i++;
                        break;
                    }
                    i++;
                }
                tokens.Add(new Token(TokenKind.String, text[start..i]));
                continue;
            }
            if (PowerShellLiteral.DoubleQuoteCharacters.Contains(c))
            {
                var start = i;
                i++;
                while (true)
                {
                    if (i >= text.Length) return false;
                    if (text[i] == '`') { i += 2; continue; }
                    if (PowerShellLiteral.DoubleQuoteCharacters.Contains(text[i]))
                    {
                        if (i + 1 < text.Length && PowerShellLiteral.DoubleQuoteCharacters.Contains(text[i + 1])) { i += 2; continue; }
                        i++;
                        break;
                    }
                    i++;
                }
                tokens.Add(new Token(TokenKind.String, text[start..i]));
                continue;
            }
            if (c == '$')
            {
                if (i + 1 < text.Length && (text[i + 1] == '(' || text[i + 1] == '{'))
                {
                    brackets.Push(text[i + 1] == '(' ? '(' : '{');
                    tokens.Add(new Token(TokenKind.Open, text[i + 1].ToString()));
                    i += 2;
                    continue;
                }
                var start = i;
                i++;
                while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] is '_' or ':' or '?' or '^' or '$')) i++;
                tokens.Add(new Token(TokenKind.Variable, text[start..i]));
                continue;
            }
            if (c == '@' && i + 1 < text.Length && (text[i + 1] == '(' || text[i + 1] == '{'))
            {
                brackets.Push(text[i + 1] == '(' ? '(' : '{');
                tokens.Add(new Token(TokenKind.Open, text[i + 1].ToString()));
                i += 2;
                continue;
            }
            if (c is '(' or '{')
            {
                var adjacent = i > 0 && !char.IsWhiteSpace(text[i - 1]);
                brackets.Push(c);
                tokens.Add(new Token(TokenKind.Open, c.ToString(), adjacent));
                i++;
                continue;
            }
            if (c is ')' or '}')
            {
                var expected = c == ')' ? '(' : '{';
                if (brackets.Count == 0 || brackets.Pop() != expected) return false;
                tokens.Add(new Token(TokenKind.Close, c.ToString()));
                i++;
                continue;
            }
            if (c == '[')
            {
                // A type literal [Name] directly followed by :: or by a value is a cast or a static access.
                var close = text.IndexOf(']', i + 1);
                var inner = close > i ? text[(i + 1)..close] : "";
                if (close > i && Regex.IsMatch(inner, @"^[A-Za-z_][\w.`,\[\] ]*$"))
                {
                    tokens.Add(new Token(TokenKind.TypeLiteral, inner.Trim()));
                    i = close + 1;
                    continue;
                }
                brackets.Push('[');
                tokens.Add(new Token(TokenKind.Open, "["));
                i++;
                continue;
            }
            if (c == ']')
            {
                if (brackets.Count == 0 || brackets.Pop() != '[') return false;
                tokens.Add(new Token(TokenKind.Close, "]"));
                i++;
                continue;
            }
            if (c == '|')
            {
                if (At(text, i, "||")) { tokens.Add(new Token(TokenKind.Operator, "||")); i += 2; continue; }
                tokens.Add(new Token(TokenKind.Pipe, "|"));
                i++;
                continue;
            }
            if (c == ':' && At(text, i, "::")) { tokens.Add(new Token(TokenKind.Operator, "::")); i += 2; continue; }
            if (c == '=' && !At(text, i, "=="))
            {
                tokens.Add(new Token(TokenKind.Assign, "="));
                i++;
                continue;
            }
            if ((c is '+' or '-' or '*' or '/' or '%') && At(text, i + 1, "=") && (i == 0 || text[i - 1] != '-'))
            {
                tokens.Add(new Token(TokenKind.Assign, c + "="));
                i += 2;
                continue;
            }
            if (c == '&' && !At(text, i, "&&")) { tokens.Add(new Token(TokenKind.Operator, "&")); i++; continue; }
            if (c == '.' && i + 1 < text.Length && (char.IsLetter(text[i + 1]) || text[i + 1] == '_')
                && tokens.Count > 0 && i > 0 && !char.IsWhiteSpace(text[i - 1]))
            {
                tokens.Add(new Token(TokenKind.Operator, "."));
                i++;
                continue;
            }
            if (char.IsDigit(c))
            {
                var start = i;
                while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '.')) i++;
                tokens.Add(new Token(TokenKind.Number, text[start..i]));
                continue;
            }
            if (IsWordCharacter(c))
            {
                var start = i;
                var member = tokens.Count > 0 && tokens[^1].Kind == TokenKind.Operator && tokens[^1].Text == ".";
                while (i < text.Length && (member ? char.IsLetterOrDigit(text[i]) || text[i] == '_' : IsWordCharacter(text[i]))) i++;
                tokens.Add(new Token(TokenKind.Word, text[start..i]));
                continue;
            }
            tokens.Add(new Token(TokenKind.Operator, c.ToString()));
            i++;
        }
        return brackets.Count == 0;
    }

    private static bool IsWordCharacter(char c) =>
        char.IsLetterOrDigit(c) || c is '_' or '-' or '.' or '\\' or '/' or ':' or '?' or '%' or '*';

    private static bool At(string text, int index, string value) =>
        index >= 0 && index + value.Length <= text.Length && string.CompareOrdinal(text, index, value, 0, value.Length) == 0;

    private static bool LineEndsAfter(string text, int index)
    {
        while (index < text.Length && text[index] is ' ' or '\t') index++;
        return index >= text.Length || text[index] is '\r' or '\n';
    }

    private static void AddEnd(List<Token> tokens)
    {
        if (tokens.Count > 0 && tokens[^1].Kind != TokenKind.StatementEnd) tokens.Add(new Token(TokenKind.StatementEnd, ";"));
    }
}
