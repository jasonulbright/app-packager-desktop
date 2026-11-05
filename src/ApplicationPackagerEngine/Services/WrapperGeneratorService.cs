using System.Text;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Services.Signing;

namespace ApplicationPackagerEngine.Services;

/// <summary>
/// Generates install.bat/ps1 and uninstall.bat/ps1 wrapper files.
/// </summary>
public sealed class WrapperGeneratorService
{
    /// <summary>
    /// Generates all 4 wrapper files for an MSI installer.
    /// </summary>
    public void GenerateMsiWrappers(string outputPath, string msiFileName, string? uninstallCommand = null, string? installArgs = null,
        bool expandEnvironment = false)
    {
        var msiArgs = string.IsNullOrEmpty(installArgs) ? "/qn /norestart" : installArgs;
        // msiexec does not expand %NAME% in a property value, so the wrapper
        // expands it in the deployment's own context.
        var msiArgsExpression = expandEnvironment
            ? $"[Environment]::ExpandEnvironmentVariables('{EscapePsLiteral(msiArgs)}')"
            : $"'{EscapePsLiteral(msiArgs)}'";
        var installPs1 = $"""
            $msiPath = Join-Path $PSScriptRoot '{EscapePsLiteral(msiFileName)}'
            $proc = Start-Process -FilePath 'msiexec.exe' -ArgumentList @('/i', "`"$msiPath`"", {msiArgsExpression}) -Wait -PassThru -NoNewWindow
            $proc.ExitCode | Out-File (Join-Path $PSScriptRoot 'exitcode.txt') -NoNewline
            exit $proc.ExitCode
            """;

        string uninstallPs1;
        if (!string.IsNullOrEmpty(uninstallCommand))
        {
            uninstallPs1 = $$"""
                {{uninstallCommand}}
                $proc.ExitCode | Out-File (Join-Path $PSScriptRoot 'exitcode.txt') -NoNewline
                exit $proc.ExitCode
                """;
        }
        else
        {
            uninstallPs1 = $$"""
                # Read ProductCode from stage-manifest.json for reliable MSI uninstall
                $manifest = Join-Path $PSScriptRoot 'stage-manifest.json'
                $productCode = $null
                if (Test-Path $manifest) {
                    $json = Get-Content $manifest -Raw | ConvertFrom-Json
                    $productCode = $json.ProductCode
                }
                if ($productCode) {
                    $proc = Start-Process -FilePath 'msiexec.exe' -ArgumentList @('/x', $productCode, '/qn', '/norestart') -Wait -PassThru -NoNewWindow
                } else {
                    $msiPath = Join-Path $PSScriptRoot '{{EscapePsLiteral(msiFileName)}}'
                    $proc = Start-Process -FilePath 'msiexec.exe' -ArgumentList @('/x', "`"$msiPath`"", '/qn', '/norestart') -Wait -PassThru -NoNewWindow
                }
                $proc.ExitCode | Out-File (Join-Path $PSScriptRoot 'exitcode.txt') -NoNewline
                exit $proc.ExitCode
                """;
        }

        WriteWrappers(outputPath, GuardNullProcess(installPs1), GuardNullProcess(uninstallPs1));
    }

    /// <summary>
    /// Generates all 4 wrapper files for an MSI patch: msiexec /p applies it to the installed base product.
    /// The uninstall runs the given expression (it assigns $proc); without one it exits 1, because a patch
    /// is removed only by its patch code and the base product code.
    /// </summary>
    public void GenerateMspWrappers(string outputPath, string mspFileName, string? installArgs, string? uninstallCommand)
    {
        var args = string.IsNullOrWhiteSpace(installArgs) ? "/qn /norestart" : installArgs;
        var installPs1 = $"""
            $mspPath = Join-Path $PSScriptRoot '{EscapePsLiteral(mspFileName)}'
            $proc = Start-Process -FilePath 'msiexec.exe' -ArgumentList @('/p', "`"$mspPath`"", '{EscapePsLiteral(args)}') -Wait -PassThru -NoNewWindow
            $proc.ExitCode | Out-File (Join-Path $PSScriptRoot 'exitcode.txt') -NoNewline
            exit $proc.ExitCode
            """;
        var uninstallPs1 = string.IsNullOrWhiteSpace(uninstallCommand)
            ? "Write-Output 'The patch has no uninstall command; nothing was uninstalled.'\r\nexit 1"
            : $$"""
                {{uninstallCommand}}
                $proc.ExitCode | Out-File (Join-Path $PSScriptRoot 'exitcode.txt') -NoNewline
                exit $proc.ExitCode
                """;
        WriteWrappers(outputPath, GuardNullProcess(installPs1), GuardNullProcess(uninstallPs1));
    }

