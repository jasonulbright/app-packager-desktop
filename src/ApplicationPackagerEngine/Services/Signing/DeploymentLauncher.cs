using System.Text.RegularExpressions;
using ApplicationPackagerEngine.Models.Signing;

namespace ApplicationPackagerEngine.Services.Signing;

/// <summary>A deployment launcher for one script: the PowerShell invocation and the .bat body around it.</summary>
public sealed record DeploymentLauncherCommand(
    string Script, bool Signed, string ScriptHost, string Executable,
    string CommandLine, string BatInvoke, string BatBody);

/// <summary>
/// Builds the launcher that runs a staged .ps1 and audits a staged tree for
/// launchers that relax the endpoint's execution policy.
///
/// Unsigned mode emits this string, byte for byte:
///   powershell.exe -NonInteractive -ExecutionPolicy Bypass -File "%~dp0install.ps1"
/// Signed mode carries no execution-policy argument in any spelling and no
/// encoded command, so the endpoint's configured policy governs:
///   powershell.exe -NoProfile -NonInteractive -File "%~dp0install.ps1"
/// The x86 host replaces powershell.exe with
///   %SystemRoot%\SysWOW64\WindowsPowerShell\v1.0\powershell.exe
/// </summary>
public static class DeploymentLauncher
{
    public const string HostX64 = "x64";
    public const string HostX86 = "x86";
    public const string X86Executable = @"%SystemRoot%\SysWOW64\WindowsPowerShell\v1.0\powershell.exe";

    public const string CodeExecutionPolicy = "LauncherExecutionPolicy";
    public const string CodeEncodedCommand = "LauncherEncodedCommand";
    public const string CodePolicyRelaxation = "LauncherPolicyRelaxation";
    public const string CodeCustomCommandBypass = "CustomCommandBypass";

    /// <summary>
    /// The .bat returns the PowerShell process exit code (with -File, the
    /// script's "exit N"), and deletes exitcode.txt: Windows PowerShell writes
    /// that file as UTF-16 with a byte-order mark, which "set /p" reads as
    /// non-numeric text and "exit /b" then turns into 0.
    /// </summary>
    public static DeploymentLauncherCommand Build(string script, bool signed, string scriptHost = HostX64)
    {
        var exe = scriptHost switch
        {
            HostX86 => X86Executable,
            HostX64 => "powershell.exe",
            _ => throw new ArgumentException($"Unsupported script host '{scriptHost}'. Use x64 or x86.", nameof(scriptHost))
        };
        var switches = signed ? "-NoProfile -NonInteractive" : "-NonInteractive -ExecutionPolicy Bypass";
        var commandLine = $"{exe} {switches} -File \"{script}\"";
        var batInvoke = $"{exe} {switches} -File \"%~dp0{script}\"";
        var batBody = string.Join("\r\n",
            "@echo off",
            batInvoke,
            "set \"PSCODE=%ERRORLEVEL%\"",
            "del /q \"%~dp0exitcode.txt\" >nul 2>&1",
            "exit /b %PSCODE%");
        return new DeploymentLauncherCommand(script, signed, scriptHost, exe, commandLine, batInvoke, batBody);
    }

