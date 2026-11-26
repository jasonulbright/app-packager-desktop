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
        Assert.Equal(["dbo.v_Applications", "dbo.v_ConfigurationItems", "dbo.v_GS_ADD_REMOVE_PROGRAMS", "dbo.v_GS_ADD_REMOVE_PROGRAMS_64"],
            targets.Distinct().Order(StringComparer.Ordinal));
        Assert.Contains("@Arch32", query);
        Assert.Contains("@Arch64", query);
        Assert.Contains("@AppObjectType", query);
        Assert.DoesNotMatch("'x86'|'x64'|= 31", query);
        Assert.DoesNotMatch(@"(?i)\bName0\b|Netbios|ResourceName|MachineName|User_Name", query);
    }

    [Fact]
    public void TheQueryReadsOnlyNamesVersionsAndViewsAndNoClientKeyOrPublisher()
    {
        var query = QueryText();
        foreach (var select in Regex.Matches(query, @"(?is)SELECT DISTINCT\s+(.*?)\s+FROM\s+dbo\.v_GS_ADD_REMOVE_PROGRAMS").Select(m => m.Groups[1].Value))
        {
            Assert.DoesNotContain("ResourceID", select);
            Assert.DoesNotContain("Publisher", select);
            Assert.DoesNotContain("TimeStamp", select);
        }
        Assert.DoesNotContain("COUNT(", query, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Publisher0", query);
    }

    [Fact]
    public void TheInventoryQueryIsTheQueryOfThePowerShellApp()
    {
        // Q-134: the query that returns the applications at the production site of the owner in the PowerShell app, without a
        // site filter on v_R_System, and UNION ALL of the two views. The script removes the doubles itself.
        var query = QueryText();
        Assert.DoesNotContain("v_R_System", query);
        Assert.Contains("UNION ALL", query);
        Assert.Equal(3, Regex.Matches(query, @"(?i)SELECT DISTINCT").Count);
        Assert.Equal(2, Regex.Matches(query, @"(?i)FROM dbo\.v_GS_ADD_REMOVE_PROGRAMS").Count);
    }

    [Fact]
    public void TheInventoryIsReadFromADataSetAndNotFromADataReader()
    {
        // Q-134: a SqlDataReader read row by row ended in "Invalid attempt to read when no data is present" at a production site.
        // The PowerShell app fills a DataSet with a SqlDataAdapter and reads that site without the error.
        var text = InventoryText;
        Assert.Contains("SqlDataAdapter", text);
        Assert.Contains("$adapter.Fill($dataSet)", text);
        Assert.DoesNotContain("ExecuteReader", text);
        Assert.DoesNotContain(".Read()", text);
        Assert.DoesNotContain(".NextResult()", text);
        Assert.DoesNotContain("GetValue(", text);
        Assert.DoesNotContain("IsDBNull(", text);
    }

    [Fact]
    public void TheConnectionUsesWindowsAuthenticationReadOnlyIntentATimeoutAndIsDisposedOnEveryPath()
    {
        var text = InventoryText;
        Assert.Contains("$builder['Integrated Security'] = $true", text);
        Assert.Contains("$builder['ApplicationIntent'] = 'ReadOnly'", text);
        Assert.Contains("$builder['Connect Timeout']", text);
        Assert.Contains("$command.CommandTimeout = [Math]::Max(1, $CommandTimeout)", text);
        Assert.DoesNotMatch("(?i)password|user id", text);
        Assert.Contains("if ($Encrypt) { $builder['Encrypt'] = $true }", text);
        Assert.Contains("if ($Trust) { $builder['TrustServerCertificate'] = $true }", text);
        var final = text[text.IndexOf("    finally {", StringComparison.Ordinal)..];
        Assert.Contains("$adapter.Dispose()", final);
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

    /// <summary>A real DataSet with one table per set, as the data adapter fills it from the inventory query.</summary>
    private const string FakeDataSet = """
        function New-FakeDataSet {
            param([object[]]$Sets, [int[]]$Widths)
            $dataSet = New-Object System.Data.DataSet
            for ($i = 0; $i -lt $Sets.Count; $i++) {
                $table = New-Object System.Data.DataTable ('Table' + $i)
                for ($c = 0; $c -lt $Widths[$i]; $c++) { [void]$table.Columns.Add((New-Object System.Data.DataColumn ('C' + $c), ([object]))) }
                foreach ($row in @($Sets[$i])) { [void]$table.Rows.Add([object[]]$row) }
                $dataSet.Tables.Add($table)
            }
            return ,$dataSet
        }
        """;

    [Fact]
    public void TheInventoryOutputCarriesNoClientDataAndParsesIntoTheSnapshot()
    {
        var script = InventoryText + Environment.NewLine + FakeDataSet + Environment.NewLine + """
            $rows = @(
                ,@('7-Zip 26.03 (x64)', '26.03', 'x64')
                ,@('7-Zip 26.03 (x64)', '26.03', 'x64')
                ,@('7-Zip 26.03 (x64)', '26.03', 'x64')
                ,@("Caf$([char]0x00e9) `"Pro`" \ tool", [System.DBNull]::Value, 'x86')
            )
            $apps = @(@(,'Git 2.56.0 (x64)'), @(,'CMTrace Open'))
            Write-DiscoveryInventory -DataSet (New-FakeDataSet -Sets @(,$rows + ,$apps) -Widths 3,1)
            [Console]::Out.WriteLine('{"t":"end"}')
            """;
        var output = RunPowerShell(script);
        Assert.DoesNotContain("\"p\"", output);
        Assert.DoesNotContain("\"c\"", output);
        Assert.DoesNotContain("clients", output);
        Assert.DoesNotContain("newest", output);
        Assert.All(output.Split('\n', StringSplitOptions.RemoveEmptyEntries), line => Assert.True(line.TrimEnd().All(c => c < 128), line));

        var snapshot = DiscoveryService.ParseInventory(output, new DiscoveryDatabaseTarget("s", "d"), 30);
        Assert.Equal(2, snapshot.Rows.Count);
        var sevenZip = snapshot.Rows[0];
        Assert.Equal(("7-Zip 26.03 (x64)", "26.03", "x64"), (sevenZip.DisplayName, sevenZip.Version, sevenZip.Architecture));
        var cafe = snapshot.Rows[1];
        Assert.Equal(("Café \"Pro\" \\ tool", "", "x86"), (cafe.DisplayName, cafe.Version, cafe.Architecture));
        Assert.Equal(["Git 2.56.0 (x64)", "CMTrace Open"], snapshot.Applications);
    }

    [Fact]
    public void AnInventoryFailureWritesNumbersFlagsTheTypesAndTheInnermostText()
    {
        var script = InventoryText + Environment.NewLine + """
            $inner = New-Object System.Exception "Login failed for user 'NT AUTHORITY\ANONYMOUS LOGON'. Cannot generate SSPI context. CONTOSO\jdoe"
            Write-DiscoveryError -Exception (New-Object System.Management.Automation.MethodInvocationException 'Exception calling "Open"', $inner)
            """;
        var output = RunPowerShell(script).Trim();
        using var json = JsonDocument.Parse(output);
        var root = json.RootElement;
        Assert.Equal("error", root.GetProperty("t").GetString());
        Assert.Equal("sql", root.GetProperty("stage").GetString());
        Assert.True(root.GetProperty("sspi").GetBoolean());
        Assert.True(root.GetProperty("anonymous").GetBoolean());
        Assert.Equal(["MethodInvocationException", "Exception"], root.GetProperty("types").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(@"Login failed for user 'NT AUTHORITY\ANONYMOUS LOGON'. Cannot generate SSPI context. CONTOSO\jdoe", root.GetProperty("detail").GetString());
        Assert.Equal("", root.GetProperty("step").GetString());
        Assert.Equal(-1, root.GetProperty("rows").GetInt32());
        Assert.Equal(-1, root.GetProperty("table").GetInt32());
        Assert.Equal(-1, root.GetProperty("column").GetInt32());
        Assert.Equal(@"MethodInvocationException: Exception calling ""Open"" | Exception: Login failed for user 'NT AUTHORITY\ANONYMOUS LOGON'. Cannot generate SSPI context. CONTOSO\jdoe",
            root.GetProperty("chain").GetString());
    }

    [Fact]
    public void AnInventoryFailureTextIsOneAsciiLineOfAtMostThreeHundredCharacters()
    {
        var script = InventoryText + Environment.NewLine + """
            $text = "The certificate chain was issued by an authority that is not trusted." + [char]13 + [char]10 + ("x" * 400) + [char]0x00e9
            Write-DiscoveryError -Exception (New-Object System.Exception $text)
            """;
        var line = RunPowerShell(script).Trim();
        using var json = System.Text.Json.JsonDocument.Parse(line);
        var detail = json.RootElement.GetProperty("detail").GetString()!;
        Assert.StartsWith("The certificate chain was issued by an authority that is not trusted.", detail);
        Assert.InRange(detail.Length, 250, 300);
        Assert.All(line, c => Assert.True(c < 128));
        Assert.DoesNotContain('\n', line.TrimEnd());
    }

    [Fact]
    public void ANameEndingInALineBreakIsEscapedByBothScripts()
    {
        foreach (var text in new[] { InventoryText, SiteText })
        {
            var script = text + Environment.NewLine + """
                [Console]::Out.WriteLine((ConvertTo-DiscoveryJsonString "Tool`n"))
                [Console]::Out.WriteLine((ConvertTo-DiscoveryJsonString "Tool`r`n"))
                [Console]::Out.WriteLine((ConvertTo-DiscoveryJsonString 'Tool'))
                """;
            var lines = RunPowerShell(script).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal(["\"Tool\\u000a\"", "\"Tool\\u000d\\u000a\"", "\"Tool\""], lines);
        }
    }

    [Fact]
    public void AnInventoryRowWhoseNameEndsInALineBreakKeepsItsRow()
    {
        var script = InventoryText + Environment.NewLine + FakeDataSet + Environment.NewLine + """
            $rows = @(
                ,@("Tool`n", '1.0', 'x64')
                ,@("Tool`r`n", '1.0', 'x64')
            )
            $apps = @()
            Write-DiscoveryInventory -DataSet (New-FakeDataSet -Sets @(,$rows + ,$apps) -Widths 3,1)
            [Console]::Out.WriteLine('{"t":"end"}')
            """;
        var snapshot = DiscoveryService.ParseInventory(RunPowerShell(script), new DiscoveryDatabaseTarget("s", "d"), 30);
        Assert.Equal(["Tool\n", "Tool\r\n"], snapshot.Rows.Select(r => r.DisplayName));
    }

    [Fact]
    public void AnEmptyInventoryAndNullCellsAreReadWithoutAnError()
    {
        var script = InventoryText + Environment.NewLine + FakeDataSet + Environment.NewLine + """
            $rows = @(
                ,@([System.DBNull]::Value, [System.DBNull]::Value, 'x64')
                ,@('Tool', [System.DBNull]::Value, [System.DBNull]::Value)
            )
            Write-DiscoveryInventory -DataSet (New-FakeDataSet -Sets @(,$rows + ,@()) -Widths 3,1)
            Write-DiscoveryInventory -DataSet (New-FakeDataSet -Sets @(,@() + ,@()) -Widths 3,1)
            [Console]::Out.WriteLine('{"t":"end"}')
            """;
        var output = RunPowerShell(script);
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd()).ToList();
        Assert.Equal(2, lines.Count(l => l.StartsWith("{\"t\":\"row\"", StringComparison.Ordinal)));
        Assert.Equal(2, lines.Count(l => l == "{\"t\":\"summary\"}"));
        Assert.DoesNotContain("\"t\":\"error\"", output);
    }

    [Fact]
    public void ADataSetWithOneTableIsAnErrorThatNamesTheStepAndTheRowCount()
    {
        var script = InventoryText + Environment.NewLine + FakeDataSet + Environment.NewLine + """
            try { Write-DiscoveryInventory -DataSet (New-FakeDataSet -Sets @(,@(,@('A','1','x64'))) -Widths 3) }
            catch { Write-DiscoveryError -Exception $_.Exception -Step 'read the inventory rows' -RowCount 1 }
            """;
        var line = RunPowerShell(script).Trim();
        using var json = JsonDocument.Parse(line);
        Assert.Equal("read the inventory rows", json.RootElement.GetProperty("step").GetString());
        Assert.Equal(1, json.RootElement.GetProperty("rows").GetInt32());
        Assert.Contains("instead of two", json.RootElement.GetProperty("detail").GetString());
        var failure = Assert.Throws<DiscoveryException>(() =>
            DiscoveryService.ParseInventory(line + "\n{\"t\":\"end\"}", new DiscoveryDatabaseTarget("sql01", "CM_MCM"), 30));
        Assert.Contains("Server sql01, database CM_MCM.", failure.Message);
        Assert.Contains("Step: read the inventory rows, 1 rows read.", failure.Message);
    }

    [Fact]
    public void AReadFailureNamesTheTableTheRowAndTheColumnAndEveryExceptionOfTheChain()
    {
        // Q-134: a cell read that throws: the failure names the cell, so a read error at a site shows where.
        var script = InventoryText + Environment.NewLine + FakeDataSet + Environment.NewLine + """
            function Get-DiscoveryCell { param($Row, [int]$Index) if ($Index -eq 1) { throw (New-Object System.InvalidOperationException 'Invalid attempt to read when no data is present.') }; return $Row[$Index] }
            $t0 = @(,@('A','1','x64'))
            $t1 = @(,@('B'))
            $ds = New-FakeDataSet -Sets @(,$t0 + ,$t1) -Widths 3,1
            try { Write-DiscoveryInventory -DataSet $ds }
            catch { Write-DiscoveryError -Exception $_.Exception -Step 'read the inventory rows' -RowCount $script:DiscoveryRowCount -Table $script:DiscoveryTable -Column $script:DiscoveryColumn }
            """;
        var line = RunPowerShell(script).Trim();
        using var json = JsonDocument.Parse(line);
        Assert.Equal(0, json.RootElement.GetProperty("table").GetInt32());
        Assert.Equal(1, json.RootElement.GetProperty("column").GetInt32());
        Assert.Equal(1, json.RootElement.GetProperty("rows").GetInt32());
        Assert.StartsWith("InvalidOperationException: Invalid attempt to read when no data is present.", json.RootElement.GetProperty("chain").GetString());
        var failure = Assert.Throws<DiscoveryException>(() =>
            DiscoveryService.ParseInventory(line + "\n{\"t\":\"end\"}", new DiscoveryDatabaseTarget("sql01", "CM_MCM"), 30));
        Assert.Contains("Step: read the inventory rows, 1 rows read, table 0, column 1.", failure.Message);
        Assert.Contains("Exceptions: ", failure.Message);
    }

    private const string FakeCim = """
        $global:Log = New-Object System.Collections.Generic.List[string]
        $global:FailSession = @()
        $global:FailQuery = @()
        $global:Empty = $false
        $global:EmptyOn = @()
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
            if ($global:Empty -or $global:EmptyOn -contains $CimSession.Protocol) { return }
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
        Assert.Contains("{\"t\":\"error\",\"stage\":\"site\",\"detail\":\"Dcom ", output);
        Assert.Contains("; Wsman ", output);
        Assert.Contains("Access is denied. (HRESULT 0x80070005)", output);
        Assert.Contains("dispose:Dcom", output);
        Assert.Contains("dispose:Wsman", output);
    }

    [Fact]
    public void AProviderWithoutTheSiteOverBothProtocolsAnswersNotFound()
    {
        var output = RunSite("$global:Empty = $true");
        Assert.StartsWith("{\"t\":\"site\",\"found\":false,\"via\":\"Wsman\"}", output);
        Assert.Contains("query:Dcom:", output);
        Assert.Contains("query:Wsman:", output);
    }

    [Fact]
    public void TheSiteDefinitionFallsBackToWinRmWhenDcomReturnsNoInstance()
    {
        var output = RunSite("$global:EmptyOn = @('Dcom')");
        Assert.StartsWith("{\"t\":\"site\",\"found\":true,\"server\":\"CM01.contoso.com\",\"database\":\"CM_MCM\",\"via\":\"Wsman\"}", output);
        Assert.Contains("dispose:Dcom|open:Wsman", output);
    }

    [Fact]
    public void ANotFoundOverDcomStaysTheAnswerWhenWinRmFails()
    {
        var output = RunSite("$global:EmptyOn = @('Dcom'); $global:FailQuery = @('Wsman')");
        Assert.StartsWith("{\"t\":\"site\",\"found\":false,\"via\":\"Dcom\"}", output);
        Assert.DoesNotContain("\"error\"", output);
        Assert.Contains("dispose:Wsman", output);
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
            "PowerShell 7-x64", "7.4.6.0", "7.5.4.0", "7.10.0.0", "7.40.1.0", "17.0.16.8", "21.0.8.9", "Mozilla Firefox (x64 en-US)", "Notepad++ (64-bit x64)",
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
