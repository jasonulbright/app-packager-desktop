using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using ApplicationPackagerEngine.Services.Logging;

namespace ApplicationPackagerEngine.Services;

/// <summary>
/// The query and script library: the items embedded in the Engine assembly, each checked against its
/// SHA-256 (of the text with line feeds only), and the *.cmpivot, *.kql and *.ps1 files of an optional
/// user folder. An embedded item whose hash does not match is left out. A user file larger than
/// <see cref="MaxFileBytes"/>, a link, or a file past <see cref="MaxUserFiles"/> is left out.
/// </summary>
public sealed class PivotsLibrary(IAppLog? log = null) : IPivotsLibrary
{
    public const string LogArea = "PivotsScripts";
    public const string Query = "Query";
    public const string Script = "Script";
    public const int MaxFileBytes = 256 * 1024;
    public const int MaxUserFiles = 500;

    private const string ResourcePrefix = "ApplicationPackagerEngine.Services.PivotsScripts.Library.";

    /// <summary>The embedded items by resource name with the SHA-256 of their text.</summary>
    internal static readonly IReadOnlyDictionary<string, string> Expected = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["Queries.BIOS and serial.cmpivot"] = "833ecbf12f5d9bf922ef04124210440a7f10d2cf6780ec3c4c355ccb582e4b46",
        ["Queries.Free disk.cmpivot"] = "e0af7b91ed677208c478aec39f2dee0605b388a6ea5d080421c4a7b82391d100",
        ["Queries.Installed software.cmpivot"] = "eaf45309143b9992e30d86637d39e2697c4041a0c1d797172d54a16b8402f111",
        ["Queries.Logged on users.cmpivot"] = "dbbfe69836953d21b884fcbf1464f0482a42ecadc53d153b7feb5a499197163a",
        ["Queries.Operating system.cmpivot"] = "26bb76e27162ace53689ab7c56a37b7757807bafe60edb9071faebca606bb8c3",
        ["Queries.Processes.cmpivot"] = "d0a3b683141858057e33e9a39e7d98e022c827e4de97bf7054c68b841949710a",
        ["Queries.Services.cmpivot"] = "e3f517e3c0a3718f7df2dac8a9b67247c7a2000ac9130e1d4478e3fdf0c3bef8",
        ["Scripts.Client version.ps1"] = "82d8f12b93df9f42b3605cf82da94f9287ccf55d9f823fce4af4108f9800e457",
        ["Scripts.Pending reboot.ps1"] = "266f49488f447a9e1361a2e270e69f3bab53231ad421ba2a74d5bc6aadbca571",
        ["Scripts.Service state.ps1"] = "f971193adcea918fe2fb9b11e1d5651cf80fd4ce038aff1d856073ea81c310af",
        ["Scripts.Uptime.ps1"] = "c16ec9615649a498a5989c2dc71966490130ab7f9269964c89307eb8e36cb552"
    };

    /// <summary>The SHA-256 of a text in lower-case hex, after carriage returns before line feeds are removed.</summary>
    public static string HashOf(string text) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes((text ?? "").Replace("\r\n", "\n", StringComparison.Ordinal))));

    public IReadOnlyList<PivotsLibraryItem> Load(string? userFolder)
    {
        var items = new List<PivotsLibraryItem>();
        var assembly = typeof(PivotsLibrary).Assembly;
        foreach (var (name, hash) in Expected)
        {
            var text = ReadResource(assembly, ResourcePrefix + name);
            if (text is null || !string.Equals(HashOf(text), hash, StringComparison.Ordinal))
            {
                log?.Warning(LogArea, "A built-in library item failed its hash check and was left out.");
                continue;
            }
            var file = name[(name.IndexOf('.') + 1)..];
            items.Add(new PivotsLibraryItem(Path.GetFileNameWithoutExtension(file), name.StartsWith("Queries.", StringComparison.Ordinal) ? Query : Script, text.Replace("\r\n", "\n", StringComparison.Ordinal), true, hash));
        }
        if (!string.IsNullOrWhiteSpace(userFolder)) items.AddRange(LoadFolder(userFolder.Trim()));
        return items;
    }

    private IEnumerable<PivotsLibraryItem> LoadFolder(string folder)
    {
        var found = new List<PivotsLibraryItem>();
        try
        {
            var dir = new DirectoryInfo(folder);
            if (!dir.Exists || dir.Attributes.HasFlag(FileAttributes.ReparsePoint)) return found;
            var files = dir.EnumerateFiles().Where(f => f.Extension.ToLowerInvariant() is ".cmpivot" or ".kql" or ".ps1").OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).Take(MaxUserFiles);
            foreach (var f in files)
            {
                if (f.Attributes.HasFlag(FileAttributes.ReparsePoint) || f.Length > MaxFileBytes) continue;
                var text = File.ReadAllText(f.FullName);
                found.Add(new PivotsLibraryItem(Path.GetFileNameWithoutExtension(f.Name), f.Extension.Equals(".ps1", StringComparison.OrdinalIgnoreCase) ? Script : Query, text, false, HashOf(text)));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            log?.Warning(LogArea, $"The library folder could not be read ({ex.GetType().Name}).");
        }
        return found;
    }

    private static string? ReadResource(Assembly assembly, string name)
    {
        using var stream = assembly.GetManifestResourceStream(name);
        if (stream is null) return null;
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
