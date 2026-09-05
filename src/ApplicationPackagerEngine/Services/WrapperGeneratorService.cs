using System.Text;
using ApplicationPackagerEngine.Models;

namespace ApplicationPackagerEngine.Services;

/// <summary>
/// Generates install.bat/ps1 and uninstall.bat/ps1 wrapper files.
/// Matches the PS1 Write-ContentWrappers + New-MsiWrapperContent output exactly.
/// </summary>
public sealed class WrapperGeneratorService
{
    /// <summary>
    /// Generates all 4 wrapper files for an MSI installer.
    /// </summary>
    public void GenerateMsiWrappers(string outputPath, string msiFileName, string? uninstallCommand = null, string? installArgs = null)
    {
        var msiArgs = string.IsNullOrEmpty(installArgs) ? "/qn /norestart" : installArgs;
        var installPs1 = $"""
            $msiPath = Join-Path $PSScriptRoot '{msiFileName}'
            $proc = Start-Process -FilePath 'msiexec.exe' -ArgumentList @('/i', "`"$msiPath`"", '{msiArgs}') -Wait -PassThru -NoNewWindow
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
                    $msiPath = Join-Path $PSScriptRoot '{{msiFileName}}'
                    $proc = Start-Process -FilePath 'msiexec.exe' -ArgumentList @('/x', "`"$msiPath`"", '/qn', '/norestart') -Wait -PassThru -NoNewWindow
                }
                $proc.ExitCode | Out-File (Join-Path $PSScriptRoot 'exitcode.txt') -NoNewline
                exit $proc.ExitCode
                """;
        }