    /// <summary>
    /// Generates all 4 wrapper files for an EXE installer.
    /// Uninstall runs the definition's command when it has one; otherwise it
    /// resolves the product's Add/Remove Programs entry (see
    /// BuildArpLookupUninstall) using the detection key name and install
    /// folder as lookup hints. With no command and no uninstall arguments,
    /// any uninstaller it could start waits for input in session 0, so the
    /// script exits 1 with <see cref="NoProvenUninstallExit"/>.
    /// </summary>
    public void GenerateExeWrappers(string outputPath, string exeFileName,
        string installArgs, string uninstallArgs, string? installerFramework = null,
        string? applicationName = null, string? uninstallCommand = null,
        string? postInstallScript = null, string? arpKeyName = null, string? installFolder = null,
        IReadOnlyList<string>? postInstallKillProcesses = null, IReadOnlyList<int>? rebootExitCodes = null,
        IReadOnlyList<int>? successExitCodes = null)
    {
        uninstallCommand = NormalizeUninstallCommand(uninstallCommand);
        var installPs1 = BuildExeInstallScript(exeFileName, installArgs, postInstallScript, postInstallKillProcesses);

        string uninstallPs1;
        if (!string.IsNullOrEmpty(uninstallCommand))
        {
            // Direct uninstall command from definition — no ARP search needed.
            // UninstallCommand is a PowerShell expression that assigns $proc, e.g.:
            // $proc = Start-Process "C:\Program Files\App\uninstall.exe" -ArgumentList '/S' -Wait -PassThru -NoNewWindow
            uninstallPs1 = $$"""
                {{uninstallCommand}}
                $proc.ExitCode | Out-File (Join-Path $PSScriptRoot 'exitcode.txt') -NoNewline
                exit $proc.ExitCode
                """;
        }
        else if (string.IsNullOrWhiteSpace(uninstallArgs))
        {
            uninstallPs1 = NoProvenUninstallExit;
        }
        else
        {
            uninstallPs1 = BuildArpLookupUninstall(exeFileName, uninstallArgs, installerFramework,
                applicationName, arpKeyName, installFolder);
        }

        WriteWrappers(outputPath,
            ApplySuccessExitCodes(ApplyRebootExitCodes(GuardNullProcess(installPs1), rebootExitCodes), successExitCodes),
            ApplyRebootExitCodes(GuardNullProcess(uninstallPs1), rebootExitCodes));
    }

    private const string PostInstallGuardLine = "if ($proc.ExitCode -ne 0) { exit $proc.ExitCode }";

    /// <summary>
    /// Reports each listed installer exit code as 0 in an install script: an
    /// installer can return its own code after a complete install, and the
    /// detection rule still decides whether the install succeeded. With a
    /// post-install section, the listed codes pass its installer exit-code
    /// guard, so the section runs after them as it does after 0. A script with
    /// no codes is returned unchanged.
    /// </summary>
    public static string ApplySuccessExitCodes(string script, IReadOnlyList<int>? successExitCodes)
    {
        var codes = successExitCodes?.Where(c => c != 0).Distinct().ToList() ?? [];
        if (codes.Count == 0) return script;

        if (script.Contains(PostInstallGuardLine, StringComparison.Ordinal))
            return script.Replace(PostInstallGuardLine,
                $"if ($proc.ExitCode -ne 0{string.Concat(codes.Select(c => $" -and $proc.ExitCode -ne {c}"))}) {{ exit $proc.ExitCode }}",
                StringComparison.Ordinal);

        return System.Text.RegularExpressions.Regex.Replace(script,
            @"(?m)^([ \t]*)" + System.Text.RegularExpressions.Regex.Escape(ExitCodeFileLine),
            m =>
            {
                var indent = m.Groups[1].Value;
                var sb = new StringBuilder();
                foreach (var code in codes)
                    sb.Append(indent)
                      .Append($"if ($proc.ExitCode -eq {code}) {{ '0' | Out-File (Join-Path $PSScriptRoot 'exitcode.txt') -NoNewline; exit 0 }}")
                      .Append("\r\n");
                return sb.Append(m.Value).ToString();
            });
    }

    private const string ExitCodeFileLine = "$proc.ExitCode | Out-File (Join-Path $PSScriptRoot 'exitcode.txt') -NoNewline";

    /// <summary>
    /// The EXE install body. With processes to stop, the installer is started
    /// without -Wait: -Wait also waits for every descendant, and an installer
    /// that launches its application never returns under a system-context
    /// deployment. The wait then covers the installer process alone and the
    /// launched instances are stopped before the exit code is reported. A
    /// Process returned by Start-Process -PassThru without -Wait holds no
    /// handle, and in Windows PowerShell its ExitCode reads null once the
    /// process has exited, which the wrapper reports as 0; reading Handle
    /// while the installer runs keeps the exit code.
    /// Post-install configuration runs only after a successful install and
    /// reports 0, so a configuration step never masks the installer's code.
    /// </summary>
    public static string BuildExeInstallScript(string exeFileName, string installArgs,
        string? postInstallScript = null, IReadOnlyList<string>? postInstallKillProcesses = null)
    {
        var kill = postInstallKillProcesses?.Where(p => !string.IsNullOrWhiteSpace(p)).ToList() ?? [];
        var lines = new List<string> { $"$exePath = Join-Path $PSScriptRoot '{EscapePsLiteral(exeFileName)}'" };
        if (kill.Count > 0)
        {
            lines.Add($"$proc = Start-Process -FilePath $exePath{ArgumentList(installArgs)} -PassThru -NoNewWindow");
            lines.Add("$null = $proc.Handle");
            lines.Add("$proc.WaitForExit()");
            lines.Add("Start-Sleep -Seconds 3");
            var names = string.Join(", ", kill.Select(p => "'" + EscapePsLiteral(p.Trim()) + "'"));
            lines.Add($"foreach ($pn in @({names})) {{ Get-Process -Name $pn -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue }}");
        }
        else
        {
            lines.Add($"$proc = Start-Process -FilePath $exePath{ArgumentList(installArgs)} -Wait -PassThru -NoNewWindow");
        }
        lines.Add(ExitCodeFileLine);

        if (!string.IsNullOrWhiteSpace(postInstallScript))
        {
            lines.Add(PostInstallGuardLine);
            lines.Add(postInstallScript.Trim().Replace("\r\n", "\n").Replace("\n", "\r\n"));
            lines.Add("exit 0");
        }
        else
        {
            lines.Add("exit $proc.ExitCode");
        }
        return string.Join("\r\n", lines);
    }

