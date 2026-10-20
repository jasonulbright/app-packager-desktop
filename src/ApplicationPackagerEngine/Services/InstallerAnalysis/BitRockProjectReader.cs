using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace ApplicationPackagerEngine.Services;

/// <summary>The project settings a BitRock InstallBuilder setup carries, with the values resolved for Windows.</summary>
public sealed class BitRockProject
{
    public string ShortName { get; init; } = "";
    public string FullName { get; init; } = "";
    public string Version { get; init; } = "";
    public string Vendor { get; init; } = "";
    /// <summary>productDisplayName with the project variables replaced; empty when absent or not resolvable.</summary>
    public string DisplayName { get; init; } = "";
    /// <summary>The DisplayVersion a registry action writes to the uninstall key, or the project version.</summary>
    public string DisplayVersion { get; init; } = "";
    public bool DisplayVersionFromRegistryAction { get; init; }
    /// <summary>True when the registry action that writes DisplayVersion has rules beyond the platform test.</summary>
    public bool DisplayVersionConditional { get; init; }
    /// <summary>The uninstall key name: windowsARPRegistryPrefix, or the InstallBuilder default "fullName version".</summary>
    public string ArpKeyName { get; init; } = "";
    public bool ArpKeyNameInProject { get; init; }
    /// <summary>windows64bitMode: true for 1, false for 0, null when the project does not set it.</summary>
    public bool? Windows64BitMode { get; init; }
    public string UninstallerName { get; init; } = "";
    /// <summary>project (a project setting), action (a setInstallerVariable action) or default.</summary>
    public string UninstallerNameSource { get; init; } = "";
    /// <summary>The default install folder in environment-variable form; empty when it is not resolvable.</summary>
    public string InstallDir { get; init; } = "";
    public string RequireInstallationByRootUser { get; init; } = "";
    /// <summary>True when a rule in the project tests installer_ui for unattended, the mode "--mode unattended" selects.</summary>
    public bool UnattendedModeHandled { get; init; }
    /// <summary>The project's unattendedModeUI default (none, minimal, minimalWithDialogs); empty when not set.</summary>
    public string UnattendedModeUI { get; init; } = "";
}

/// <summary>
/// Reads project.xml from the runtime archive at the end of a BitRock InstallBuilder setup. The
/// runtime is a Metakit file ("JL", 0x1A, 0, then its big-endian length) whose files are stored
/// as zlib streams; the stream that inflates to a &lt;project&gt; document is the project. Nothing
/// is run; the search, the inflated size and the XML are bounded.
/// </summary>
public static class BitRockProjectReader
{
    public const int MaxRuntimeBytes = 64 * 1024 * 1024;
    public const int MaxProjectBytes = 16 * 1024 * 1024;
    private const int SearchWindowBytes = 96 * 1024 * 1024;
    private const int ChunkBytes = 4 * 1024 * 1024;
    private const string DefaultUninstallerName = "uninstall";
    private const int MaxCandidates = 4;

    private static readonly Regex Variable = new(@"\$\{([^}]+)\}", RegexOptions.Compiled);
    private static readonly Regex DoubleColonTag = new(@"<(/?)([A-Za-z_][\w.\-]*)::", RegexOptions.Compiled);
    private static readonly Regex DoubleColonAttribute = new(@"(\s)([A-Za-z_][\w.\-]*)::([\w.\-]+)=", RegexOptions.Compiled);

