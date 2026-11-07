using System.Text;
using System.Text.RegularExpressions;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// Finds the test classes that open windows by reading the test sources. A test class that uses WPF, directly or through
/// a helper class of the test project, must use <c>[WindowFact]</c> and <c>[WindowTheory]</c> so that
/// <see cref="WindowTestGuard"/> can skip it. The rule errs to the safe side: a class that uses WPF in code counts.
/// Text in comments and in string literals does not count, so a test that only reads source files is not a window test.
/// </summary>
internal static class WindowTestSources
{
    // A type at column 0 (the files use file-scoped namespaces).
    private static readonly Regex TypeStart = new(
        @"^(?:public |internal |file )?(?:sealed |static |abstract |partial |readonly )*(?:class|record|struct) (\w+)",
        RegexOptions.Multiline);

    // What opens a window, renders a control or starts the WPF thread. The ConfigMgr maintenance window model
    // is not a WPF window.
    private static readonly Regex WindowUse = new(
        @"StaTestHelper|new\s+(?!Maintenance)\w*Window\b|Application\.Current|RenderTargetBitmap|ShowDialog\(|\.Show\(\)|STAThread|SetApartmentState|XamlReader|\bUserControl\b|\bFrameworkElement\b",
        RegexOptions.Compiled);

    // A WPF namespace imported by the file, also through an alias (using Point = System.Windows.Point;).
    private static readonly Regex WpfUsing = new(
        @"^\s*using\s+(?:static\s+)?(?:\w+\s*=\s*)?(?:global::)?(?:System\.Windows|ApplicationPackagerUI\.Views)",
        RegexOptions.Multiline | RegexOptions.Compiled);

    // A test attribute at the start of a line: [Fact], [Fact(...)], [Theory], [Theory(...)].
    public static readonly Regex PlainTestAttribute = new(@"^(\s*)\[(Fact|Theory)(?=[\](])", RegexOptions.Multiline | RegexOptions.Compiled);

    private static readonly Regex WindowTestAttribute = new(@"^(\s*)\[Window(Fact|Theory)(?=[\](])", RegexOptions.Multiline | RegexOptions.Compiled);

    private static readonly Regex AnyTestAttribute = new(@"^\s*\[(?:Window)?(?:Fact|Theory)(?=[\](])", RegexOptions.Multiline | RegexOptions.Compiled);

    public sealed record TypeSlice(string Name, int Start, int Length);

    public static IReadOnlyList<TypeSlice> Slices(string text)
    {
        var starts = TypeStart.Matches(text);
        var slices = new List<TypeSlice>(starts.Count);
        for (var i = 0; i < starts.Count; i++)
        {
            var end = i + 1 < starts.Count ? starts[i + 1].Index : text.Length;
            slices.Add(new TypeSlice(starts[i].Groups[1].Value, starts[i].Index, end - starts[i].Index));
        }
        return slices;
    }