    /// <summary>
    /// Reports each reboot exit code as 3010 at every point a wrapper writes
    /// exitcode.txt: the script exits 3010 there, and the .bat wrapper
    /// returns the script's exit code. A script with no codes is returned
    /// unchanged.
    /// </summary>
    public static string ApplyRebootExitCodes(string script, IReadOnlyList<int>? rebootExitCodes)
    {
        var codes = rebootExitCodes?.Where(c => c != 0 && c != 3010).Distinct().ToList() ?? [];
        if (codes.Count == 0) return script;

        return System.Text.RegularExpressions.Regex.Replace(script,
            @"(?m)^([ \t]*)" + System.Text.RegularExpressions.Regex.Escape(ExitCodeFileLine),
            m =>
            {
                var indent = m.Groups[1].Value;
                var sb = new StringBuilder();
                foreach (var code in codes)
                    sb.Append(indent)
                      .Append($"if ($proc.ExitCode -eq {code}) {{ '3010' | Out-File (Join-Path $PSScriptRoot 'exitcode.txt') -NoNewline; exit 3010 }}")
                      .Append("\r\n");
                return sb.Append(m.Value).ToString();
            });
    }

    public const string NoArpEntryExit = "Write-Output 'No Add/Remove Programs entry of this product was found; nothing was uninstalled.'\r\nexit 1";

    public const string NoProvenUninstallExit =
        "Write-Output 'This definition has no uninstall command and no silent uninstall arguments. Nothing was uninstalled. Add the uninstall command or the silent uninstall arguments to the definition, then stage it again.'\r\n"
        + "'1' | Out-File (Join-Path $PSScriptRoot 'exitcode.txt') -NoNewline\r\n"
        + "exit 1";

    /// <summary>
    /// Uninstall body that locates this product's own Add/Remove Programs
    /// entry before running its uninstaller. Candidates are tried from most
    /// to least specific: the detection key name, an uninstaller inside the
    /// detection folder, then a display-name match. A scan that ran the
    /// first uninstaller found removes an unrelated product on any machine
    /// carrying more than one product of the same installer engine.
    /// The entries of the machine (both registry views) and of the running
    /// account are searched, so a per-user product is found too.
    /// Inno Setup entries must name an unins*.exe. With no match the script
    /// exits 1 and never starts the staged installer: an installer started
    /// with arguments nobody proved can install, or wait for input in
    /// session 0 until the client times out.
    /// </summary>
    public static string BuildArpLookupUninstall(string exeFileName, string uninstallArgs,
        string? installerFramework, string? applicationName, string? arpKeyName, string? installFolder)
    {
        // The trailing architecture tag is the definition's, not the vendor's:
        // "Notepad++ (x64)" has to match "Notepad++ (64-bit x64)".
        var matchName = applicationName ?? exeFileName.Replace(".exe", "");
        matchName = System.Text.RegularExpressions.Regex.Replace(matchName, @"\s*\(x\d+\)\s*$", "").Trim();

        var innoFilter = installerFramework is "InnoSetup"
            ? " -and $_.UninstallString -like '*unins*'"
            : "";

        return $$"""
            $arpPaths = @(
                'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall',
                'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall',
                'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall',
                'HKCU:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall'
            )
            function Get-UninstallerPath([string]$command) {
                if ($command -match '^\s*"([^"]+)"') { return $matches[1] }
                $trimmed = $command.Trim()
                if (Test-Path -LiteralPath $trimmed) { return $trimmed }
                return ($trimmed -split '\s+[/-]', 2)[0].Trim()
            }
            $entries = @(foreach ($arpPath in $arpPaths) {
                Get-ChildItem $arpPath -ErrorAction SilentlyContinue | ForEach-Object {
                    Get-ItemProperty $_.PSPath -ErrorAction SilentlyContinue
                }
            })
            $entries = @($entries | Where-Object { $_.UninstallString -and $_.UninstallString -notlike '*msiexec*'{{innoFilter}} })
            $keyName = '{{EscapePsLiteral(arpKeyName ?? "")}}'
            $installFolder = '{{EscapePsLiteral(installFolder ?? "")}}'
            $displayName = '{{EscapeLikePattern(matchName)}}'
            $match = $null
            if ($keyName) {
                $match = $entries | Where-Object { $_.PSChildName -eq $keyName } | Select-Object -First 1
            }
            if (-not $match -and $installFolder) {
                $root = $installFolder.TrimEnd('\') + '\'
                $match = $entries | Where-Object { (Get-UninstallerPath $_.UninstallString).StartsWith($root, [System.StringComparison]::OrdinalIgnoreCase) } | Select-Object -First 1
            }
            if (-not $match -and $displayName) {
                $match = $entries | Where-Object { $_.DisplayName -like $displayName } | Select-Object -First 1
            }
            if ($match) {
                $uninstaller = Get-UninstallerPath $match.UninstallString
                if (Test-Path -LiteralPath $uninstaller) {
                    $proc = Start-Process -FilePath $uninstaller{{ArgumentList(uninstallArgs)}} -Wait -PassThru -NoNewWindow
                    $proc.ExitCode | Out-File (Join-Path $PSScriptRoot 'exitcode.txt') -NoNewline
                    exit $proc.ExitCode
                }
            }
            {{NoArpEntryExit}}
            """;
    }

