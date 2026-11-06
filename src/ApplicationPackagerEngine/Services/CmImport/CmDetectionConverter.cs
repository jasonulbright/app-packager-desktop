using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ApplicationPackagerEngine.Services.CmImport;

/// <summary>
/// Maps the detection of one deployment type to Microsoft Graph detection rules. A flat And of
/// file, folder and registry comparisons becomes native rules. Anything else (Or, groups, a
/// comparison no native rule carries) becomes one PowerShell script rule that follows the Intune
/// rule: exit 0 with output means installed.
/// </summary>
public static class CmDetectionConverter
{
    private static readonly Dictionary<string, string> GraphOperators = new(StringComparer.Ordinal)
    {
        ["IsEquals"] = "equal", ["NotEquals"] = "notEqual", ["GreaterThan"] = "greaterThan", ["GreaterEquals"] = "greaterThanOrEqual",
        ["LessThan"] = "lessThan", ["LessEquals"] = "lessThanOrEqual"
    };

    public static IReadOnlyList<DetectionLeaf> Leaves(DetectionNode? tree) => tree switch
    {
        null => [],
        DetectionLeaf leaf => [leaf],
        DetectionGroup group => group.Children.SelectMany(Leaves).ToList(),
        _ => []
    };

    /// <summary>True when the tree is one leaf or a chain of And groups.</summary>
    public static bool IsFlatAnd(DetectionNode tree) => tree switch
    {
        DetectionLeaf => true,
        DetectionGroup g => g.Operator == "And" && g.Children.All(IsFlatAnd),
        _ => false
    };

    private static string HiveRoot(string hive) =>
        Regex.IsMatch(hive, "CURRENT_USER|CurrentUser") ? @"HKEY_CURRENT_USER\" : @"HKEY_LOCAL_MACHINE\";

    /// <summary>One leaf as a Graph rule, or null when no native rule fits.</summary>
    public static JsonObject? NativeRule(DetectionLeaf leaf)
    {
        if (!leaf.Supported) return null;

        if (leaf.Source is "File" or "Folder")
        {
            if (leaf.Name.IndexOfAny(['*', '?']) >= 0) return null;
            if (leaf.Property == "Exists" && leaf.Expected == "True")
                return FileRule(leaf, "exists", "notConfigured", null);
            if (leaf.Source == "File" && leaf.Property == "Version" && GraphOperators.TryGetValue(leaf.Operator, out var op))
                return FileRule(leaf, "version", op, leaf.Expected);
            return null;
        }
        if (leaf.Source == "RegistryKey" && leaf.Property == "Exists" && leaf.Expected == "True")
            return RegistryRule(leaf, null, "exists", "notConfigured", null);
        if (leaf.Source == "Registry" && leaf.Property == "Value" && GraphOperators.TryGetValue(leaf.Operator, out var regOp))
        {
            var type = leaf.DataType switch { "Int64" => "integer", "Version" => "version", "String" => "string", _ => "" };
            if (type.Length == 0) return null;
            return RegistryRule(leaf, leaf.Name, type, regOp, leaf.Expected);
        }
        return null;
    }

    private static JsonObject FileRule(DetectionLeaf leaf, string operation, string op, string? value) => new()
    {
        ["@odata.type"] = "#microsoft.graph.win32LobAppFileSystemRule",
        ["ruleType"] = "detection",
        ["path"] = leaf.Path,
        ["fileOrFolderName"] = leaf.Name,
        ["check32BitOn64System"] = !leaf.Is64Bit,
        ["operationType"] = operation,
        ["operator"] = op,
        ["comparisonValue"] = value
    };

    private static JsonObject RegistryRule(DetectionLeaf leaf, string? valueName, string operation, string op, string? value) => new()
    {
        ["@odata.type"] = "#microsoft.graph.win32LobAppRegistryRule",
        ["ruleType"] = "detection",
        ["check32BitOn64System"] = !leaf.Is64Bit,
        ["keyPath"] = HiveRoot(leaf.Hive) + leaf.Key,
        ["valueName"] = valueName,
        ["operationType"] = operation,
        ["operator"] = op,
        ["comparisonValue"] = value
    };