    /// <summary>
    /// The code without comments and without the text of string and character literals (the quotes stay), so that a
    /// word in a message or in a pattern is not taken for a use. Newlines are kept.
    /// </summary>
    public static string Code(string text)
    {
        var result = new StringBuilder(text.Length);
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            var next = i + 1 < text.Length ? text[i + 1] : '\0';
            if (c == '/' && next == '/')
            {
                while (i < text.Length && text[i] != '\n') i++;
            }
            else if (c == '/' && next == '*')
            {
                i += 2;
                while (i + 1 < text.Length && !(text[i] == '*' && text[i + 1] == '/')) { if (text[i] == '\n') result.Append('\n'); i++; }
                i += 2;
            }
            else if (c == '"' && next == '"' && i + 2 < text.Length && text[i + 2] == '"')
            {
                // Raw string literal: three or more quotes to the same number of quotes.
                var quotes = 0;
                while (i + quotes < text.Length && text[i + quotes] == '"') quotes++;
                var closing = new string('"', quotes);
                var end = text.IndexOf(closing, i + quotes, StringComparison.Ordinal);
                var stop = end < 0 ? text.Length : end + quotes;
                for (var k = i; k < stop; k++) if (text[k] == '\n') result.Append('\n');
                result.Append("\"\"");
                i = stop;
            }
            else if (c == '"' || ((c == '@' || c == '$') && (next == '"' || (next is '@' or '$' && i + 2 < text.Length && text[i + 2] == '"'))))
            {
                var verbatim = false;
                while (text[i] != '"') { if (text[i] == '@') verbatim = true; i++; }
                i++;
                while (i < text.Length)
                {
                    if (text[i] == '\n') result.Append('\n');
                    // A regular string ends at the end of its line: a hole of an interpolated string can hold quotes of its own.
                    if (!verbatim && text[i] == '\n') break;
                    if (verbatim && text[i] == '"' && i + 1 < text.Length && text[i + 1] == '"') { i += 2; continue; }
                    if (!verbatim && text[i] == '\\') { i += 2; continue; }
                    if (text[i] == '"') { i++; break; }
                    i++;
                }
                result.Append("\"\"");
            }
            else if (c == '\'' && (next == '\\' || (i + 2 < text.Length && text[i + 2] == '\'')))
            {
                i++;
                while (i < text.Length && text[i] != '\'') { if (text[i] == '\\') i++; i++; }
                i++;
                result.Append("''");
            }
            else
            {
                result.Append(c);
                i++;
            }
        }
        return result.ToString();
    }

    /// <summary>The names of helper types: they use WPF in code and hold no test of their own.</summary>
    public static HashSet<string> HelperNames(IEnumerable<string> fileTexts)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var text in fileTexts)
        {
            var code = Code(text);
            var wpfFile = WpfUsing.IsMatch(code);
            foreach (var slice in Slices(text))
            {
                var body = text.Substring(slice.Start, slice.Length);
                if (AnyTestAttribute.IsMatch(body)) continue;
                var bodyCode = Code(body);
                if (WindowUse.IsMatch(bodyCode) || (wpfFile && bodyCode.Contains("System.Windows", StringComparison.Ordinal)))
                    names.Add(slice.Name);
            }
        }
        return names;
    }

    /// <summary>The slices of the file that hold tests and use WPF in code, directly or through one of the helper types.</summary>
    public static IReadOnlyList<TypeSlice> WindowTestClasses(string text, ISet<string> helperNames)
    {
        var wpfFile = WpfUsing.IsMatch(Code(text));
        var result = new List<TypeSlice>();
        foreach (var slice in Slices(text))
        {
            var body = text.Substring(slice.Start, slice.Length);
            if (!AnyTestAttribute.IsMatch(body)) continue;
            var bodyCode = Code(body);
            var uses = wpfFile || WindowUse.IsMatch(bodyCode)
                || helperNames.Any(h => h != slice.Name && Regex.IsMatch(bodyCode, @"\b" + Regex.Escape(h) + @"\b"));
            if (uses) result.Add(slice);
        }
        return result;
    }

    /// <summary>
    /// The file text in which every test class that uses WPF has [WindowFact] and [WindowTheory], and every other test class has
    /// [Fact] and [Theory].
    /// </summary>
    public static string Normalize(string text, ISet<string> helperNames)
    {
        var window = WindowTestClasses(text, helperNames).Select(s => s.Start).ToHashSet();
        var builder = new StringBuilder();
        var position = 0;
        foreach (var slice in Slices(text))
        {
            var body = text.Substring(slice.Start, slice.Length);
            if (!AnyTestAttribute.IsMatch(body)) continue;
            builder.Append(text, position, slice.Start - position);
            builder.Append(window.Contains(slice.Start)
                ? PlainTestAttribute.Replace(body, "$1[Window$2")
                : WindowTestAttribute.Replace(body, "$1[$2"));
            position = slice.Start + slice.Length;
        }
        builder.Append(text, position, text.Length - position);
        return builder.ToString();
    }

    /// <summary>The test classes of the file that use WPF and still carry a plain [Fact] or [Theory].</summary>
    public static IReadOnlyList<string> Untagged(string text, ISet<string> helperNames) =>
        WindowTestClasses(text, helperNames)
            .Where(s => PlainTestAttribute.IsMatch(text.Substring(s.Start, s.Length)))
            .Select(s => s.Name)
            .ToList();

    /// <summary>The test classes of the file that carry [WindowFact] or [WindowTheory] without any use of WPF.</summary>
    public static IReadOnlyList<string> OverTagged(string text, ISet<string> helperNames)
    {
        var window = WindowTestClasses(text, helperNames).Select(s => s.Start).ToHashSet();
        return Slices(text)
            .Where(s => !window.Contains(s.Start) && WindowTestAttribute.IsMatch(text.Substring(s.Start, s.Length)))
            .Select(s => s.Name)
            .ToList();
    }
}