    /// <summary>
    /// Wraps a display name in wildcards for a PowerShell -like pattern inside
    /// a single-quoted literal. Wildcard characters in the name are bracketed
    /// so a name such as "Foo [Beta]" matches itself, and an empty name yields
    /// an empty pattern so the caller skips the display-name step.
    /// </summary>
    public static string EscapeLikePattern(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";
        var sb = new StringBuilder("*");
        foreach (var c in name)
        {
            switch (c)
            {
                case '*': case '?': case '[': case ']': sb.Append('[').Append(c).Append(']'); break;
                case var q when PowerShellLiteral.IsSingleQuote(q): sb.Append(q).Append(q); break;
                default: sb.Append(c); break;
            }
        }
        return sb.Append('*').ToString();
    }

    /// <summary>
    /// The two lookup hints the uninstall wrapper takes from a detection rule:
    /// the Add/Remove Programs key name when the rule reads such a key, and
    /// the install folder when the rule checks a file. A compound rule
    /// contributes the first clause of each kind.
    /// </summary>
    public static (string? ArpKeyName, string? InstallFolder) ResolveUninstallHints(StageDetection? detection)
    {
        if (detection is null) return (null, null);

        string? keyName = null;
        string? folder = null;
        var clauses = detection.Clauses is { Length: > 0 } ? detection.Clauses : [detection];
        foreach (var clause in clauses)
        {
            var key = clause.RegistryKeyRelative;
            if (keyName is null && !string.IsNullOrWhiteSpace(key))
            {
                var marker = key.LastIndexOf(@"\Uninstall\", StringComparison.OrdinalIgnoreCase);
                if (marker >= 0)
                {
                    var leaf = key[(marker + @"\Uninstall\".Length)..].Trim('\\');
                    // An unresolved template token ({ProductCode}) is not a key name; a GUID leaf is.
                    if (leaf.Length > 0 && !leaf.Contains('\\')
                        && !System.Text.RegularExpressions.Regex.IsMatch(leaf, @"\{[A-Za-z_:]+\}"))
                        keyName = leaf;
                }
            }
            if (folder is null && !string.IsNullOrWhiteSpace(clause.FilePath) && !clause.FilePath.Contains('{'))
                folder = clause.FilePath;
        }
        return (keyName, folder);
    }

    /// <summary>
    /// Generates all 4 wrapper files for an MSIX/APPX package.
    /// The provisioned flow installs device-wide with Add-AppxProvisionedPackage
    /// so new user profiles get the app; uninstall removes the provisioning
    /// entry and every per-user registration. Both bodies load the compression
    /// assemblies because Windows PowerShell 5.1 does not load them implicitly
    /// and the uninstall path re-reads the package manifest on the client.
    /// A signer thumbprint recorded at stage time is pinned into the install
    /// wrapper, which refuses a package signed by anything else. With a
    /// publisher id, uninstall removes only packages of that publisher; a
    /// bundle is removed as its Bundle package. A definition uninstall command
    /// replaces that removal; an error it raises exits the wrapper with 1, and
    /// a native command that fails exits it with the code of that command.
    /// </summary>
    public void GenerateMsixWrappers(string outputPath, string msixFileName,
        string? signerThumbprint = null, bool provisioned = true, string? publisherId = null,
        string? uninstallCommand = null)
    {
        var file = EscapePsLiteral(msixFileName);

        var install = new List<string>
        {
            ExitHelper,
            "Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem",
            $"$msixPath = Join-Path $PSScriptRoot '{file}'",
            "if (-not (Test-Path -LiteralPath $msixPath)) { Write-Error \"MSIX not found: $msixPath\"; Exit-Wrapper 1 }"
        };

        if (!string.IsNullOrWhiteSpace(signerThumbprint))
        {
            install.Add($"$expected = '{EscapePsLiteral(signerThumbprint!)}'");
            install.Add("$sig = Get-AuthenticodeSignature -LiteralPath $msixPath");
            install.Add("if ($sig.Status -ne 'Valid') { Write-Error \"MSIX signature not valid: $($sig.Status)\"; Exit-Wrapper 2 }");
            install.Add("if ($sig.SignerCertificate.Thumbprint -ne $expected) { Write-Error \"MSIX signed by unexpected cert (got $($sig.SignerCertificate.Thumbprint), expected $expected)\"; Exit-Wrapper 3 }");
        }

        // The AppX cmdlets report most failures as non-terminating errors; without
        // -ErrorAction Stop the wrapper would reach Exit-Wrapper 0 after a failed install.
        install.Add(provisioned
            ? "try { Add-AppxProvisionedPackage -Online -PackagePath $msixPath -SkipLicense -ErrorAction Stop | Out-Null } catch { [Console]::Error.WriteLine($_.Exception.Message); Exit-Wrapper 1 }"
            : "try { Add-AppxPackage -Path $msixPath -ErrorAction Stop } catch { [Console]::Error.WriteLine($_.Exception.Message); Exit-Wrapper 1 }");
        install.Add("Exit-Wrapper 0");

        if (!string.IsNullOrWhiteSpace(uninstallCommand))
        {
            WriteWrappers(outputPath,
                string.Join("\r\n", install),
                string.Join("\r\n",
                    ExitHelper,
                    "$ErrorActionPreference = 'Stop'",
                    "try {",
                    "    " + uninstallCommand.Trim(),
                    // A native command that fails sets $LASTEXITCODE and raises no error.
                    "    if ($LASTEXITCODE) { Exit-Wrapper $LASTEXITCODE }",
                    "} catch {",
                    "    [Console]::Error.WriteLine('The uninstall command failed: ' + $_.Exception.Message)",
                    "    Exit-Wrapper 1",
                    "}",
                    "Exit-Wrapper 0"));
            return;
        }

        var uninstall = new List<string>
        {
            ExitHelper,
            "Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem",
            $"$msixPath = Join-Path $PSScriptRoot '{file}'",
            // Content already removed from the client cache leaves nothing to
            // uninstall; that is a success, not a failure.
            "if (-not (Test-Path -LiteralPath $msixPath)) { Exit-Wrapper 0 }",
            "$zip = [System.IO.Compression.ZipFile]::OpenRead($msixPath)",
            "try {",
            "    $entry = $zip.GetEntry('AppxManifest.xml')",
            "    $packageType = 'Main, Framework, Resource, Optional'",
            // A bundle carries its identity in the bundle manifest and has no AppxManifest.xml at its root.
            "    if (-not $entry) { $entry = $zip.GetEntry('AppxMetadata/AppxBundleManifest.xml'); $packageType = 'Bundle' }",
            "    if (-not $entry) { Write-Error 'AppxManifest.xml not found in MSIX'; Exit-Wrapper 4 }",
            "    $reader = New-Object System.IO.StreamReader($entry.Open())",
            "    try { [xml]$manifest = $reader.ReadToEnd() } finally { $reader.Dispose() }",
            "} finally { $zip.Dispose() }",
            "$identity = $manifest.DocumentElement.Identity"
        };

        var publisher = string.IsNullOrWhiteSpace(publisherId) ? null : EscapePsLiteral(publisherId!);
        var provisionedMatch = publisher is null ? "" : $" -and $_.PackageName.EndsWith('_{publisher}')";
        var installedMatch = publisher is null ? "" : $" | Where-Object {{ $_.PublisherId -eq '{publisher}' }}";
        if (provisioned)
        {
            uninstall.Add("try {");
            uninstall.Add($"    $prov = Get-AppxProvisionedPackage -Online -ErrorAction Stop | Where-Object {{ $_.DisplayName -eq $identity.Name{provisionedMatch} }}");
            uninstall.Add("    foreach ($p in $prov) { Remove-AppxProvisionedPackage -Online -PackageName $p.PackageName -ErrorAction Stop | Out-Null }");
            uninstall.Add($"    Get-AppxPackage -AllUsers -Name $identity.Name -PackageTypeFilter $packageType -ErrorAction Stop{installedMatch} | ForEach-Object {{ Remove-AppxPackage -Package $_.PackageFullName -AllUsers -ErrorAction Stop }}");
            uninstall.Add("} catch { [Console]::Error.WriteLine($_.Exception.Message); Exit-Wrapper 1 }");
        }
        else
        {
            uninstall.Add($"try {{ Get-AppxPackage -Name $identity.Name -PackageTypeFilter $packageType -ErrorAction Stop{installedMatch} | Remove-AppxPackage -ErrorAction Stop }} catch {{ [Console]::Error.WriteLine($_.Exception.Message); Exit-Wrapper 1 }}");
        }
        uninstall.Add("Exit-Wrapper 0");

        WriteWrappers(outputPath,
            string.Join("\r\n", install),
            string.Join("\r\n", uninstall));
    }

    /// <summary>
    /// Generates all 4 wrapper files for a bootstrapper shipped inside its own
    /// offline layout. The layout writes a copy of the bootstrapper at its root,
    /// so the install wrapper runs that copy and the bundle consumes the local
    /// payload set instead of reaching the vendor from every client. Uninstall
    /// runs the same copy with the uninstall switch: the bundle removes what it
    /// installed without needing an ARP lookup.
    /// </summary>
    public void GenerateBootstrapperLayoutWrappers(string outputPath, BootstrapperLayoutConfig layout)
    {
        var file = EscapePsLiteral(ResolveBootstrapperFileName(layout));

        var installArgs = new List<string> { layout.QuietSwitch };
        if (!string.IsNullOrWhiteSpace(layout.InstallPath))
        {
            installArgs.Add("/installpath");
            installArgs.Add(layout.InstallPath);
        }
        if (layout.Features.Length > 0)
        {
            installArgs.Add("/features");
            installArgs.AddRange(layout.Features);
        }

        var install = string.Join("\r\n",
            ExitHelper,
            $"$setupPath = Join-Path $PSScriptRoot '{file}'",
            "if (-not (Test-Path -LiteralPath $setupPath)) { Write-Error \"Bootstrapper not found: $setupPath\"; Exit-Wrapper 1 }",
            $"$proc = Start-Process -FilePath $setupPath -ArgumentList @({FormatPsArgs(installArgs)}) -Wait -PassThru -NoNewWindow",
            "if (-not $proc) { Exit-Wrapper 1 }",
            "Exit-Wrapper $proc.ExitCode");

        // Content already removed from the client cache leaves nothing to
        // uninstall; that is a success, not a failure.
        var uninstall = string.Join("\r\n",
            ExitHelper,
            $"$setupPath = Join-Path $PSScriptRoot '{file}'",
            "if (-not (Test-Path -LiteralPath $setupPath)) { Exit-Wrapper 0 }",
            $"$proc = Start-Process -FilePath $setupPath -ArgumentList @({FormatPsArgs([layout.QuietSwitch, layout.UninstallSwitch])}) -Wait -PassThru -NoNewWindow",
            "if (-not $proc) { Exit-Wrapper 1 }",
            "Exit-Wrapper $proc.ExitCode");

        WriteWrappers(outputPath, install, uninstall);
    }

    /// <summary>
    /// Generates all 4 wrapper files for a self-extracting payload wrapping an
    /// inner setup executable. Extracting first and calling setup directly keeps
    /// the parameter set under the wrapper's control — the outer box forwards
    /// nothing — and leaves a readable setup log tree on the client. The
    /// extraction tree is removed once setup returns, and its exit code, not the
    /// cleanup's, is what the wrapper reports.
    /// </summary>
    public void GenerateExtractSetupWrappers(string outputPath, string payloadFileName,
        ExtractSetupConfig config, string installArgs, string uninstallArgs)
    {
        ValidateExtractSetup(config);

        var payload = EscapePsLiteral(payloadFileName);
        var extractDir = EscapePsLiteral(config.ExtractDirName);
        var innerSetup = EscapePsLiteral(config.InnerSetupPath);

        // The extract args carry the target folder, which is only known as a
        // runtime expression, so {extractdir} becomes a variable reference
        // rather than a literal.
        var extractArgList = FormatPsArgs(
            Tokenize(config.ExtractArgs),
            token => token.Contains("{extractdir}", StringComparison.OrdinalIgnoreCase)
                ? "(\"" + token.Replace("{extractdir}", "\" + $extractRoot", StringComparison.OrdinalIgnoreCase) + ")"
                : null);

        var preamble = new[]
        {
            ExitHelper,
            $"$mediaPath = Join-Path $PSScriptRoot '{payload}'",
            "if (-not (Test-Path -LiteralPath $mediaPath)) { Write-Error \"Payload not found: $mediaPath\"; Exit-Wrapper 1 }",
            $"$extractRoot = Join-Path $env:SystemDrive '{extractDir}'",
            "if (Test-Path -LiteralPath $extractRoot) { Remove-Item -LiteralPath $extractRoot -Recurse -Force -ErrorAction SilentlyContinue }",
            $"$box = Start-Process -FilePath $mediaPath -ArgumentList @({extractArgList}) -Wait -PassThru -NoNewWindow",
            "if (-not $box) { Exit-Wrapper 1 }",
            "if ($box.ExitCode -ne 0) { Exit-Wrapper $box.ExitCode }",
            $"$setup = Join-Path $extractRoot '{innerSetup}'",
            "if (-not (Test-Path -LiteralPath $setup)) { Write-Error \"Inner setup not found: $setup\"; Exit-Wrapper 2 }"
        };

        var cleanup = new[]
        {
            "$code = if ($proc) { $proc.ExitCode } else { 1 }",
            "Remove-Item -LiteralPath $extractRoot -Recurse -Force -ErrorAction SilentlyContinue",
            "Exit-Wrapper $code"
        };

        string Body(string args) => string.Join("\r\n",
            preamble
                .Append($"$proc = Start-Process -FilePath $setup -ArgumentList @({FormatPsArgs(Tokenize(args))}) -Wait -PassThru -NoNewWindow")
                .Concat(cleanup));

        WriteWrappers(outputPath, Body(installArgs), Body(uninstallArgs));
    }

    /// <summary>
    /// Setup refuses an extraction path containing a space, and the wrapper
    /// builds that path from the configured name, so a bad name has to fail at
    /// stage time rather than on every client.
    /// </summary>
    public static void ValidateExtractSetup(ExtractSetupConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.ExtractDirName))
            throw new InvalidOperationException("ExtractSetup.ExtractDirName is required.");
        if (config.ExtractDirName.Any(char.IsWhiteSpace))
            throw new InvalidOperationException(
                $"ExtractSetup.ExtractDirName must not contain whitespace: '{config.ExtractDirName}'");
        if (config.ExtractDirName.IndexOfAny([':', '/', '\\']) >= 0)
            throw new InvalidOperationException(
                $"ExtractSetup.ExtractDirName must be a single folder name, not a path: '{config.ExtractDirName}'");
        if (string.IsNullOrWhiteSpace(config.InnerSetupPath))
            throw new InvalidOperationException("ExtractSetup.InnerSetupPath is required.");
        if (!config.ExtractArgs.Contains("{extractdir}", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "ExtractSetup.ExtractArgs must carry the {extractdir} placeholder.");
    }

    /// <summary>
    /// The bootstrapper name the layout writes at its root, which is what both
    /// wrappers run.
    /// </summary>
    public static string ResolveBootstrapperFileName(BootstrapperLayoutConfig layout) =>
        !string.IsNullOrWhiteSpace(layout.BootstrapperFileName)
            ? layout.BootstrapperFileName
            : throw new InvalidOperationException("BootstrapperLayout.BootstrapperFileName is required.");

    /// <summary>
    /// A definition's uninstall command is either a PowerShell expression
    /// that assigns $proc or a plain command line. Pasted into the wrapper as
    /// written, a command line parses as an expression and fails at its first
    /// switch, so it becomes the Start-Process form: an absolute or
    /// environment-form executable through UninstallExpressionFromCommandLine,
    /// a bare file name as the staged file beside the wrapper.
    /// </summary>
    public static string? NormalizeUninstallCommand(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        if (command.Contains("$proc", StringComparison.Ordinal)) return command;
        var tokens = Tokenize(command);
        if (tokens.Count == 0) return null;
        var exe = Unquote(tokens[0]);
        if (exe.Contains('\\') || exe.Contains('%'))
            return UninstallExpressionFromCommandLine(command);
        var args = tokens.Skip(1).Select(Unquote).ToList();
        var argList = args.Count == 0
            ? ""
            : $" -ArgumentList @({string.Join(", ", args.Select(a => "'" + EscapePsLiteral(a) + "'"))})";
        return $"$exePath = Join-Path $PSScriptRoot '{EscapePsLiteral(exe)}'\r\n" +
               $"$proc = Start-Process -FilePath $exePath{argList} -Wait -PassThru -NoNewWindow";
    }

    /// <summary>Strips one pair of surrounding double quotes; inner quotes stay.</summary>
    private static string Unquote(string token) =>
        token.Length >= 2 && token[0] == '"' && token[^1] == '"' ? token[1..^1] : token;

    /// <summary>
    /// Moves a definition's uninstall expression to another install branch.
    /// The $proc = Start-Process statement that names the default branch's
    /// uninstaller is replaced by the branch's own uninstall lines; the
    /// statements around it (a process stop before, a wait for the
    /// uninstaller's temporary copy after) stay. Null when no top-level
    /// statement names that uninstaller: an expression that looks the
    /// uninstaller up itself would still find the other branch's product.
    /// </summary>
    public static string? MoveUninstallExpressionToBranch(string expression,
        IEnumerable<string> defaultUninstallerPaths, string branchUninstallCommandLine)
    {
        var replacement = UninstallExpressionFromCommandLine(branchUninstallCommandLine);
        if (replacement.Length == 0) return null;

        foreach (var (start, end) in TopLevelStatements(expression))
        {
            var statement = expression[start..end];
            var trimmed = statement.TrimStart();
            if (!trimmed.StartsWith("$proc", StringComparison.Ordinal)) continue;
            if (!defaultUninstallerPaths.Any(p => p.Length > 0 && statement.Contains(p, StringComparison.OrdinalIgnoreCase)))
                continue;
            var prefix = expression[..start].TrimEnd();
            var suffix = expression[end..].TrimStart();
            var sb = new StringBuilder();
            if (prefix.Length > 0) sb.Append(prefix).Append("\r\n");
            sb.Append(replacement);
            if (suffix.Length > 0) sb.Append(suffix.StartsWith(';') ? "" : "\r\n").Append(suffix);
            return sb.ToString();
        }
        return null;
    }

    /// <summary>
    /// Ranges of the statements separated by ';' or a line break outside
    /// quotes, braces and parentheses; the separator is not part of a range.
    /// </summary>
    private static IEnumerable<(int Start, int End)> TopLevelStatements(string text)
    {
        var start = 0;
        var depth = 0;
        char? quote = null;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quote is not null)
            {
                if (c == quote) quote = null;
                continue;
            }
            switch (c)
            {
                case '\'' or '"': quote = c; break;
                case '{' or '(' or '[': depth++; break;
                case '}' or ')' or ']': depth--; break;
                case ';' or '\n' when depth == 0:
                    yield return (start, i);
                    start = i + 1;
                    break;
            }
        }
        if (start < text.Length) yield return (start, text.Length);
    }

