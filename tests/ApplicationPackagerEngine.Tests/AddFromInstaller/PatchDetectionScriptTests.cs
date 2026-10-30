using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.AddFromInstaller;

namespace ApplicationPackagerEngine.Tests.AddFromInstaller;

/// <summary>
/// The MSP detection script runs in Windows PowerShell 5.1 on the client. Installer.PatchesEx takes an empty string as
/// the user SID of the machine context; $null throws in Windows PowerShell 5.1. A failure reaches standard error with a
/// non-zero exit code, which ConfigMgr reports as detection state Unknown.
/// </summary>
public sealed class PatchDetectionScriptTests
{
    private const string ProductCode = "{6F1A2B3C-4D5E-4F60-8A7B-9C0D1E2F3A4B}";
    private const string PatchCode = "{AAAABBBB-0000-4000-8000-000000000002}";

    private static string Script(string productCode = ProductCode, string patchCode = PatchCode) =>
        AddFromInstallerService.PatchDetectionConfig(productCode, patchCode).ScriptText!;

    [Fact]
    public void TheCallPassesAnEmptyUserSidForTheMachineContext()
    {
        var script = Script();

        Assert.Contains($"InvokeMember('PatchesEx', 'GetProperty', $null, $installer, @('{ProductCode}', '', 4, 1))", script);
        Assert.DoesNotContain("$null, 4", script);
    }

    [Fact]
    public async Task InWindowsPowerShell51_TheScriptParsesWithNoErrors_AndEveryCatchBlockExitsWithAnError()
    {
        var path = Path.Combine(Path.GetTempPath(), $"apd-patchparse-{Guid.NewGuid():N}.ps1");
        await File.WriteAllTextAsync(path, Script());
        try
        {
            var check = $$"""
                $t = $null; $e = $null
                $ast = [System.Management.Automation.Language.Parser]::ParseFile('{{path}}', [ref]$t, [ref]$e)
                Write-Output ('errors=' + @($e).Count)
                foreach ($c in $ast.FindAll({ param($n) $n -is [System.Management.Automation.Language.CatchClauseAst] }, $true)) {
                    Write-Output ('catch=' + $c.Body.Statements.Count + '|' + ($c.Body.Extent.Text -match 'exit 1') + '|' + ($c.Body.Extent.Text -match 'Console\]::Error'))
                }
                """;
            var (exitCode, stdOut, stdErr) = await WindowsPowerShellHost.RunAsync(check, null, CancellationToken.None, "apd-patchparse");

            Assert.True(exitCode == 0, stdErr);
            var lines = stdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            Assert.Contains("errors=0", lines);
            var catches = lines.Where(l => l.StartsWith("catch=", StringComparison.Ordinal)).ToList();
            Assert.NotEmpty(catches);
            Assert.All(catches, c => Assert.Matches(@"^catch=[1-9]\d*\|True\|True$", c));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task InWindowsPowerShell51_AProductThatIsNotInstalled_IsNotDetected_WithNoError()
    {
        var (exitCode, stdOut, stdErr) = await WindowsPowerShellHost.RunAsync(Script(), null, CancellationToken.None, "apd-patchdetect");

        Assert.True(exitCode == 0, $"exit {exitCode}: {stdErr}");
        Assert.Equal("", stdOut.Trim());
        Assert.Equal("", stdErr.Trim());
    }

    /// <summary>The packed GUID form of the Windows Installer registration keys, unpacked: three reversed groups, then eight swapped byte pairs.</summary>
    private static string Unpack(string packed)
    {
        static string Reverse(string s) => new(s.Reverse().ToArray());
        static string Swap(string s) => string.Concat(Enumerable.Range(0, s.Length / 2).Select(i => $"{s[2 * i + 1]}{s[2 * i]}"));
        return $"{{{Reverse(packed[..8])}-{Reverse(packed[8..12])}-{Reverse(packed[12..16])}-{Swap(packed[16..20])}-{Swap(packed[20..32])}}}";
    }

    /// <summary>One product and patch applied in the machine context of this computer, or null when there is none.</summary>
    private static (string Product, string Patch)? AppliedMachinePatch()
    {
        using var products = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry64)
            .OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Installer\UserData\S-1-5-18\Products");
        foreach (var product in products?.GetSubKeyNames() ?? [])
        {
            using var patches = products!.OpenSubKey(product + @"\Patches");
            foreach (var patch in patches?.GetSubKeyNames() ?? [])
            {
                using var key = patches!.OpenSubKey(patch);
                if (product.Length == 32 && patch.Length == 32 && key?.GetValue("State") is int and 1) return (Unpack(product), Unpack(patch));
            }
        }
        return null;
    }

    [Fact]
    public void TheUnpackedRegistrationForm_IsTheGuid()
    {
        Assert.Equal("{1D8E6291-B0D5-35EC-8441-6616F567A0F7}", Unpack("1926E8D15D0BCE53481466615F760A7F"));
    }

    [Fact]
    public async Task InWindowsPowerShell51_APatchAppliedOnThisComputer_IsDetected_AndAnotherPatchCodeIsNot()
    {
        // A computer with no Windows Installer patch in the machine context has no applied patch to detect.
        if (AppliedMachinePatch() is not { } applied) return;

        var (exitCode, stdOut, stdErr) = await WindowsPowerShellHost.RunAsync(Script(applied.Product, applied.Patch), null, CancellationToken.None, "apd-patchdetect");
        var (otherExit, otherOut, otherErr) = await WindowsPowerShellHost.RunAsync(Script(applied.Product, PatchCode), null, CancellationToken.None, "apd-patchdetect");

        Assert.True(exitCode == 0, $"exit {exitCode}: {stdErr}");
        Assert.Equal("Installed", stdOut.Trim());
        Assert.True(otherExit == 0, $"exit {otherExit}: {otherErr}");
        Assert.Equal("", otherOut.Trim());
    }
}
