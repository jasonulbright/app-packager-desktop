using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests.Discovery;

/// <summary>
/// The embedded scripts: their text, and their functions run in Windows PowerShell against
/// a stand-in data reader and stand-in CIM cmdlets. No test opens a SQL connection or a
/// CIM session.
/// </summary>
public sealed class DiscoveryScriptTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "apkg-discovery-" + Guid.NewGuid().ToString("N"));

    public DiscoveryScriptTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private static string InventoryText => DiscoveryService.ReadResource(DiscoveryService.InventoryResource);
    private static string SiteText => DiscoveryService.ReadResource(DiscoveryService.SiteDefinitionResource);

    private static string QueryText()
    {
        var m = Regex.Match(InventoryText, "@'\\r?\\n(?<q>.*?)\\r?\\n'@", RegexOptions.Singleline);
        Assert.True(m.Success);
        return m.Groups["q"].Value;
    }

    private string RunPowerShell(string script)
    {
        var path = Path.Combine(_dir, "run-" + Guid.NewGuid().ToString("N") + ".ps1");
        File.WriteAllText(path, script, new UTF8Encoding(true));
        var start = new ProcessStartInfo("powershell.exe")
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true
        };
        foreach (var a in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", path }) start.ArgumentList.Add(a);
        using var process = Process.Start(start)!;
        var stderr = process.StandardError.ReadToEndAsync();
        var stdout = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        Assert.True(stderr.Result.Trim().Length == 0, stderr.Result);
        return stdout;
    }

    [Fact]
    public void TheInventoryQueryReadsOnlyViewsWithBoundParameters()
    {
        var query = QueryText();
        Assert.DoesNotMatch(@"(?i)\b(insert|update|delete|merge|exec|execute|drop|alter|create|truncate|grant)\b", query);
        var targets = Regex.Matches(query, @"(?i)\b(?:from|join)\s+([\w\.\[\]]+)").Select(m => m.Groups[1].Value).Where(t => t != "(").ToList();
        Assert.NotEmpty(targets);
        Assert.All(targets, t => Assert.Matches(@"^(dbo\.)?v_", t));
        Assert.Equal(["dbo.v_Applications", "dbo.v_ConfigurationItems", "dbo.v_GS_ADD_REMOVE_PROGRAMS", "dbo.v_GS_ADD_REMOVE_PROGRAMS_64", "dbo.v_R_System"],
            targets.Distinct().Order(StringComparer.Ordinal));
        Assert.Contains("@Arch32", query);
        Assert.Contains("@Arch64", query);
        Assert.Contains("@AppObjectType", query);
        Assert.DoesNotMatch("'x86'|'x64'|= 31", query);
        Assert.DoesNotMatch(@"(?i)\bName0\b|Netbios|ResourceName|MachineName|User_Name", query);
    }

    [Fact]
    public void ObsoleteAndDecommissionedClientsAreExcludedFromEveryInventoryStatement()
    {
        var statements = QueryText().Split(';').Where(s => Regex.IsMatch(s, "(?i)v_GS_ADD_REMOVE_PROGRAMS")).ToList();
        Assert.Equal(2, statements.Count);
        foreach (var statement in statements)
        {
            var joins = Regex.Matches(statement, @"(?i)JOIN dbo\.v_R_System").Count;
            Assert.True(joins >= 1);
            Assert.Equal(joins, Regex.Matches(statement, @"(?i)ISNULL\(\w+\.Obsolete0, 0\) = 0").Count);
            Assert.Equal(joins, Regex.Matches(statement, @"(?i)ISNULL\(\w+\.Decommissioned0, 0\) = 0").Count);
        }
    }

    [Fact]
    public void TheConnectionUsesWindowsAuthenticationReadOnlyIntentATimeoutAndIsDisposedOnEveryPath()
    {
        var text = InventoryText;
        Assert.Contains("$builder['Integrated Security'] = $true", text);
        Assert.Contains("$builder['ApplicationIntent'] = 'ReadOnly'", text);
        Assert.Contains("$builder['Connect Timeout']", text);
        Assert.Contains("$command.CommandTimeout = [Math]::Max(1, $CommandTimeout)", text);
        Assert.DoesNotMatch("(?i)password|user id|Encrypt", text);
        var final = text[text.IndexOf("    finally {", StringComparison.Ordinal)..];
        Assert.Contains("$reader.Dispose()", final);
        Assert.Contains("$command.Dispose()", final);
        Assert.Contains("$connection.Dispose()", final);
    }

    [Fact]
    public void InputsReachTheScriptsAsEscapedLiterals()
    {
        var hostile = "sql01'; Remove-Item C:\\ -Recurse; '\u2019";
        var inventory = DiscoveryService.BuildInventoryScript(new DiscoveryDatabaseTarget(hostile, "CM_MCM"), 600);
        var first = inventory.Split(Environment.NewLine)[0];
        Assert.Equal("$DiscoveryServer = " + PowerShellLiteral.SingleQuoted(hostile), first);
        var site = DiscoveryService.BuildSiteDefinitionScript(hostile, "MCM");
        Assert.StartsWith("$DiscoveryProvider = " + PowerShellLiteral.SingleQuoted(hostile), site);
        Assert.Throws<ArgumentException>(() => DiscoveryService.BuildSiteDefinitionScript("cm01", "M';"));
    }

    [Fact]
    public void BothScriptsParseInWindowsPowerShell()
    {
        var files = new[] { ("inventory.ps1", InventoryText), ("site.ps1", SiteText) };
        var check = new StringBuilder();
        foreach (var (name, text) in files)
        {
            var path = Path.Combine(_dir, name);
            File.WriteAllText(path, text, new UTF8Encoding(true));
            check.AppendLine($"$t = $null; $e = $null; [void][System.Management.Automation.Language.Parser]::ParseFile('{path}', [ref]$t, [ref]$e); [Console]::Out.WriteLine('{name}:' + @($e).Count)");
        }
        var output = RunPowerShell(check.ToString());
        Assert.Contains("inventory.ps1:0", output);
        Assert.Contains("site.ps1:0", output);
    }

    /// <summary>A stand-in data reader over three result sets, as the inventory query returns them.</summary>
    private const string FakeReader = """
        function New-FakeReader {
            param([object[]]$Sets)
            $reader = New-Object psobject -Property @{ Sets = $Sets; SetIndex = 0; RowIndex = -1 }
            $reader | Add-Member ScriptMethod Read { $this.RowIndex++; return $this.RowIndex -lt @($this.Sets[$this.SetIndex]).Count }
            $reader | Add-Member ScriptMethod NextResult { $this.SetIndex++; $this.RowIndex = -1; return $this.SetIndex -lt $this.Sets.Count }
            $reader | Add-Member ScriptMethod IsDBNull { param($i) $v = @($this.Sets[$this.SetIndex])[$this.RowIndex][$i]; return ($null -eq $v -or $v -is [System.DBNull]) }
            $reader | Add-Member ScriptMethod GetValue { param($i) return @($this.Sets[$this.SetIndex])[$this.RowIndex][$i] }
            return $reader
        }
        """;

    [Fact]
    public void TheInventoryOutputCarriesOrdinalsNeverResourceIdsAndParsesIntoTheSnapshot()
    {
        var script = InventoryText + Environment.NewLine + FakeReader + Environment.NewLine + """
            $rows = @(
                ,@([long]16777301, '7-Zip 26.03 (x64)', 'Igor Pavlov', '26.03', 'x64')
                ,@([long]16777305, '7-Zip 26.03 (x64)', 'Igor Pavlov', '26.03', 'x64')
                ,@([long]16777301, '7-Zip 26.03 (x64)', 'Igor Pavlov', '26.03', 'x64')
                ,@([long]16777309, "Caf$([char]0x00e9) `"Pro`" \ tool", [System.DBNull]::Value, [System.DBNull]::Value, 'x86')
                ,@([System.DBNull]::Value, 'Ghost', 'P', '1', 'x86')
            )
            $summary = @(,@(3, [datetime]'2026-10-15T02:55:30'))
            $apps = @(@(,'Git 2.56.0 (x64)'), @(,'CMTrace Open'))
            Write-DiscoveryInventory -Reader (New-FakeReader -Sets @(,$rows + ,$summary + ,$apps))
            [Console]::Out.WriteLine('{"t":"end"}')
            """;
        var output = RunPowerShell(script);
        Assert.DoesNotContain("1677730", output);
        Assert.All(output.Split('\n', StringSplitOptions.RemoveEmptyEntries), line => Assert.True(line.TrimEnd().All(c => c < 128), line));

        var snapshot = DiscoveryService.ParseInventory(output, new DiscoveryDatabaseTarget("s", "d"), 30);
        Assert.Equal(2, snapshot.Rows.Count);
        var sevenZip = snapshot.Rows[0];
        Assert.Equal(("7-Zip 26.03 (x64)", "Igor Pavlov", "26.03", "x64", 2), (sevenZip.DisplayName, sevenZip.Publisher, sevenZip.Version, sevenZip.Architecture, sevenZip.Clients.Count));
        var cafe = snapshot.Rows[1];
        Assert.Equal(("Café \"Pro\" \\ tool", "", "", "x86", 1), (cafe.DisplayName, cafe.Publisher, cafe.Version, cafe.Architecture, cafe.Clients.Count));
        Assert.Equal(3, snapshot.ClientTotal);
        Assert.Equal(new DateTime(2026, 10, 15, 2, 55, 30), snapshot.InventoryNewest);
        Assert.Equal(["Git 2.56.0 (x64)", "CMTrace Open"], snapshot.Applications);
    }

    [Fact]
    public void AnInventoryFailureWritesNumbersAndFlagsButNoRawText()
    {
        var script = InventoryText + Environment.NewLine + """
            $inner = New-Object System.Exception "Login failed for user 'NT AUTHORITY\ANONYMOUS LOGON'. Cannot generate SSPI context. CONTOSO\jdoe"
            Write-DiscoveryError -Exception (New-Object System.Management.Automation.MethodInvocationException 'Exception calling "Open"', $inner)
            """;
        var output = RunPowerShell(script).Trim();
        Assert.Equal("{\"t\":\"error\",\"stage\":\"sql\",\"numbers\":[],\"sspi\":true,\"anonymous\":true}", output);
    }

    private const string FakeCim = """
        $global:Log = New-Object System.Collections.Generic.List[string]
        $global:FailSession = @()
        $global:FailQuery = @()
        $global:Empty = $false
        function New-CimSessionOption { param([string]$Protocol) return $Protocol }
        function New-CimSession {
            param($ComputerName, $SessionOption, $OperationTimeoutSec, $ErrorAction)
            if ($global:FailSession -contains $SessionOption) { throw 'The RPC server is unavailable. (HRESULT 0x800706BA)' }
            $global:Log.Add("open:${SessionOption}:${ComputerName}")
            $s = New-Object psobject -Property @{ Protocol = $SessionOption }
            $s | Add-Member ScriptMethod Dispose { $global:Log.Add("dispose:" + $this.Protocol) }
            return $s
        }
        function Get-CimInstance {
            param($CimSession, $Namespace, $ClassName, $Filter, $OperationTimeoutSec, $ErrorAction)
            $global:Log.Add("query:" + $CimSession.Protocol + ":" + $Namespace + ":" + $ClassName + ":" + $Filter)
            if ($global:FailQuery -contains $CimSession.Protocol) { throw 'Access is denied. (HRESULT 0x80070005)' }
            if ($global:Empty) { return }
            New-Object psobject -Property @{ SQLServerName = 'CM01.contoso.com'; SQLDatabaseName = 'CM_MCM' }
        }
        """;

    private string RunSite(string setup)
    {
        var script = SiteText + Environment.NewLine + FakeCim + Environment.NewLine + setup + Environment.NewLine + """
            Invoke-DiscoverySiteDefinition -Provider 'CM01.contoso.com' -SiteCode 'MCM'
            [Console]::Out.WriteLine('LOG:' + ($global:Log -join '|'))
            """;
        return RunPowerShell(script);
    }

    [Fact]
    public void TheSiteDefinitionIsReadOverDcomWithoutAWinRmSession()
    {
        var output = RunSite("");
        Assert.Contains("{\"t\":\"site\",\"found\":true,\"server\":\"CM01.contoso.com\",\"database\":\"CM_MCM\",\"via\":\"Dcom\"}", output);
        Assert.Contains("LOG:open:Dcom:CM01.contoso.com|query:Dcom:root\\sms\\site_MCM:SMS_SCI_SiteDefinition:SiteCode='MCM'|dispose:Dcom", output);
        Assert.DoesNotContain("Wsman", output);
    }

    [Fact]
    public void TheSiteDefinitionFallsBackToWinRmWhenTheDcomQueryFails()
    {
        var output = RunSite("$global:FailQuery = @('Dcom')");
        Assert.Contains("\"via\":\"Wsman\"", output);
        Assert.Contains("dispose:Dcom|open:Wsman", output);
        Assert.EndsWith("dispose:Wsman", output.Trim());
    }

    [Fact]
    public void TheSiteDefinitionFallsBackToWinRmWhenTheDcomSessionCannotOpen()
    {
        var output = RunSite("$global:FailSession = @('Dcom')");
        Assert.Contains("\"via\":\"Wsman\"", output);
        Assert.Contains("LOG:open:Wsman", output);
    }

    [Fact]
    public void BothProtocolsFailingWritesOnePlainErrorLine()
    {
        var output = RunSite("$global:FailQuery = @('Dcom', 'Wsman')");
        Assert.Contains("{\"t\":\"error\",\"stage\":\"site\"}", output);
        Assert.DoesNotContain("Access is denied", output);
        Assert.DoesNotContain("HRESULT", output);
        Assert.Contains("dispose:Dcom", output);
        Assert.Contains("dispose:Wsman", output);
    }

    [Fact]
    public void AProviderWithoutTheSiteAnswersNotFoundOverDcomOnly()
    {
        var output = RunSite("$global:Empty = $true");
        Assert.Contains("{\"t\":\"site\",\"found\":false,\"via\":\"Dcom\"}", output);
        Assert.DoesNotContain("Wsman", output);
    }

    [Fact]
    public void EveryShippedPatternMatchesLikePowerShellLike()
    {
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var definitions = Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "Definitions"), "*.json")
            .Select(p => JsonSerializer.Deserialize<AppDefinition>(File.ReadAllText(p), options)!).ToList();
        var patterns = definitions.SelectMany(d => ArpWildcard.Split(d.ArpName).Concat(ArpWildcard.Split(d.ArpVersion))).Distinct().ToList();
        var samples = definitions.SelectMany(d => new[] { d.Application, d.CMName, d.Vendor }).Concat(
        [
            "7-Zip 26.03 (x64 edition)", "Git", "grepWin x64", "gsudo v2.6.1", "Microsoft Windows Desktop Runtime - 8.0.31 (x64)",
            "Microsoft Visual C++ v14 Redistributable (x64) - 14.51.36247", "Microsoft Edge", "Microsoft Edge WebView2 Runtime",
            "PowerShell 7-x64", "7.4.6.0", "17.0.16.8", "21.0.8.9", "Mozilla Firefox (x64 en-US)", "Notepad++ (64-bit x64)",
        ]).Where(s => !string.IsNullOrWhiteSpace(s)).Distinct().ToList();

        var script = new StringBuilder("$o = [System.Management.Automation.WildcardOptions]'IgnoreCase,CultureInvariant'").AppendLine();
        script.AppendLine("$samples = @(" + string.Join(",", samples.Select(PowerShellLiteral.SingleQuoted)) + ")");
        foreach (var pattern in patterns)
            script.AppendLine($"$w = New-Object System.Management.Automation.WildcardPattern -ArgumentList {PowerShellLiteral.SingleQuoted(pattern)}, $o; [Console]::Out.WriteLine((@($samples | ForEach-Object {{ if ($w.IsMatch($_)) {{ '1' }} else {{ '0' }} }}) -join ''))");
        var lines = RunPowerShell(script.ToString()).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(patterns.Count, lines.Length);
        var differ = new List<string>();
        for (var i = 0; i < patterns.Count; i++)
        {
            Assert.True(ArpWildcard.TryCreate(patterns[i], out var regex));
            for (var j = 0; j < samples.Count; j++)
                if ((lines[i][j] == '1') != regex.IsMatch(samples[j])) differ.Add($"'{patterns[i]}' on '{samples[j]}': PowerShell {lines[i][j]}");
        }
        Assert.True(differ.Count == 0, string.Join(Environment.NewLine, differ));
    }
}