    // ---- the script form ------------------------------------------------

    /// <summary>The helper functions the detection script carries, evaluated on the device.</summary>
    internal const string DetectionHelpers = """
        function Expand-ImpPath([string]$Path, [bool]$Is64Bit) {
            $p = $Path
            if (-not $Is64Bit -and [Environment]::Is64BitOperatingSystem) {
                $p = $p -replace '(?i)%ProgramFiles%', '%ProgramFiles(x86)%'
                $p = $p -replace '(?i)%CommonProgramFiles%', '%CommonProgramFiles(x86)%'
            }
            $p = [Environment]::ExpandEnvironmentVariables($p)
            if (-not $Is64Bit -and [Environment]::Is64BitOperatingSystem) {
                $system = Join-Path $env:SystemRoot 'System32'
                if ($p.StartsWith($system, [StringComparison]::OrdinalIgnoreCase)) { $p = (Join-Path $env:SystemRoot 'SysWOW64') + $p.Substring($system.Length) }
            }
            return $p
        }
        function Get-ImpRegistryValue([string]$Hive, [bool]$Is64Bit, [string]$Key, [string]$Name) {
            $hiveName = if ($Hive -match 'CURRENT_USER|CurrentUser') { 'CurrentUser' } else { 'LocalMachine' }
            $view = if ($Is64Bit -or -not [Environment]::Is64BitOperatingSystem) { [Microsoft.Win32.RegistryView]::Registry64 } else { [Microsoft.Win32.RegistryView]::Registry32 }
            if (-not [Environment]::Is64BitOperatingSystem) { $view = [Microsoft.Win32.RegistryView]::Default }
            $base = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]$hiveName, $view)
            try {
                $subKey = $base.OpenSubKey($Key)
                if ($null -eq $subKey) { return [pscustomobject]@{ KeyExists = $false; Value = $null } }
                try { return [pscustomobject]@{ KeyExists = $true; Value = $(if ($Name) { $subKey.GetValue($Name) } else { $null }) } }
                finally { $subKey.Close() }
            }
            finally { $base.Close() }
        }
        function ConvertTo-ImpVersion($Value) {
            $text = ([string]$Value) -replace ',\s*', '.' -replace '[^\d.].*$', ''
            $parsed = $null
            if ([version]::TryParse($text, [ref]$parsed)) { return $parsed }
            return $null
        }
        function Compare-ImpValue($Actual, $Expected, [string]$Type, [string]$Operator) {
            if ($null -eq $Actual) { return $false }
            switch ($Type) {
                'Version' { $a = ConvertTo-ImpVersion $Actual; $e = ConvertTo-ImpVersion $Expected; if ($null -eq $a -or $null -eq $e) { return $false }; $c = $a.CompareTo($e) }
                'Int64'   { $c = ([int64]$Actual).CompareTo([int64]$Expected) }
                'DateTime' { $c = ([datetime]$Actual).CompareTo([datetime]$Expected) }
                default   {
                    $a = [string]$Actual; $e = [string]$Expected
                    switch ($Operator) {
                        'BeginsWith' { return $a.StartsWith($e, [StringComparison]::OrdinalIgnoreCase) }
                        'EndsWith' { return $a.EndsWith($e, [StringComparison]::OrdinalIgnoreCase) }
                        'Contains' { return ($a.IndexOf($e, [StringComparison]::OrdinalIgnoreCase) -ge 0) }
                        'NotBeginsWith' { return -not $a.StartsWith($e, [StringComparison]::OrdinalIgnoreCase) }
                        'NotEndsWith' { return -not $a.EndsWith($e, [StringComparison]::OrdinalIgnoreCase) }
                        'NotContains' { return ($a.IndexOf($e, [StringComparison]::OrdinalIgnoreCase) -lt 0) }
                    }
                    $c = [string]::Compare($a, $e, $true)
                }
            }
            switch ($Operator) {
                'IsEquals' { return ($c -eq 0) } 'NotEquals' { return ($c -ne 0) } 'GreaterThan' { return ($c -gt 0) }
                'GreaterEquals' { return ($c -ge 0) } 'LessThan' { return ($c -lt 0) } 'LessEquals' { return ($c -le 0) }
            }
            return $false
        }
        function Test-ImpLeaf([string]$Source, [string]$Property, [string]$Operator, [string]$Expected, [string]$DataType, [string]$Path, [string]$Name, [string]$Hive, [string]$Key, [bool]$Is64Bit) {
            switch ($Source) {
                { $_ -eq 'File' -or $_ -eq 'Folder' } {
                    $directory = Expand-ImpPath $Path $Is64Bit
                    $item = $null
                    if (Test-Path -LiteralPath $directory -PathType Container) {
                        $kind = if ($Source -eq 'File') { @{ File = $true } } else { @{ Directory = $true } }
                        $item = Get-ChildItem -LiteralPath $directory -Filter $Name -Force -ErrorAction SilentlyContinue @kind | Select-Object -First 1
                    }
                    if ($Property -eq 'Exists') { return (($null -ne $item) -eq ($Expected -eq 'True')) }
                    if ($null -eq $item) { return $false }
                    switch ($Property) {
                        'Version' { return (Compare-ImpValue $item.VersionInfo.FileVersion $Expected 'Version' $Operator) }
                        'DateModified' { return (Compare-ImpValue $item.LastWriteTime $Expected 'DateTime' $Operator) }
                        'DateCreated' { return (Compare-ImpValue $item.CreationTime $Expected 'DateTime' $Operator) }
                        'Size' { return (Compare-ImpValue $item.Length $Expected 'Int64' $Operator) }
                    }
                    return $false
                }
                'RegistryKey' {
                    $found = Get-ImpRegistryValue $Hive $Is64Bit $Key ''
                    return ($found.KeyExists -eq ($Expected -eq 'True'))
                }
                'Registry' {
                    $found = Get-ImpRegistryValue $Hive $Is64Bit $Key $Name
                    return (Compare-ImpValue $found.Value $Expected $DataType $Operator)
                }
            }
            return $false
        }
        """;

