using System.Text.RegularExpressions;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// Finds the test classes that open windows by reading the test sources. A test class that uses WPF, directly or through
/// a helper class of the test project, must use <c>[WindowFact]</c> and <c>[WindowTheory]</c> so that
/// <see cref="WindowTestGuard"/> can skip it. The rule errs to the safe side: a class that mentions WPF at all counts.
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

    // A WPF namespace imported by the file.
    private static readonly Regex WpfUsing = new(@"^\s*using\s+(?:System\.Windows|ApplicationPackagerUI\.Views)", RegexOptions.Multiline | RegexOptions.Compiled);

    // A test attribute at the start of a line: [Fact], [Fact(...)], [Theory], [Theory(...)].
    public static readonly Regex PlainTestAttribute = new(@"^(\s*)\[(Fact|Theory)(?=[\](])", RegexOptions.Multiline | RegexOptions.Compiled);

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

    /// <summary>The names of helper types: they use WPF and hold no test of their own.</summary>
    public static HashSet<string> HelperNames(IEnumerable<string> fileTexts)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var text in fileTexts)
        {
            var wpfFile = WpfUsing.IsMatch(text);
            foreach (var slice in Slices(text))
            {
                var body = text.Substring(slice.Start, slice.Length);
                if (!AnyTestAttribute.IsMatch(body) && (WindowUse.IsMatch(body) || (wpfFile && body.Contains("System.Windows", StringComparison.Ordinal))))
                    names.Add(slice.Name);
            }
        }
        return names;
    }

    /// <summary>The slices of the file that hold tests and use WPF, directly or through one of the helper types.</summary>
    public static IReadOnlyList<TypeSlice> WindowTestClasses(string text, ISet<string> helperNames)
    {
        var wpfFile = WpfUsing.IsMatch(text);
        var result = new List<TypeSlice>();
        foreach (var slice in Slices(text))
        {
            var body = text.Substring(slice.Start, slice.Length);
            if (!AnyTestAttribute.IsMatch(body)) continue;
            var uses = wpfFile || WindowUse.IsMatch(body)
                || helperNames.Any(h => h != slice.Name && Regex.IsMatch(body, @"\b" + Regex.Escape(h) + @"\b"));
            if (uses) result.Add(slice);
        }
        return result;
    }

    /// <summary>The file text with [Fact] and [Theory] of the window test classes replaced by [WindowFact] and [WindowTheory].</summary>
    public static string Tag(string text, ISet<string> helperNames)
    {
        var classes = WindowTestClasses(text, helperNames);
        if (classes.Count == 0) return text;
        var builder = new System.Text.StringBuilder();
        var position = 0;
        foreach (var slice in classes)
        {
            builder.Append(text, position, slice.Start - position);
            builder.Append(PlainTestAttribute.Replace(text.Substring(slice.Start, slice.Length), "$1[Window$2"));
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
}
