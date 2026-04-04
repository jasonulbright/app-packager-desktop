using System.Diagnostics;
using System.Text;
using ApplicationPackagerEngine.Models;

namespace ApplicationPackagerEngine.Services;

/// <summary>
/// Creates MECM applications from staged content.
/// Shells out to Windows PowerShell 5.1 (powershell.exe) because the CM module
/// requires .NET Framework — it cannot run in a .NET 10 hosted runspace.
/// </summary>
public sealed class MecmTarget
{
    public async Task<PackagingResult> PackageAsync(
        StageManifest manifest,
        string stagedContentPath,
        string siteCode,
        string fileServerPath,
        string comment,
        int estimatedRuntimeMins = 15,
        int maximumRuntimeMins = 30,
        Action<string>? onProgress = null,
        CancellationToken ct = default)
    {
        try
        {
            var appName = manifest.AppName;
            var version = manifest.SoftwareVersion;
            var publisher = manifest.Publisher;
            var vendorFolder = SanitizeFolderName(publisher);
            var appFolder = manifest.DisplayName ?? appName;
            var networkRoot = Path.Combine(fileServerPath, "Applications", vendorFolder, appFolder);
            var networkContentPath = Path.Combine(networkRoot, version);

            // 0. Validate content share accessibility
            var shareRoot = Path.GetPathRoot(fileServerPath) ?? fileServerPath;
            if (fileServerPath.StartsWith(@"\\") && !Directory.Exists(fileServerPath))
            {
                return new PackagingResult
                {
                    Success = false,
                    ErrorMessage = $"Content share not accessible: {fileServerPath}"
                };
            }

            // 1. Copy content to network
            onProgress?.Invoke($"Network content path: {networkContentPath}");
            Directory.CreateDirectory(networkContentPath);

            foreach (var file in Directory.GetFiles(stagedContentPath, "*", SearchOption.AllDirectories))
            {
                var relativePath = Path.GetRelativePath(stagedContentPath, file);
                if (relativePath.Equals("stage-manifest.json", StringComparison.OrdinalIgnoreCase))
                    continue;

                var dest = Path.Combine(networkContentPath, relativePath);
                var destDir = Path.GetDirectoryName(dest)!;
                if (!Directory.Exists(destDir))
                    Directory.CreateDirectory(destDir);

                if (!File.Exists(dest))
                {
                    File.Copy(file, dest);
                    onProgress?.Invoke($"Copied: {relativePath}");
                }
                else
                {
                    onProgress?.Invoke($"Already on network: {relativePath}");
                }
            }

            // 2. Build the PowerShell script for CM app creation
            onProgress?.Invoke("Connecting to MECM...");
            var psScript = manifest.InstallerType?.Equals("MSIX", StringComparison.OrdinalIgnoreCase) == true
                ? BuildMsixMecmScript(manifest, appName, networkContentPath, siteCode, comment)
                : BuildMecmScript(manifest, appName, networkContentPath,
                    siteCode, comment, estimatedRuntimeMins, maximumRuntimeMins);

            // 3. Execute via Windows PowerShell 5.1
            var result = await RunWindowsPowerShellAsync(psScript, onProgress, ct);

            if (result.ExitCode == 0)
            {
                onProgress?.Invoke($"Packaged: {appName}");
                return new PackagingResult
                {
                    Success = true,
                    ApplicationName = appName,
                    NetworkContentPath = networkContentPath
                };
            }
            else
            {
                return new PackagingResult
                {
                    Success = false,
                    ErrorMessage = result.StdErr
                };
            }
        }
        catch (Exception ex)
        {
            return new PackagingResult { Success = false, ErrorMessage = ex.Message };
        }
    }