    /// <summary>A value as the inside of a PowerShell single-quoted string; every quote character is doubled.</summary>
    internal static string SingleQuoted(string? value) =>
        Regex.Replace(value ?? "", "['‘’‚‛]", "$0$0");

    private static string ScriptExpression(DetectionNode tree)
    {
        if (tree is DetectionGroup group)
        {
            var joiner = group.Operator == "Or" ? " -or " : " -and ";
            return "(" + string.Join(joiner, group.Children.Select(ScriptExpression)) + ")";
        }
        var leaf = (DetectionLeaf)tree;
        if (!leaf.Supported) throw new InvalidOperationException(leaf.Reason);
        static string Q(string v) => "'" + SingleQuoted(v) + "'";
        return $"(Test-ImpLeaf -Source {Q(leaf.Source)} -Property {Q(leaf.Property)} -Operator {Q(leaf.Operator)} -Expected {Q(leaf.Expected)} " +
               $"-DataType {Q(leaf.DataType)} -Path {Q(leaf.Path)} -Name {Q(leaf.Name)} -Hive {Q(leaf.Hive)} -Key {Q(leaf.Key)} " +
               $"-Is64Bit ${(leaf.Is64Bit ? "true" : "false")})";
    }

    /// <summary>
    /// The detection tree as one PowerShell script. Intune ANDs every detection rule and has no
    /// grouping, so an expression with Or, nested groups or a comparison that no Graph rule
    /// carries runs as one script.
    /// </summary>
    public static string ExpressionScript(DetectionNode tree)
    {
        var expression = ScriptExpression(tree);
        return string.Join("\r\n",
            "$ErrorActionPreference = 'Stop'",
            DetectionHelpers.Replace("\r\n", "\n").Replace("\n", "\r\n"),
            "try { $detected = " + expression + " } catch { $detected = $false }",
            "if ($detected) { Write-Output 'Detected'; exit 0 }",
            "exit 1");
    }