    /// <summary>
    /// Token-level findings for one line of text. PowerShell binds a
    /// parameter by unique prefix, so -e, -ex and -executionpo all reach
    /// -ExecutionPolicy and -e and -enc reach -EncodedCommand; -ep and -ec
    /// are aliases. powershell.exe accepts an en dash, em dash or horizontal
    /// bar as the parameter dash, cmd removes carets and quotes before
    /// powershell.exe sees the line, and an -ArgumentList string carries
    /// its first parameter behind a quote, so the line is normalized for all
    /// four before tokens are matched. PowerShell drops a backtick anywhere in
    /// a parameter name or a bare word (-Executi`onPolicy By`pass), so a token
    /// is matched without its backticks. A bare policy name counts only on a
    /// line that also names the execution policy: a comment or a string that
    /// merely contains the word is not a launcher.
    /// </summary>
    public static IReadOnlyList<(string Code, string Token)> TokenFindings(string text)
    {
        var findings = new List<(string, string)>();
        text = Regex.Replace(text.Replace("^", "", StringComparison.Ordinal), "[–—―]", "-");
        // A parameter can arrive as one element of an argument array ('-ep','bypass' or
        // @('-ex', 'bypass')), so a comma, a semicolon and a parenthesis end a token too.
        foreach (var raw in Regex.Split(text, @"[\s=:,;()]+"))
        {
            var token = raw.TrimStart('"', '\'', '`', '‘', '’', '‚', '‛', '“', '”', '„');
            if (string.IsNullOrWhiteSpace(token)) continue;
            if (token[0] != '-' && token[0] != '/') continue;
            var name = token.TrimStart('-', '/').Replace("`", "", StringComparison.Ordinal).Trim('"', '\'').ToLowerInvariant();
            if (name.Length == 0) continue;
            if ("executionpolicy".StartsWith(name, StringComparison.Ordinal) || name == "ep")
                findings.Add((CodeExecutionPolicy, raw));
            if ("encodedcommand".StartsWith(name, StringComparison.Ordinal) || name == "ec")
                findings.Add((CodeEncodedCommand, raw));
        }
        var plain = text.Replace("`", "", StringComparison.Ordinal);
        if (Regex.IsMatch(plain, "(?i)executionpolicy"))
        {
            var bare = Regex.Match(plain, @"(?i)\b(bypass|unrestricted|remotesigned)\b");
            if (bare.Success) findings.Add((CodePolicyRelaxation, bare.Groups[1].Value.ToLowerInvariant()));
        }
        return findings;
    }

    /// <summary>The launchers the stage writes at its root; always AppPackager-owned and always audited.</summary>
    public static readonly IReadOnlyList<string> GeneratedLaunchers = ["install.bat", "uninstall.bat", "install.ps1", "uninstall.ps1"];

    private static readonly string[] ToolkitEntryScripts = ["Invoke-AppDeployToolkit.ps1", "Deploy-Application.ps1"];

    /// <summary>The folders a PSADT v3 or v4 layout carries beside its entry script.</summary>
    private static readonly string[] ToolkitFolders =
        ["AppDeployToolkit", "PSAppDeployToolkit", "PSAppDeployToolkit.Extensions", "Assets", "Config", "Strings", "Files", "SupportFiles"];

    /// <summary>
    /// Vendor toolkit content: every folder below the stage root that carries
    /// a PSADT entry script (Invoke-AppDeployToolkit.ps1 or
    /// Deploy-Application.ps1), plus manifest-declared third-party scripts by
    /// stage-relative path. The stage root itself is never a root: a toolkit
    /// there contributes its entry script and its own folders, so the
    /// generated launchers and scripts\ beside it stay owned.
    /// </summary>
    public static (IReadOnlyList<string> Roots, IReadOnlyList<string> Declared) ThirdPartyRoots(
        string stageRoot, IEnumerable<string>? declaredThirdPartyScripts)
    {
        var roots = new List<string>();
        var declared = declaredThirdPartyScripts?.Where(d => !string.IsNullOrWhiteSpace(d)).ToList() ?? [];
        if (Directory.Exists(stageRoot))
        {
            var rootFull = Path.GetFullPath(stageRoot).TrimEnd('\\');
            foreach (var entry in Directory.EnumerateFiles(rootFull, "*", SearchOption.AllDirectories)
                         .Where(f => ToolkitEntryScripts.Contains(Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)))
            {
                var folder = Path.GetDirectoryName(entry)!.TrimEnd('\\');
                if (!string.Equals(folder, rootFull, StringComparison.OrdinalIgnoreCase))
                {
                    roots.Add(folder);
                    continue;
                }
                declared.Add(Path.GetFileName(entry));
                roots.AddRange(ToolkitFolders.Select(f => Path.Combine(rootFull, f)).Where(Directory.Exists));
            }
        }
        return (roots.Distinct(StringComparer.OrdinalIgnoreCase).ToList(), declared);
    }

