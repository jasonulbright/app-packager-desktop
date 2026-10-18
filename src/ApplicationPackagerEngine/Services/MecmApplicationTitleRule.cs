using System.Text.RegularExpressions;

namespace ApplicationPackagerEngine.Services;

/// <summary>
/// Decides whether a ConfigMgr Application title is a packaged title of a CMName. Check
/// ConfigMgr runs <see cref="PowerShellFunctions"/> inside its Windows PowerShell query and
/// Discover runs <see cref="Matches"/>; both carry the same rule.
/// </summary>
/// <remarks>
/// A packaged title holds the words of CMName in order, with only release details around
/// them: a version, qualifiers in parentheses, an architecture, and after the last word
/// optionally " - " and the rest of the title. The word after the dash starts with a version
/// ("1.2", "v2"), is the word "Latest", is a parenthetical or is a qualifier word, unless the last word of CMName
/// ends in a number and the title continues it as a version right before the dash
/// ("Microsoft .NET 8.0.31 - Windows Server Hosting"). A word of CMName that ends in a number
/// may continue as a version ("18" as "18.5.1.1") or carry an architecture ("7" as "7-x64").
/// Any other word names a different product: "Git" must not find "Git Extensions" or
/// "Git - Extensions", and "Mozilla Firefox" must not find "Mozilla Firefox ESR".
/// Installer method (EXE, MSI) and architecture are qualifier classes. When CMName and the
/// title both carry a class, the normalized values must be equal (amd64 and 64-bit are x64,
/// 32-bit is x86), or "OpenJDK 17 (EXE)" counts as managed by "OpenJDK 17 (MSI) (EXE)". A
/// CMName word that holds only qualifiers of classes the title does not carry is optional.
/// </remarks>
public static partial class MecmApplicationTitleRule
{
    private const string QualifierPatternText = @"(?i)(?<![a-z0-9])(exe|msi|x64|x86|arm64|amd64|32-bit|64-bit)(?![a-z0-9])";
    private const string TokenPatternText = @"\([^)]*\)|\S+";
    private const string ArchitectureSuffix = "(-(x64|x86|arm64|amd64))?";

    [GeneratedRegex(QualifierPatternText)]
    private static partial Regex QualifierRegex();

    [GeneratedRegex(TokenPatternText)]
    private static partial Regex TokenRegex();

    [GeneratedRegex(@"[\s()+,/]")]
    private static partial Regex QualifierSeparatorRegex();

