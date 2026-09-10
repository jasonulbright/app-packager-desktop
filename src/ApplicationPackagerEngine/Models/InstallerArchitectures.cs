namespace ApplicationPackagerEngine.Models;

/// <summary>
/// Architecture of an installer payload, in the engine's own spelling. Translation to
/// the Graph properties happens at publish time, where arm64 does not share a property
/// with x86 and x64.
///
/// This is not the registry or file system view a detection reads. A per-user x64
/// product writes its uninstall entry where a 32-bit view finds it, so a detection
/// carrying Is64Bit false says nothing about the payload it detects.
/// </summary>
public static class InstallerArchitectures
{
    public const string X86 = "x86";
    public const string X64 = "x64";
    public const string Arm64 = "ARM64";

    /// <summary>
    /// A manifest staged before the architecture field existed carries none. An absent
    /// value reads as x64, matching the default the publish options and the PowerShell
    /// publisher already apply, rather than being inferred from the detection view.
    /// </summary>
    public const string LegacyDefault = X64;

    /// <summary>
    /// Canonical spelling for a declared architecture. Absent or blank takes the
    /// compatibility default; anything outside the supported set is refused rather
    /// than folded into a neighbouring architecture.
    /// </summary>
    public static string Resolve(string? architecture)
    {
        var value = (architecture ?? "").Trim();
        if (value.Length == 0) return LegacyDefault;
        if (value.Equals(X86, StringComparison.OrdinalIgnoreCase)) return X86;
        if (value.Equals(X64, StringComparison.OrdinalIgnoreCase)) return X64;
        if (value.Equals(Arm64, StringComparison.OrdinalIgnoreCase)) return Arm64;
        throw new InvalidOperationException(
            $"Unsupported installer architecture '{value}'; expected {X86}, {X64} or {Arm64}.");
    }

    /// <summary>The manifest's declared payload architecture, never its detection view.</summary>
    public static string Resolve(StageManifest manifest) => Resolve(manifest.Architecture);
}