    /// <summary>
    /// Turns a plain uninstall command line ("C:\App\unins000.exe" /VERYSILENT)
    /// into the uninstall expression the EXE wrapper embeds: the executable is
    /// environment-expanded on the client, so a %LOCALAPPDATA% path resolves
    /// in the user's session, and a missing uninstaller exits 0 because the
    /// product is already gone.
    /// </summary>
    public static string UninstallExpressionFromCommandLine(string commandLine)
    {
        var tokens = Tokenize(commandLine);
        if (tokens.Count == 0) return "";
        var exe = Unquote(tokens[0]);
        // A bare file name is the switch table's placeholder, not a location;
        // the Add/Remove Programs lookup finds the real uninstaller instead.
        if (!exe.Contains('\\') && !exe.Contains('%')) return "";
        var args = tokens.Skip(1).Select(Unquote).ToList();
        var lines = new List<string>
        {
            $"$uninstaller = [Environment]::ExpandEnvironmentVariables('{EscapePsLiteral(exe)}')",
            "if (-not (Test-Path -LiteralPath $uninstaller)) { exit 0 }",
            args.Count == 0
                ? "$proc = Start-Process -FilePath $uninstaller -Wait -PassThru -NoNewWindow"
                : $"$proc = Start-Process -FilePath $uninstaller -ArgumentList @({string.Join(", ", args.Select(a => "'" + EscapePsLiteral(a) + "'"))}) -Wait -PassThru -NoNewWindow"
        };
        return string.Join("\r\n", lines);
    }