    public static JsonObject ScriptRule(string text, string ruleType, string displayName = "", bool runAs32Bit = false)
    {
        var rule = new JsonObject
        {
            ["@odata.type"] = "#microsoft.graph.win32LobAppPowerShellScriptRule",
            ["ruleType"] = ruleType,
            ["enforceSignatureCheck"] = false,
            ["runAs32Bit"] = runAs32Bit,
            ["scriptContent"] = System.Convert.ToBase64String(new UTF8Encoding(false).GetBytes(text)),
            ["operationType"] = "notConfigured",
            ["operator"] = "notConfigured"
        };
        if (ruleType == "requirement")
        {
            rule["displayName"] = displayName;
            rule["runAsAccount"] = "system";
            rule["operationType"] = "boolean";
            rule["operator"] = "equal";
            rule["comparisonValue"] = "true";
        }
        return rule;
    }

    public static CmDetectionResult Convert(CmDetection detection, bool nativeOnly = false)
    {
        static CmDetectionResult Blocked(string mode, string code, string message) =>
            new([], mode, [new CmFinding("Blocking", code, message)]);

        switch (detection.Method)
        {
            case "ProductCode":
                return new CmDetectionResult([new JsonObject
                {
                    ["@odata.type"] = "#microsoft.graph.win32LobAppProductCodeRule",
                    ["ruleType"] = "detection",
                    ["productCode"] = detection.ProductCode,
                    ["productVersionOperator"] = "notConfigured",
                    ["productVersion"] = null
                }], "ProductCode", []);

            case "Script":
                if (detection.ScriptLanguage.Length > 0 && detection.ScriptLanguage != "PowerShell")
                    return Blocked("Script", "DetectionLanguageNotSupported",
                        $"The detection script language '{detection.ScriptLanguage}' is not supported. Intune runs PowerShell detection scripts only.");
                return new CmDetectionResult([ScriptRule(detection.ScriptText, "detection", runAs32Bit: detection.RunAs32Bit)], "Script", []);

            case "Enhanced":
            {
                if (detection.Tree is null) return Blocked("Native", "DetectionMissing", "The deployment type stores an empty detection expression.");
                var leaves = Leaves(detection.Tree);
                if (leaves.Count == 0) return Blocked("Native", "DetectionMissing", "The detection expression has no conditions.");
                var unsupported = leaves.FirstOrDefault(l => !l.Supported);
                if (unsupported is not null) return Blocked("Native", "DetectionNotConvertible", unsupported.Reason);

                if (IsFlatAnd(detection.Tree))
                {
                    var native = leaves.Select(NativeRule).ToList();
                    if (native.All(r => r is not null))
                        return new CmDetectionResult(native.Select(r => r!).ToList(), "Native", []);
                }
                if (nativeOnly)
                    return Blocked("ExpressionScript", "DetectionNeedsScript",
                        "The detection uses Or, a group, or a comparison that no native Intune rule carries, and script conversion is turned off.");

                string text;
                try { text = ExpressionScript(detection.Tree); }
                catch (InvalidOperationException ex) { return Blocked("ExpressionScript", "DetectionNotConvertible", ex.Message); }
                return new CmDetectionResult([ScriptRule(text, "detection")], "ExpressionScript",
                    [new CmFinding("Review", "DetectionConvertedToScript",
                        "Intune joins all detection rules with AND and has no groups. The stored detection expression runs as one PowerShell script, with the same And, Or and grouping.")]);
            }

            default:
                return Blocked("None", "DetectionMissing", $"The deployment type has no convertible detection (method '{detection.Method}').");
        }
    }
}