    private static string BuildMecmScript(
        StageManifest manifest, string appName, string networkContentPath,
        string siteCode, string comment, int estRuntime, int maxRuntime)
    {
        var det = manifest.Detection;
        var sb = new StringBuilder();

        // Import CM module
        sb.AppendLine("$ErrorActionPreference = 'Stop'");
        sb.AppendLine("$cmModule = Join-Path $env:SMS_ADMIN_UI_PATH '..\\ConfigurationManager.psd1'");
        sb.AppendLine("if (Test-Path $cmModule) { Import-Module $cmModule }");
        sb.AppendLine("else { Import-Module ConfigurationManager }");

        // Create PSDrive if needed
        sb.AppendLine($"if (-not (Get-PSDrive -Name '{siteCode}' -ErrorAction SilentlyContinue)) {{");
        sb.AppendLine($"    New-PSDrive -Name '{siteCode}' -PSProvider CMSite -Root $env:COMPUTERNAME | Out-Null");
        sb.AppendLine("}");
        sb.AppendLine($"Set-Location '{siteCode}:'");
        sb.AppendLine("Write-Host 'Connected to MECM'");

        // Check for existing app (catch the "key" bug)
        sb.AppendLine("$existing = $null");
        sb.AppendLine($"try {{ $existing = Get-CMApplication -Name '{Esc(appName)}' -Fast -ErrorAction SilentlyContinue }} catch {{ }}");
        sb.AppendLine("if ($existing) { Write-Host 'Application already exists'; exit 0 }");

        // Create application
        sb.AppendLine("Write-Host 'Creating application...'");
        sb.Append($"$app = New-CMApplication -Name '{Esc(appName)}'");
        sb.Append($" -Publisher '{Esc(manifest.Publisher)}'");
        sb.Append($" -SoftwareVersion '{Esc(manifest.SoftwareVersion)}'");
        sb.Append($" -Description '{Esc(comment)}'");
        sb.Append(" -AutoInstall $true");
        if (!string.IsNullOrEmpty(manifest.DisplayName))
            sb.Append($" -LocalizedApplicationName '{Esc(manifest.DisplayName)}'");
        sb.AppendLine();

        sb.AppendLine("Write-Host \"CI_ID: $($app.CI_ID)\"");

        // Create detection clause (leave CM drive)
        sb.AppendLine("Set-Location C:");
        sb.AppendLine(BuildDetectionClausePs(det));

        // Reconnect to CM drive
        sb.AppendLine($"Set-Location '{siteCode}:'");

        // Add deployment type
        sb.AppendLine("Write-Host 'Adding deployment type...'");
        var isUserInstall = manifest.InstallContext.Equals("User", StringComparison.OrdinalIgnoreCase);
        sb.Append($"Add-CMScriptDeploymentType -ApplicationName '{Esc(appName)}'");
        sb.Append($" -DeploymentTypeName '{Esc(appName)}'");
        sb.Append($" -ContentLocation '{Esc(networkContentPath)}'");
        sb.Append(" -InstallCommand 'install.bat' -UninstallCommand 'uninstall.bat'");
        if (isUserInstall)
        {
            sb.Append(" -InstallationBehaviorType InstallForUser");
            // LogonRequirementType cannot be set when InstallationBehaviorType is InstallForUser
        }
        else
        {
            sb.Append(" -InstallationBehaviorType InstallForSystem");
            sb.Append(" -LogonRequirementType WhetherOrNotUserLoggedOn");
        }
        sb.Append($" -EstimatedRuntimeMins {estRuntime} -MaximumRuntimeMins {maxRuntime}");
        sb.Append(" -ContentFallback -SlowNetworkDeploymentMode Download");
        sb.Append(" -AddDetectionClause $clause");
        sb.AppendLine(" | Out-Null");

        // Clean up revision history (keep only latest)
        sb.AppendLine("try {");
        sb.AppendLine($"  $revisions = Get-CMApplicationRevisionHistory -Id $app.CI_ID -ErrorAction Stop");
        sb.AppendLine("  if (@($revisions).Count -gt 1) {");
        sb.AppendLine("    @($revisions) | Sort-Object -Property @{Expression={if ($_.Revision) {$_.Revision} else {$_.CIVersion}}} -Descending | Select-Object -Skip 1 | ForEach-Object {");
        sb.AppendLine("      Remove-CMApplicationRevisionHistory -InputObject $_ -Force -ErrorAction SilentlyContinue");
        sb.AppendLine("    }");
        sb.AppendLine("    Write-Host 'Cleaned revision history'");
        sb.AppendLine("  }");
        sb.AppendLine("} catch { Write-Host \"Revision cleanup skipped: $($_.Exception.Message)\" }");

        sb.AppendLine("Write-Host 'Application created successfully'");

        return sb.ToString();
    }