    public static bool IsThirdPartyFile(string fullName, string relativePath,
        (IReadOnlyList<string> Roots, IReadOnlyList<string> Declared) thirdParty)
    {
        if (GeneratedLaunchers.Contains(relativePath, StringComparer.OrdinalIgnoreCase)) return false;
        foreach (var root in thirdParty.Roots)
            if (fullName.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase)) return true;
        foreach (var name in thirdParty.Declared)
            if (string.Equals(relativePath, name, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>
    /// Inventories the staged execution chain: every .bat/.cmd/.ps1/.psm1
    /// under the stage root outside third-party roots, plus the resolved
    /// entry commands. With SignDeployment or RequireDeployment set, any
    /// finding throws naming the file and line; otherwise findings are
    /// informational.
    /// </summary>
    public static LauncherChainResult AuditChain(string stageRoot, IEnumerable<LauncherCommand>? commands,
        SigningPolicy policy, IEnumerable<string>? declaredThirdPartyScripts = null)
    {
        var normalized = SigningPolicy.Normalize(policy);
        var findings = new List<LauncherFinding>();
        var files = new List<string>();
        var thirdPartySkipped = 0;

        if (Directory.Exists(stageRoot))
        {
            var thirdParty = ThirdPartyRoots(stageRoot, declaredThirdPartyScripts);
            var rootFull = Path.GetFullPath(stageRoot).TrimEnd('\\');
            foreach (var file in Directory.EnumerateFiles(rootFull, "*", SearchOption.AllDirectories)
                         .Where(f => Regex.IsMatch(Path.GetExtension(f), @"^(?i)\.(bat|cmd|ps1|psm1)$"))
                         .OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                var relative = file[rootFull.Length..].TrimStart('\\');
                if (IsThirdPartyFile(file, relative, thirdParty))
                {
                    thirdPartySkipped++;
                    continue;
                }
                files.Add(file);
            }
        }

        foreach (var file in files)
        {
            string[] lines;
            try { lines = File.ReadAllLines(file); }
            catch (IOException) { continue; }
            var isBatch = Regex.IsMatch(Path.GetExtension(file), @"^(?i)\.(bat|cmd)$");
            var inSignatureBlock = false;
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                // Every code line is scanned: a launcher can reach PowerShell as
                // pwsh.exe, through a variable or through a cmd start, so the host
                // name is not a gate. Comments and the signature block are not commands.
                if (Regex.IsMatch(line, @"^\s*#\s*SIG # Begin signature block")) { inSignatureBlock = true; continue; }
                if (Regex.IsMatch(line, @"^\s*#\s*SIG # End signature block")) { inSignatureBlock = false; continue; }
                if (inSignatureBlock) continue;
                if (isBatch) { if (Regex.IsMatch(line, @"^\s*(?i:rem)\b|^\s*::")) continue; }
                else if (Regex.IsMatch(line, @"^\s*#")) continue;

                foreach (var (code, token) in TokenFindings(line))
                {
                    var message = code switch
                    {
                        CodeExecutionPolicy => $"Launcher passes an execution-policy argument ('{token}').",
                        CodeEncodedCommand => $"Launcher passes an encoded command ('{token}').",
                        _ => $"Launcher relaxes the execution policy ('{token}')."
                    };
                    findings.Add(new LauncherFinding { Code = code, File = file, LineNumber = i + 1, Line = line, Message = message });
                }
            }
        }

        var commandList = commands?.Where(c => !string.IsNullOrWhiteSpace(c.Text)).ToList() ?? [];
        foreach (var command in commandList)
        {
            foreach (var (code, token) in TokenFindings(command.Text))
            {
                findings.Add(new LauncherFinding
                {
                    Code = code == CodePolicyRelaxation ? CodeCustomCommandBypass : code,
                    File = command.Source,
                    LineNumber = 0,
                    Line = command.Text,
                    Message = $"Resolved command carries '{token}'."
                });
            }
        }

        var result = new LauncherChainResult
        {
            Findings = findings,
            FilesInspected = files.Count,
            ThirdPartySkipped = thirdPartySkipped,
            CommandsInspected = commandList.Count,
            Enforced = normalized.SignDeployment || normalized.RequireDeployment
        };

        // RequireDeployment refuses on its own: an environment that verifies
        // signatures without producing them still refuses a relaxed launcher.
        if (result.Enforced && findings.Count > 0)
        {
            var detail = string.Join("; ", findings.Select(f => $"{f.Message} ({f.File}:{f.LineNumber})"));
            throw new InvalidOperationException($"Signed deployment mode refuses this build: {detail}");
        }
        return result;
    }
}