        WriteWrappers(outputPath, installPs1, uninstallPs1);
    }

    /// <summary>
    /// Generates all 4 wrapper files for an EXE installer.
    /// Uninstall runs the definition's command when it has one; otherwise it
    /// resolves the product's Add/Remove Programs entry (see
    /// BuildArpLookupUninstall) using the detection key name and install
    /// folder as lookup hints.
    /// </summary>
    public void GenerateExeWrappers(string outputPath, string exeFileName,
        string installArgs, string uninstallArgs, string? installerFramework = null,
        string? applicationName = null, string? uninstallCommand = null,
        string? postInstallScript = null, string? arpKeyName = null, string? installFolder = null)
    {
        var installPs1 = $"""
            $exePath = Join-Path $PSScriptRoot '{exeFileName}'
            $proc = Start-Process -FilePath $exePath -ArgumentList '{installArgs}' -Wait -PassThru -NoNewWindow
            $proc.ExitCode | Out-File (Join-Path $PSScriptRoot 'exitcode.txt') -NoNewline
            exit $proc.ExitCode
            """;

        if (!string.IsNullOrWhiteSpace(postInstallScript))
        {
            // Post-install configuration runs only after a successful install and
            // reports 0, so a configuration step never masks the installer's code.
            installPs1 = $$"""
                $exePath = Join-Path $PSScriptRoot '{{exeFileName}}'
                $proc = Start-Process -FilePath $exePath -ArgumentList '{{installArgs}}' -Wait -PassThru -NoNewWindow
                $proc.ExitCode | Out-File (Join-Path $PSScriptRoot 'exitcode.txt') -NoNewline
                if ($proc.ExitCode -ne 0) { exit $proc.ExitCode }
                {{postInstallScript!.Trim().Replace("\r\n", "\n").Replace("\n", "\r\n")}}
                exit 0
                """;
        }

        // Framework-aware uninstall script
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
        else
        {
            uninstallPs1 = BuildArpLookupUninstall(exeFileName, uninstallArgs, installerFramework,
                applicationName, arpKeyName, installFolder);
        }

        WriteWrappers(outputPath, installPs1, uninstallPs1);
    }

    /// <summary>
    /// Uninstall body that locates this product's own Add/Remove Programs
    /// entry before running its uninstaller. Candidates are tried from most
    /// to least specific: the detection key name, an uninstaller inside the
    /// detection folder, then a display-name match. A scan that ran the
    /// first uninstaller found removes an unrelated product on any machine
    /// carrying more than one product of the same installer engine.
    /// Inno Setup entries must name an unins*.exe; with no match the body
    /// exits 0 because re-running the setup with silent switches installs.
    /// NSIS exits 1 with no match. Other engines fall back to the staged
    /// installer with the uninstall arguments, which is how a Burn bundle
    /// removes itself.
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

        var noMatch = installerFramework switch
        {
            "InnoSetup" => "exit 0",
            "NSIS" => "exit 1",
            _ => $$"""
                $exePath = Join-Path $PSScriptRoot '{{EscapePsLiteral(exeFileName)}}'
                $proc = Start-Process -FilePath $exePath -ArgumentList '{{uninstallArgs}}' -Wait -PassThru -NoNewWindow
                $proc.ExitCode | Out-File (Join-Path $PSScriptRoot 'exitcode.txt') -NoNewline
                exit $proc.ExitCode
                """
        };

        return $$"""
            $arpPaths = @(
                'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall',
                'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall'
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
                    $proc = Start-Process -FilePath $uninstaller -ArgumentList '{{uninstallArgs}}' -Wait -PassThru -NoNewWindow
                    $proc.ExitCode | Out-File (Join-Path $PSScriptRoot 'exitcode.txt') -NoNewline
                    exit $proc.ExitCode
                }
            }
            {{noMatch}}
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
                case '\'': sb.Append("''"); break;
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
    /// wrapper, which refuses a package signed by anything else.
    /// </summary>
    public void GenerateMsixWrappers(string outputPath, string msixFileName,
        string? signerThumbprint = null, bool provisioned = true)
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

        install.Add(provisioned
            ? "Add-AppxProvisionedPackage -Online -PackagePath $msixPath -SkipLicense | Out-Null"
            : "Add-AppxPackage -Path $msixPath");
        install.Add("Exit-Wrapper 0");

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
            "    if (-not $entry) { Write-Error 'AppxManifest.xml not found in MSIX'; Exit-Wrapper 4 }",
            "    $reader = New-Object System.IO.StreamReader($entry.Open())",
            "    try { [xml]$manifest = $reader.ReadToEnd() } finally { $reader.Dispose() }",
            "} finally { $zip.Dispose() }",
            "$identity = $manifest.Package.Identity"
        };

        if (provisioned)
        {
            uninstall.Add("$prov = Get-AppxProvisionedPackage -Online | Where-Object { $_.DisplayName -eq $identity.Name }");
            uninstall.Add("foreach ($p in $prov) { Remove-AppxProvisionedPackage -Online -PackageName $p.PackageName | Out-Null }");
            uninstall.Add("Get-AppxPackage -AllUsers -Name $identity.Name | ForEach-Object { Remove-AppxPackage -Package $_.PackageFullName -AllUsers }");
        }
        else
        {
            uninstall.Add("Get-AppxPackage -Name $identity.Name | Remove-AppxPackage");
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
            "Exit-Wrapper $proc.ExitCode");

        // Content already removed from the client cache leaves nothing to
        // uninstall; that is a success, not a failure.
        var uninstall = string.Join("\r\n",
            ExitHelper,
            $"$setupPath = Join-Path $PSScriptRoot '{file}'",
            "if (-not (Test-Path -LiteralPath $setupPath)) { Exit-Wrapper 0 }",
            $"$proc = Start-Process -FilePath $setupPath -ArgumentList @({FormatPsArgs([layout.QuietSwitch, layout.UninstallSwitch])}) -Wait -PassThru -NoNewWindow",
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
            "if ($box.ExitCode -ne 0) { Exit-Wrapper $box.ExitCode }",
            $"$setup = Join-Path $extractRoot '{innerSetup}'",
            "if (-not (Test-Path -LiteralPath $setup)) { Write-Error \"Inner setup not found: $setup\"; Exit-Wrapper 2 }"
        };

        var cleanup = new[]
        {
            "$code = $proc.ExitCode",
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

    /// <summary>
    /// The .bat wrapper reads the real exit code back from exitcode.txt because
    /// powershell.exe only surfaces 0 or 1 to cmd.exe.
    /// </summary>
    private const string ExitHelper =
        "function Exit-Wrapper([int]$code) { $code | Out-File (Join-Path $PSScriptRoot 'exitcode.txt') -NoNewline; exit $code }";

    /// <summary>A bare apostrophe would terminate the single-quoted literal early.</summary>
    private static string EscapePsLiteral(string value) => value.Replace("'", "''");

    private static void WriteWrappers(string outputPath, string installPs1Content, string uninstallPs1Content)
    {
        var encoding = new UTF8Encoding(false); // ASCII-compatible, no BOM

        // PowerShell only returns 0 or 1 to cmd.exe. The ps1 writes the real
        // exit code to exitcode.txt, and the bat reads it back for the true value.
        var batTemplate = """
            @echo off
            del /q "%~dp0exitcode.txt" >nul 2>&1
            powershell.exe -NonInteractive -ExecutionPolicy Bypass -File "%~dp0{0}"
            if exist "%~dp0exitcode.txt" (
                set /p REALCODE=<"%~dp0exitcode.txt"
                del /q "%~dp0exitcode.txt" >nul 2>&1
                exit /b %REALCODE%
            )
            exit /b %ERRORLEVEL%
            """;

        WriteIfMissing(Path.Combine(outputPath, "install.ps1"), installPs1Content, encoding);
        WriteIfMissing(Path.Combine(outputPath, "uninstall.ps1"), uninstallPs1Content, encoding);
        WriteIfMissing(Path.Combine(outputPath, "install.bat"), string.Format(batTemplate, "install.ps1"), encoding);
        WriteIfMissing(Path.Combine(outputPath, "uninstall.bat"), string.Format(batTemplate, "uninstall.ps1"), encoding);
    }

    private static void WriteIfMissing(string path, string content, Encoding encoding)
    {
        if (!File.Exists(path))
            File.WriteAllText(path, content, encoding);
    }
}