    /// <summary>
    /// Splits a command line on whitespace outside double quotes. Quote
    /// characters are kept: an installer that wants a quoted value
    /// (/SQLSYSADMINACCOUNTS="BUILTIN\Administrators") receives one, because
    /// the whole token is passed as a single PowerShell argument.
    /// </summary>
    public static List<string> Tokenize(string commandLine)
    {
        var tokens = new List<string>();
        if (string.IsNullOrWhiteSpace(commandLine)) return tokens;

        var current = new StringBuilder();
        var inQuotes = false;
        foreach (var c in commandLine)
        {
            if (c == '"') { inQuotes = !inQuotes; current.Append(c); }
            else if (!inQuotes && char.IsWhiteSpace(c))
            {
                if (current.Length > 0) { tokens.Add(current.ToString()); current.Clear(); }
            }
            else current.Append(c);
        }
        if (current.Length > 0) tokens.Add(current.ToString());
        return tokens;
    }

    /// <summary>
    /// Renders tokens as a PowerShell argument list. Each token becomes a
    /// single-quoted literal unless <paramref name="expression"/> returns a
    /// replacement for it.
    /// </summary>
    private static string FormatPsArgs(IEnumerable<string> tokens, Func<string, string?>? expression = null) =>
        string.Join(", ", tokens.Select(t => expression?.Invoke(t) ?? $"'{EscapePsLiteral(t)}'"));