    /// <summary>The project, or null when the setup has no readable runtime archive or project.</summary>
    public static BitRockProject? Read(string path, CancellationToken ct)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.RandomAccess);
        string? xml = null;
        foreach (var runtime in Runtimes(stream, ct))
        {
            xml = FindProjectXml(runtime, ct);
            if (xml is not null) break;
        }
        if (xml is null) return null;
        XDocument doc;
        try
        {
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaxProjectBytes };
            // InstallBuilder writes per-action settings as <action::name>, which is not an XML name.
            xml = DoubleColonTag.Replace(xml, "<$1$2__");
            xml = DoubleColonAttribute.Replace(xml, "$1$2__$3=");
            using var reader = XmlReader.Create(new StringReader(xml), settings);
            doc = XDocument.Load(reader);
        }
        catch (XmlException)
        {
            return null;
        }
        return doc.Root is { Name.LocalName: "project" } root ? FromProject(root) : null;
    }

    /// <summary>Metakit files in the tail of the setup whose length field fits the file, last first, at most <see cref="MaxCandidates"/>.</summary>
    private static IEnumerable<byte[]> Runtimes(FileStream stream, CancellationToken ct)
    {
        var length = stream.Length;
        var windowStart = Math.Max(0, length - SearchWindowBytes);
        var buffer = new byte[ChunkBytes + 8];
        var found = 0;
        for (var end = length; end > windowStart; end -= ChunkBytes)
        {
            ct.ThrowIfCancellationRequested();
            var start = Math.Max(windowStart, end - ChunkBytes);
            var count = (int)Math.Min(buffer.Length, length - start);
            stream.Position = start;
            stream.ReadExactly(buffer, 0, count);
            for (var i = (int)(end - start) - 1; i >= 0; i--)
            {
                if (i + 8 > count || buffer[i] != (byte)'J' || buffer[i + 1] != (byte)'L' || buffer[i + 2] != 0x1A || buffer[i + 3] != 0) continue;
                var size = BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(i + 4, 4));
                var at = start + i;
                if (size < 16 || size > MaxRuntimeBytes || at + size > length) continue;
                var runtime = new byte[size];
                var resume = stream.Position;
                stream.Position = at;
                stream.ReadExactly(runtime);
                stream.Position = resume;
                yield return runtime;
                if (++found >= MaxCandidates) yield break;
            }
        }
    }

    private static string? FindProjectXml(byte[] runtime, CancellationToken ct)
    {
        if (runtime.AsSpan().IndexOf("project.xml\0"u8) < 0) return null;
        var probe = new byte[64];
        for (var i = 0; i < runtime.Length - 2; i++)
        {
            if ((i & 0xFFFF) == 0) ct.ThrowIfCancellationRequested();
            if (runtime[i] != 0x78 || ((runtime[i] << 8) | runtime[i + 1]) % 31 != 0) continue;
            try
            {
                using var z = new ZLibStream(new MemoryStream(runtime, i, runtime.Length - i, writable: false), CompressionMode.Decompress);
                var read = z.ReadAtLeast(probe, probe.Length, throwOnEndOfStream: false);
                var head = Encoding.UTF8.GetString(probe, 0, read).TrimStart('﻿', ' ', '\t', '\r', '\n');
                if (head.StartsWith("<?xml", StringComparison.Ordinal)) head = head[(head.IndexOf("?>", StringComparison.Ordinal) + 2)..].TrimStart();
                if (!head.StartsWith("<project", StringComparison.Ordinal)) continue;
                using var full = new ZLibStream(new MemoryStream(runtime, i, runtime.Length - i, writable: false), CompressionMode.Decompress);
                using var output = new MemoryStream();
                var chunk = new byte[81920];
                int n;
                while ((n = full.Read(chunk, 0, chunk.Length)) > 0)
                {
                    if (output.Length + n > MaxProjectBytes) return null;
                    output.Write(chunk, 0, n);
                }
                return Encoding.UTF8.GetString(output.GetBuffer(), 0, (int)output.Length);
            }
            catch (InvalidDataException) { }
        }
        return null;
    }

    private static string Value(XElement element, string name) =>
        (element.Attribute(name)?.Value ?? element.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value ?? "").Trim();

    private static string Top(XElement root, string name) =>
        root.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value.Trim() ?? "";

    /// <summary>True for an element without rules or whose rules only test that the platform is Windows.</summary>
    private static bool AppliesOnWindows(XElement element)
    {
        var rules = element.Elements().FirstOrDefault(e => e.Name.LocalName == "ruleList");
        if (rules is null) return true;
        var tests = rules.Elements().ToList();
        if (tests.Count == 0) return true;
        return tests.All(t => t.Name.LocalName == "compareText"
            && Value(t, "text") == "${platform_name}"
            && Value(t, "logic") is "equals" or "contains"
            && Value(t, "value").Contains("windows", StringComparison.Ordinal));
    }

    private static BitRockProject FromProject(XElement root)
    {
        var shortName = Top(root, "shortName");
        var fullName = Top(root, "fullName");
        var version = Top(root, "version");
        var bits = Top(root, "windows64bitMode");
        bool? is64 = bits switch { "1" => true, "0" => false, _ => null };

        // Literal setInstallerVariable actions that apply on Windows, first one per name.
        var variables = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var set in root.Descendants().Where(e => e.Name.LocalName == "setInstallerVariable"))
        {
            var name = Value(set, "name");
            if (name.Length == 0 || variables.ContainsKey(name) || !AppliesOnWindows(set)) continue;
            variables[name] = Value(set, "value");
        }

        var programFiles = is64 == true ? "%ProgramW6432%" : "%ProgramFiles(x86)%";
        string Resolve(string text, int depth = 0)
        {
            if (depth > 4) return text;
            return Variable.Replace(text, m => m.Groups[1].Value switch
            {
                "product_fullname" or "project.fullName" => fullName,
                "product_shortname" or "project.shortName" => shortName,
                "product_version" or "project.version" => version,
                "platform_name" => "windows",
                "platform_install_prefix" => programFiles,
                var other when variables.TryGetValue(other, out var v) && !v.Contains("${" + other + "}", StringComparison.Ordinal) => Resolve(v, depth + 1),
                _ => m.Value
            });
        }
        string Resolved(string text) { var r = Resolve(text); return r.Contains("${", StringComparison.Ordinal) ? "" : r; }

        var prefix = Top(root, "windowsARPRegistryPrefix");
        var arpKey = prefix.Length > 0 ? Resolved(prefix) : Resolved("${project.fullName} ${project.version}");

        // A registry action that writes DisplayVersion to the uninstall key replaces the project
        // version; one whose rules test more than the platform is conditional.
        var displayVersion = version;
        var fromAction = false;
        var conditional = false;
        foreach (var set in root.Descendants().Where(e => e.Name.LocalName == "registrySet"))
        {
            var key = Value(set, "key");
            if (!key.Contains(@"\CurrentVersion\Uninstall\", StringComparison.OrdinalIgnoreCase)) continue;
            var keyName = key[(key.LastIndexOf('\\') + 1)..];
            if (keyName != "${project.windowsARPRegistryPrefix}" && Resolved(keyName) != arpKey) continue;
            if (!string.Equals(Value(set, "name"), "DisplayVersion", StringComparison.OrdinalIgnoreCase)) continue;
            var written = Resolved(Value(set, "value"));
            if (written.Length == 0) continue;
            var always = AppliesOnWindows(set);
            if (fromAction && (!always || !conditional)) continue;
            displayVersion = written;
            fromAction = true;
            conditional = !always;
            if (always) break;
        }

        var uninstallerName = Top(root, "uninstallerName");
        var uninstallerSource = "project";
        if (uninstallerName.Length == 0 && variables.TryGetValue("uninstallerName", out var actionName) && Resolved(actionName).Length > 0)
        {
            uninstallerName = Resolved(actionName);
            uninstallerSource = "action";
        }
        else if (uninstallerName.Length == 0)
        {
            uninstallerName = DefaultUninstallerName;
            uninstallerSource = "default";
        }

        var installDir = "";
        var parameter = root.Descendants().FirstOrDefault(e => e.Name.LocalName == "directoryParameter" && Value(e, "name") == "installdir"
            && (Value(e, "default").Length > 0 || Value(e, "value").Length > 0));
        if (parameter is not null) installDir = Value(parameter, "default") is { Length: > 0 } d ? d : Value(parameter, "value");
        installDir = Resolved(installDir).Replace('/', '\\');
        if (!Regex.IsMatch(installDir, @"^(%[^%]+%|[A-Za-z]:)\\")) installDir = "";

        return new BitRockProject
        {
            ShortName = shortName,
            FullName = fullName,
            Version = version,
            Vendor = Top(root, "vendor"),
            DisplayName = Resolved(Top(root, "productDisplayName")),
            DisplayVersion = displayVersion,
            DisplayVersionFromRegistryAction = fromAction,
            DisplayVersionConditional = conditional,
            ArpKeyName = arpKey,
            ArpKeyNameInProject = prefix.Length > 0,
            Windows64BitMode = is64,
            UninstallerName = uninstallerName,
            UninstallerNameSource = uninstallerSource,
            InstallDir = installDir,
            RequireInstallationByRootUser = Top(root, "requireInstallationByRootUser"),
            UnattendedModeHandled = root.Descendants().Any(e => e.Name.LocalName == "compareText"
                && Value(e, "text") == "${installer_ui}" && Value(e, "value") == "unattended"),
            UnattendedModeUI = Top(root, "unattendedModeUI")
        };
    }
}