    private static string BuildDetectionClausePs(StageDetection det)
    {
        return det.Type switch
        {
            "File" when det.PropertyType == "Existence" =>
                $"$clause = New-CMDetectionClauseFile -Path '{Esc(det.FilePath ?? "")}' -FileName '{Esc(det.FileName ?? "")}' -Existence{(det.Is64Bit ? " -Is64Bit" : "")}",

            "File" =>
                $"$clause = New-CMDetectionClauseFile -Path '{Esc(det.FilePath ?? "")}' -FileName '{Esc(det.FileName ?? "")}' -PropertyType {det.PropertyType ?? "Version"} -Value -ExpressionOperator {det.Operator ?? "GreaterEquals"} -ExpectedValue '{Esc(det.ExpectedValue ?? "")}'{(det.Is64Bit ? " -Is64Bit" : "")}",

            "RegistryKeyValue" =>
                $"$clause = New-CMDetectionClauseRegistryKeyValue -Hive LocalMachine -KeyName '{Esc(det.RegistryKeyRelative ?? "")}' -ValueName '{Esc(det.ValueName ?? "DisplayVersion")}' -PropertyType {det.PropertyType ?? "String"} -Value -ExpressionOperator {det.Operator ?? "IsEquals"} -ExpectedValue '{Esc(det.ExpectedValue ?? "")}'{(det.Is64Bit ? " -Is64Bit" : "")}",

            "RegistryKey" =>
                $"$clause = New-CMDetectionClauseRegistryKey -Hive LocalMachine -KeyName '{Esc(det.RegistryKeyRelative ?? "")}' -Existence{(det.Is64Bit ? " -Is64Bit" : "")}",

            _ => throw new InvalidOperationException($"Unsupported detection type: {det.Type}")
        };
    }

    private static async Task<(int ExitCode, string StdOut, string StdErr)> RunWindowsPowerShellAsync(
        string script, Action<string>? onProgress, CancellationToken ct)
    {
        // Write script to temp file to avoid argument escaping issues
        var scriptPath = Path.Combine(Path.GetTempPath(), $"ap-mecm-{Guid.NewGuid():N}.ps1");
        await File.WriteAllTextAsync(scriptPath, script, Encoding.UTF8, ct);

        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            process.Start();

            var outLines = new List<string>();
            string? line;
            while ((line = await process.StandardOutput.ReadLineAsync(ct)) is not null)
            {
                outLines.Add(line);
                onProgress?.Invoke(line);
            }

            var stderr = await process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);