    private const string ExitHelper =
        "function Exit-Wrapper([int]$code) { $code | Out-File (Join-Path $PSScriptRoot 'exitcode.txt') -NoNewline; exit $code }";

    /// <summary>A bare quote character would terminate the single-quoted literal early.</summary>
    private static string EscapePsLiteral(string value) => PowerShellLiteral.EscapeSingleQuoted(value);

    /// <summary>
    /// The -ArgumentList clause for one argument string, with its leading
    /// space. Windows PowerShell refuses an empty -ArgumentList, Start-Process
    /// then starts nothing, so an empty string emits no clause.
    /// </summary>
    private static string ArgumentList(string? arguments) =>
        string.IsNullOrWhiteSpace(arguments) ? "" : $" -ArgumentList '{EscapePsLiteral(arguments)}'";

    private const string NullProcessGuard = "if (-not $proc) { exit 1 }";

    /// <summary>
    /// Inserts an exit 1 before every exit-code write. A Start-Process that
    /// fails leaves $proc null, and exit $null reports success.
    /// </summary>
    public static string GuardNullProcess(string script) =>
        System.Text.RegularExpressions.Regex.Replace(script,
            @"(?m)^([ \t]*)(?=" + System.Text.RegularExpressions.Regex.Escape(ExitCodeFileLine) + ")",
            m => m.Groups[1].Value + NullProcessGuard + "\r\n" + m.Groups[1].Value);

