using System.Text.RegularExpressions;
using ApplicationPackagerEngine.Models;

namespace ApplicationPackagerEngine.Services;

/// <summary>
/// Gives each target its own random secret for an installer argument, such as
/// the administrator password of a database server. The staged content holds
/// no secret: install.ps1 creates a 32 character value on the target, stores it
/// machine-protected (DPAPI, LocalMachine) in a folder only SYSTEM,
/// Administrators and the installing account can read, and passes it where the
/// install arguments carry the {TargetSecret} token. A later run reuses the
/// stored value, but only from a folder those principals own and alone can
/// access. When the secret cannot be created, stored or trusted, the script
/// exits 1 before the installer starts.
/// </summary>
public static partial class TargetSecretScript
{
    /// <summary>The install-argument token that stands for the secret.</summary>
    public const string Token = "{TargetSecret}";

    /// <summary>The PowerShell variable that holds the secret in install.ps1.</summary>
    public const string VariableName = "targetSecret";

    /// <summary>What the manifest shows in place of the token.</summary>
    public const string ManifestText = "<generated on each target>";

    [GeneratedRegex(@"^[A-Za-z0-9-]{1,64}$")]
    private static partial Regex FolderPattern();

    [GeneratedRegex(@"^[A-Za-z0-9.-]{1,64}$")]
    private static partial Regex FilePattern();

    [GeneratedRegex(@"^[A-Za-z0-9 ]{1,40}$")]
    private static partial Regex LabelPattern();

    /// <summary>A single-quoted PowerShell literal, with doubled quotes inside.</summary>
    [GeneratedRegex(@"'(?:[^']|'')*'")]
    private static partial Regex SingleQuoted();

    /// <summary>The install-script lines that leave the secret in <c>$targetSecret</c>.</summary>
    public static string BuildBlock(TargetSecretConfig config)
    {
        if (!FolderPattern().IsMatch(config.FolderName ?? ""))
            throw new InvalidOperationException($"TargetSecret.FolderName '{config.FolderName}' is not usable.");
        if (!FilePattern().IsMatch(config.FileName ?? ""))
            throw new InvalidOperationException($"TargetSecret.FileName '{config.FileName}' is not usable.");
        var label = string.IsNullOrWhiteSpace(config.Label) ? "product" : config.Label;
        if (!LabelPattern().IsMatch(label))
            throw new InvalidOperationException($"TargetSecret.Label '{label}' is not usable.");

        var v = VariableName;
        return string.Join("\r\n",
            "try {",
            "    Add-Type -AssemblyName System.Security -ErrorAction Stop",
            $"    $secretDir = Join-Path $env:ProgramData '{config.FolderName}'",
            $"    $secretFile = Join-Path $secretDir '{config.FileName}'",
            "    $trusted = @('S-1-5-18', 'S-1-5-32-544', [System.Security.Principal.WindowsIdentity]::GetCurrent().User.Value)",
            "    if (Test-Path -LiteralPath $secretDir) {",
            "        # A folder that already exists is used only when SYSTEM, Administrators or this account own it and",
            "        # are the only principals it grants access to; a standard user can create the folder first.",
            "        $acl = Get-Acl -LiteralPath $secretDir",
            "        $ownerSid = $acl.GetOwner([System.Security.Principal.SecurityIdentifier]).Value",
            "        if ($trusted -notcontains $ownerSid) { throw \"the folder $secretDir is owned by $ownerSid\" }",
            "        foreach ($rule in $acl.Access) {",
            "            $ruleSid = $rule.IdentityReference.Translate([System.Security.Principal.SecurityIdentifier]).Value",
            "            if ($rule.AccessControlType -eq 'Allow' -and $trusted -notcontains $ruleSid) { throw \"the folder $secretDir grants access to $ruleSid\" }",
            "        }",
            "    }",
            "    else {",
            "        New-Item -ItemType Directory -Path $secretDir -Force -ErrorAction Stop | Out-Null",
            "        $grants = @('*S-1-5-18:(OI)(CI)F', '*S-1-5-32-544:(OI)(CI)F')",
            "        if ($trusted[2] -ne 'S-1-5-18') { $grants += ('*' + $trusted[2] + ':(OI)(CI)F') }",
            "        & icacls.exe $secretDir /inheritance:r /grant:r $grants | Out-Null",
            "        if ($LASTEXITCODE -ne 0) { Remove-Item -LiteralPath $secretDir -Recurse -Force -ErrorAction SilentlyContinue; throw \"icacls failed with exit code $LASTEXITCODE\" }",
            "    }",
            "    if (Test-Path -LiteralPath $secretFile) {",
            "        $plain = [System.Security.Cryptography.ProtectedData]::Unprotect([System.IO.File]::ReadAllBytes($secretFile), $null, [System.Security.Cryptography.DataProtectionScope]::LocalMachine)",
            $"        ${v} = [System.Text.Encoding]::UTF8.GetString($plain)",
            "    }",
            "    else {",
            "        $alphabet = 'ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789'",
            "        $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()",
            "        $chars = New-Object char[] 32",
            "        $byte = New-Object byte[] 1",
            "        for ($index = 0; $index -lt $chars.Length; ) {",
            "            $rng.GetBytes($byte)",
            "            if ($byte[0] -lt (256 - (256 % $alphabet.Length))) { $chars[$index] = $alphabet[$byte[0] % $alphabet.Length]; $index++ }",
            "        }",
            "        $rng.Dispose()",
            $"        ${v} = -join $chars",
            $"        $protected = [System.Security.Cryptography.ProtectedData]::Protect([System.Text.Encoding]::UTF8.GetBytes(${v}), $null, [System.Security.Cryptography.DataProtectionScope]::LocalMachine)",
            "        [System.IO.File]::WriteAllBytes($secretFile, $protected)",
            "    }",
            "}",
            "catch {",
            $"    Write-Error (\"Could not create the {label} secret: \" + $_.Exception.Message)",
            "    exit 1",
            "}");
    }

    /// <summary>
    /// The install script with the secret block in front, and each single-quoted
    /// literal that carries the token turned into a string expression that
    /// joins the variable. A script without the token is refused: the secret
    /// would be created and never used.
    /// </summary>
    public static string Apply(string installScript, TargetSecretConfig config)
    {
        var block = BuildBlock(config);
        var found = false;
        // Only a line that carries the token is rewritten: elsewhere an apostrophe in a comment
        // or a double-quoted string would pair the quotes wrongly.
        var lines = installScript.Split('\n').Select(line =>
        {
            if (!line.Contains(Token, StringComparison.Ordinal)) return line;
            found = true;
            return SingleQuoted().Replace(line, m => m.Value.Contains(Token, StringComparison.Ordinal)
                ? "(" + m.Value.Replace(Token, "' + $" + VariableName + " + '", StringComparison.Ordinal) + ")"
                : m.Value);
        }).ToList();
        var body = string.Join('\n', lines);
        if (!found)
            throw new InvalidOperationException($"The definition declares a TargetSecret, but its install arguments do not carry {Token}.");
        return block + "\r\n" + body;
    }

    /// <summary>Rewrites install.ps1 in a staged content folder.</summary>
    public static void ApplyToFile(string installPs1Path, TargetSecretConfig config)
    {
        var script = File.ReadAllText(installPs1Path);
        File.WriteAllText(installPs1Path, Apply(script, config), new System.Text.UTF8Encoding(false));
    }
}