            return (process.ExitCode, string.Join(Environment.NewLine, outLines), stderr);
        }
        finally
        {
            try { File.Delete(scriptPath); } catch { }
        }
    }

    /// <summary>
    /// Builds the PowerShell script for creating an MSIX deployment type in MECM.
    /// CM handles detection, provisioning, and per-user registration natively for MSIX.
    /// No custom detection clause, install/uninstall commands, or wrappers needed.
    /// </summary>
    private static string BuildMsixMecmScript(
        StageManifest manifest, string appName, string networkContentPath,
        string siteCode, string comment)
    {
        var sb = new StringBuilder();

        // Import CM module
        sb.AppendLine("$ErrorActionPreference = 'Stop'");
        sb.AppendLine("$cmModule = Join-Path $env:SMS_ADMIN_UI_PATH '..\\ConfigurationManager.psd1'");
        sb.AppendLine("if (Test-Path $cmModule) { Import-Module $cmModule }");
        sb.AppendLine("else { Import-Module ConfigurationManager }");

        // Create PSDrive if needed
        sb.AppendLine($"if (-not (Get-PSDrive -Name '{siteCode}' -ErrorAction SilentlyContinue)) {{");
        sb.AppendLine($"    New-PSDrive -Name '{siteCode}' -PSProvider CMSite -Root $env:COMPUTERNAME | Out-Null");
        sb.AppendLine("}");
        sb.AppendLine($"Set-Location '{siteCode}:'");
        sb.AppendLine("Write-Host 'Connected to MECM'");

        // Check for existing app
        sb.AppendLine("$existing = $null");
        sb.AppendLine($"try {{ $existing = Get-CMApplication -Name '{Esc(appName)}' -Fast -ErrorAction SilentlyContinue }} catch {{ }}");
        sb.AppendLine("if ($existing) { Write-Host 'Application already exists'; exit 0 }");

        // Create application
        sb.AppendLine("Write-Host 'Creating application...'");
        sb.Append($"$app = New-CMApplication -Name '{Esc(appName)}'");
        sb.Append($" -Publisher '{Esc(manifest.Publisher)}'");
        sb.Append($" -SoftwareVersion '{Esc(manifest.SoftwareVersion)}'");
        sb.Append($" -Description '{Esc(comment)}'");
        sb.Append(" -AutoInstall $true");
        sb.AppendLine();

        sb.AppendLine("Write-Host \"CI_ID: $($app.CI_ID)\"");

        // Find the MSIX file in the content path
        sb.AppendLine($"$msixFile = Get-ChildItem -Path '{Esc(networkContentPath)}' -Filter '*.msix' -ErrorAction SilentlyContinue | Select-Object -First 1");
        sb.AppendLine("if (-not $msixFile) { $msixFile = Get-ChildItem -Path '" + Esc(networkContentPath) + "' -Filter '*.appx' -ErrorAction SilentlyContinue | Select-Object -First 1 }");
        sb.AppendLine("if (-not $msixFile) { throw 'No .msix or .appx file found in content path' }");

        // Add MSIX deployment type — CM handles detection automatically
        sb.AppendLine("Write-Host 'Adding MSIX deployment type...'");
        sb.Append($"Add-CMWindowsAppxDeploymentType -ApplicationName '{Esc(appName)}'");
        sb.Append($" -DeploymentTypeName '{Esc(appName)}'");
        sb.Append(" -ContentLocation $msixFile.FullName");
        sb.Append(" -ContentFallback -SlowNetworkDeploymentMode Download");
        sb.Append(" -Force");
        sb.AppendLine(" | Out-Null");

        // Clean up revision history
        sb.AppendLine("try {");
        sb.AppendLine($"  $revisions = Get-CMApplicationRevisionHistory -Id $app.CI_ID -ErrorAction Stop");
        sb.AppendLine("  if (@($revisions).Count -gt 1) {");
        sb.AppendLine("    @($revisions) | Sort-Object -Property @{Expression={if ($_.Revision) {$_.Revision} else {$_.CIVersion}}} -Descending | Select-Object -Skip 1 | ForEach-Object {");
        sb.AppendLine("      Remove-CMApplicationRevisionHistory -InputObject $_ -Force -ErrorAction SilentlyContinue");
        sb.AppendLine("    }");
        sb.AppendLine("    Write-Host 'Cleaned revision history'");
        sb.AppendLine("  }");
        sb.AppendLine("} catch { Write-Host \"Revision cleanup skipped: $($_.Exception.Message)\" }");

        sb.AppendLine("Write-Host 'Application created successfully'");

        return sb.ToString();
    }

    /// <summary>
    /// Distributes content for a packaged application to a DP group.
    /// Call after PackageAsync succeeds.
    /// </summary>
    public async Task<bool> DistributeAsync(
        string appName, string siteCode, string dpGroupName = "All DPs",
        Action<string>? onProgress = null, CancellationToken ct = default)
    {
        var sb = new StringBuilder();
        AppendCmPreamble(sb, siteCode);
        sb.AppendLine($"Start-CMContentDistribution -ApplicationName '{Esc(appName)}' -DistributionPointGroupName '{Esc(dpGroupName)}' -ErrorAction Stop");
        sb.AppendLine($"Write-Host 'Distributed: {Esc(appName)} to {Esc(dpGroupName)}'");

        var result = await RunWindowsPowerShellAsync(sb.ToString(), onProgress, ct);
        if (result.ExitCode != 0)
            onProgress?.Invoke($"Distribution failed: {result.StdErr}");
        return result.ExitCode == 0;
    }

    /// <summary>
    /// Creates a deployment for a packaged application to a collection.
    /// Call after DistributeAsync succeeds.
    /// </summary>
    public async Task<bool> DeployAsync(
        string appName, string siteCode, string collectionName,
        string purpose = "Required", bool overrideServiceWindow = true,
        Action<string>? onProgress = null, CancellationToken ct = default)
    {
        var sb = new StringBuilder();
        AppendCmPreamble(sb, siteCode);

        sb.Append($"New-CMApplicationDeployment -Name '{Esc(appName)}'");
        sb.Append($" -CollectionName '{Esc(collectionName)}'");
        sb.Append(" -DeployAction Install");
        sb.Append($" -DeployPurpose {purpose}");
        sb.Append(" -UserNotification DisplaySoftwareCenterOnly");
        if (purpose.Equals("Required", StringComparison.OrdinalIgnoreCase) && overrideServiceWindow)
        {
            sb.Append(" -OverrideServiceWindow $true");
            sb.Append(" -RebootOutsideServiceWindow $true");
        }
        sb.AppendLine(" -ErrorAction Stop | Out-Null");
        sb.AppendLine($"Write-Host 'Deployed: {Esc(appName)} to {Esc(collectionName)} ({purpose})'");

        var result = await RunWindowsPowerShellAsync(sb.ToString(), onProgress, ct);
        if (result.ExitCode != 0)
            onProgress?.Invoke($"Deployment failed: {result.StdErr}");
        return result.ExitCode == 0;
    }

    private static void AppendCmPreamble(StringBuilder sb, string siteCode)
    {
        sb.AppendLine("$ErrorActionPreference = 'Stop'");
        sb.AppendLine("$cmModule = Join-Path $env:SMS_ADMIN_UI_PATH '..\\ConfigurationManager.psd1'");
        sb.AppendLine("if (Test-Path $cmModule) { Import-Module $cmModule }");
        sb.AppendLine("else { Import-Module ConfigurationManager }");
        sb.AppendLine($"if (-not (Get-PSDrive -Name '{siteCode}' -ErrorAction SilentlyContinue)) {{");
        sb.AppendLine($"    New-PSDrive -Name '{siteCode}' -PSProvider CMSite -Root $env:COMPUTERNAME | Out-Null");
        sb.AppendLine("}");
        sb.AppendLine($"Set-Location '{siteCode}:'");
    }

    private static string Esc(string value) => value.Replace("'", "''");

    /// <summary>
    /// Sanitizes a string for use as a filesystem folder name.
    /// Strips trailing dots/spaces (Windows silently removes these), replaces commas
    /// and other problematic characters. Only allows alphanumeric, space, dot, hyphen, underscore.
    /// </summary>
    /// <summary>Public accessor for BareStaging and other services that need sanitized folder names.</summary>
    internal static string SanitizeFolderNamePublic(string name) => SanitizeFolderName(name);

    private static string SanitizeFolderName(string name)
    {
        // Replace commas and other special chars with nothing
        var sanitized = System.Text.RegularExpressions.Regex.Replace(name, @"[^a-zA-Z0-9 .\-_]", "");
        // Collapse multiple spaces
        sanitized = System.Text.RegularExpressions.Regex.Replace(sanitized, @"\s{2,}", " ");
        // Trim trailing dots and spaces (Windows strips these silently, causing path mismatches)
        sanitized = sanitized.TrimEnd('.', ' ');
        return sanitized;
    }
}

public sealed class PackagingResult
{
    public bool Success { get; init; }
    public string ApplicationName { get; init; } = "";
    public string CiId { get; init; } = "";
    public string NetworkContentPath { get; init; } = "";
    public string? ErrorMessage { get; init; }
    public bool AlreadyExists { get; init; }
}