    /// <summary>
    /// True when deployment scripts are signed: the .bat launchers then carry
    /// no execution-policy argument and the endpoint's policy governs.
    /// </summary>
    public bool SignedLaunchers { get; init; }

    /// <summary>x64 or x86; x86 launches the SysWOW64 Windows PowerShell host.</summary>
    public string ScriptHost { get; init; } = DeploymentLauncher.HostX64;

    internal void WriteWrappers(string outputPath, string installPs1Content, string uninstallPs1Content)
    {
        var encoding = new UTF8Encoding(false); // ASCII-compatible, no BOM

        WriteIfMissing(Path.Combine(outputPath, "install.ps1"), installPs1Content, encoding);
        WriteIfMissing(Path.Combine(outputPath, "uninstall.ps1"), uninstallPs1Content, encoding);
        WriteIfMissing(Path.Combine(outputPath, "install.bat"),
            DeploymentLauncher.Build("install.ps1", SignedLaunchers, ScriptHost).BatBody, encoding);
        WriteIfMissing(Path.Combine(outputPath, "uninstall.bat"),
            DeploymentLauncher.Build("uninstall.ps1", SignedLaunchers, ScriptHost).BatBody, encoding);
    }

    private static void WriteIfMissing(string path, string content, Encoding encoding)
    {
        if (!File.Exists(path))
            File.WriteAllText(path, content, encoding);
    }
}