    [GeneratedRegex(@"^(\([^)]*\)|v?\d[\w.+-]*|x64|x86|arm64|amd64)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DetailRegex();

    [GeneratedRegex(@"^(v?\d|\([^)]*\)$|latest$)",RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DashDetailRegex();

    [GeneratedRegex(@"\d$")]
    private static partial Regex EndsInDigitRegex();

    /// <summary>
    /// The Windows PowerShell 5.1 functions Get-MecmApplicationNamePattern and
    /// Test-MecmApplicationTitle, the same rule as <see cref="NamePattern"/> and <see cref="Matches"/>.
    /// </summary>
    public const string PowerShellFunctions = """
        $script:MecmQualifierPattern = '(?i)(?<![a-z0-9])(exe|msi|x64|x86|arm64|amd64|32-bit|64-bit)(?![a-z0-9])'
        $script:MecmTitleTokenPattern = '\([^)]*\)|\S+'
        function Get-MecmTitleQualifier {
            param([string]$Text)
            $classes = @{}
            foreach ($m in [regex]::Matches([string]$Text, $script:MecmQualifierPattern)) {
                $value = $m.Value.ToLowerInvariant()
                $class = 'arch'
                switch ($value) {
                    'exe' { $class = 'method' }
                    'msi' { $class = 'method' }
                    'amd64' { $value = 'x64' }
                    '64-bit' { $value = 'x64' }
                    '32-bit' { $value = 'x86' }
                }
                if (-not $classes.ContainsKey($class)) { $classes[$class] = New-Object 'System.Collections.Generic.SortedSet[string]' -ArgumentList ([System.StringComparer]::Ordinal) }
                [void]$classes[$class].Add($value)
            }
            return $classes
        }
        function Test-MecmQualifierWord {
            param([string]$Word)
            if ($Word -notmatch $script:MecmQualifierPattern) { return $false }
            return (($Word -replace $script:MecmQualifierPattern, '') -replace '[\s()+,/]', '').Length -eq 0
        }
        function Test-MecmQualifierEqual {
            param([string]$Word, [string]$Token)
            $wordQualifier = Get-MecmTitleQualifier $Word
            $tokenQualifier = Get-MecmTitleQualifier $Token
            foreach ($class in $wordQualifier.Keys) {
                if (-not $tokenQualifier.ContainsKey($class) -or -not $tokenQualifier[$class].SetEquals($wordQualifier[$class])) { return $false }
            }
            return $true
        }
        function Get-MecmApplicationNamePattern {
            param([string]$CMName)
            $words = @([regex]::Matches($CMName.Trim(), $script:MecmTitleTokenPattern) | ForEach-Object { $_.Value })
            $kept = @($words | Where-Object { -not (Test-MecmQualifierWord $_) })
            if ($kept.Count -eq 0) { $kept = $words }
            return (($kept -join '*') + '*')
        }
        function Test-MecmApplicationTitle {
            param([string]$CMName, [AllowEmptyString()][string]$Title)
            if ([string]::IsNullOrWhiteSpace($Title) -or [string]::IsNullOrWhiteSpace($CMName)) { return $false }
            $nameQualifier = Get-MecmTitleQualifier $CMName
            $titleQualifier = Get-MecmTitleQualifier $Title
            foreach ($class in $nameQualifier.Keys) {
                if ($titleQualifier.ContainsKey($class) -and -not $titleQualifier[$class].SetEquals($nameQualifier[$class])) { return $false }
            }
            $arch = '(-(x64|x86|arm64|amd64))?'
            $detail = '^(\([^)]*\)|v?\d[\w.+-]*|x64|x86|arm64|amd64)$'
            $allWords = @([regex]::Matches($CMName.Trim(), $script:MecmTitleTokenPattern) | ForEach-Object { $_.Value })
            $words = @($allWords | Where-Object {
                    if (-not (Test-MecmQualifierWord $_)) { return $true }
                    foreach ($class in (Get-MecmTitleQualifier $_).Keys) { if ($titleQualifier.ContainsKey($class)) { return $true } }
                    return $false
                })
            if ($words.Count -eq 0) { $words = $allWords }
            $tokens = @([regex]::Matches($Title, $script:MecmTitleTokenPattern) | ForEach-Object { $_.Value })
            $next = 0
            $versionedAt = -1
            for ($i = 0; $i -lt $tokens.Count; $i++) {
                $token = $tokens[$i]
                if ($next -eq $words.Count -and $token -eq '-') {
                    if ($versionedAt -eq $i - 1) { return $true }
                    if ($i + 1 -ge $tokens.Count) { return $false }
                    return (($tokens[$i + 1] -imatch '^(v?\d|\([^)]*\)$|latest$)') -or (Test-MecmQualifierWord $tokens[$i + 1]))
                }
                if ($next -lt $words.Count) {
                    if ((Test-MecmQualifierWord $words[$next]) -and (Test-MecmQualifierWord $token)) {
                        if (Test-MecmQualifierEqual -Word $words[$next] -Token $token) { $next++; continue }
                    }
                    $continuation = if ($words[$next] -match '\d$') { '(?<version>\.\d[\w.+-]*)?' } else { '' }
                    if ($token -imatch ('^' + [regex]::Escape($words[$next]) + $continuation + $arch + '$')) {
                        $next++
                        if ($next -eq $words.Count -and $Matches.ContainsKey('version')) { $versionedAt = $i }
                        continue
                    }
                }
                if ($token -imatch $detail) { continue }
                return $false
            }
            return ($next -eq $words.Count)
        }
        """;

    private static Dictionary<string, SortedSet<string>> Qualifiers(string text)
    {
        var classes = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        foreach (Match m in QualifierRegex().Matches(text))
        {
            var value = m.Value.ToLowerInvariant();
            var qualifierClass = value is "exe" or "msi" ? "method" : "arch";
            value = value switch { "amd64" or "64-bit" => "x64", "32-bit" => "x86", _ => value };
            if (!classes.TryGetValue(qualifierClass, out var set)) classes[qualifierClass] = set = new SortedSet<string>(StringComparer.Ordinal);
            set.Add(value);
        }
        return classes;
    }

    /// <summary>True when the word holds only qualifiers: "(x64)", "(EXE)", "(x86+x64)", "ARM64".</summary>
    private static bool IsQualifierWord(string word) =>
        QualifierRegex().IsMatch(word) && QualifierSeparatorRegex().Replace(QualifierRegex().Replace(word, ""), "").Length == 0;

    /// <summary>True when the token carries every qualifier class of the word with the same values.</summary>
    private static bool QualifierEqual(string word, string token)
    {
        var tokenQualifier = Qualifiers(token);
        foreach (var (qualifierClass, values) in Qualifiers(word))
            if (!tokenQualifier.TryGetValue(qualifierClass, out var other) || !other.SetEquals(values)) return false;
        return true;
    }

    private static List<string> Tokens(string text) => TokenRegex().Matches(text).Select(m => m.Value).ToList();

    /// <summary>The Get-CMApplication -Name wildcard that returns every title <see cref="Matches"/> can accept.</summary>
    public static string NamePattern(string cmName)
    {
        var words = Tokens(cmName.Trim());
        var kept = words.Where(w => !IsQualifierWord(w)).ToList();
        if (kept.Count == 0) kept = words;
        return string.Join("*", kept) + "*";
    }

    /// <summary>True when <paramref name="title"/> is a packaged title of <paramref name="cmName"/>.</summary>
    public static bool Matches(string? cmName, string? title)
    {
        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(cmName)) return false;
        var nameQualifier = Qualifiers(cmName);
        var titleQualifier = Qualifiers(title);
        foreach (var (qualifierClass, values) in nameQualifier)
            if (titleQualifier.TryGetValue(qualifierClass, out var other) && !other.SetEquals(values)) return false;

        var allWords = Tokens(cmName.Trim());
        var words = allWords.Where(w => !IsQualifierWord(w) || Qualifiers(w).Keys.Any(titleQualifier.ContainsKey)).ToList();
        if (words.Count == 0) words = allWords;

        var tokens = Tokens(title);
        var next = 0;
        var versionedAt = -1;
        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (next == words.Count && token == "-")
                return versionedAt == i - 1 || (i + 1 < tokens.Count && (DashDetailRegex().IsMatch(tokens[i + 1]) || IsQualifierWord(tokens[i + 1])));
            if (next < words.Count)
            {
                var word = words[next];
                if (IsQualifierWord(word) && IsQualifierWord(token) && QualifierEqual(word, token)) { next++; continue; }
                var continuation = EndsInDigitRegex().IsMatch(word) ? @"(?<version>\.\d[\w.+-]*)?" : "";
                var match = Regex.Match(token, "^" + Regex.Escape(word) + continuation + ArchitectureSuffix + "$",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                if (match.Success)
                {
                    next++;
                    if (next == words.Count && match.Groups["version"].Success) versionedAt = i;
                    continue;
                }
            }
            if (DetailRegex().IsMatch(token)) continue;
            return false;
        }
        return next == words.Count;
    }

    /// <summary>
    /// True when a title in <paramref name="titles"/> equals CMName ignoring case or passes
    /// <see cref="Matches"/>. Every title the rule accepts contains the first word of CMName,
    /// so other titles skip the per-title rule.
    /// </summary>
    public static bool IsManaged(string cmName, IEnumerable<string> titles)
    {
        if (string.IsNullOrWhiteSpace(cmName)) return false;
        var trimmed = cmName.Trim();
        var first = trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)[0];
        foreach (var title in titles)
        {
            if (string.IsNullOrWhiteSpace(title)) continue;
            if (string.Equals(trimmed, title.Trim(), StringComparison.OrdinalIgnoreCase)) return true;
            if (title.IndexOf(first, StringComparison.OrdinalIgnoreCase) < 0) continue;
            if (Matches(trimmed, title)) return true;
        }
        return false;
    }
}
