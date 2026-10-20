using System.IO.Compression;
using System.IO.MemoryMappedFiles;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace ApplicationPackagerEngine.Services;

/// <summary>
/// The nuspec a Velopack setup stub carries inside its embedded package, plus
/// the per-user locations that follow from the package id.
/// </summary>
public sealed class VelopackMetadata
{
    /// <summary>True when the bundle header and the nuspec were both read.</summary>
    public bool HeaderAvailable { get; init; }
    public long BundleOffset { get; init; }
    public long BundleLength { get; init; }

    public string PackageId { get; init; } = "";
    public string DisplayName { get; init; } = "";
    /// <summary>Add/Remove Programs version: major.minor.patch, no prerelease or build label.</summary>
    public string DisplayVersion { get; init; } = "";
    /// <summary>The nuspec version in full, prerelease and build labels included.</summary>
    public string PackageVersion { get; init; } = "";
    public string Publisher { get; init; } = "";
    public string Architecture { get; init; } = "";
    public string MainExe { get; init; } = "";
    public string Channel { get; init; } = "";

    public string InstallDir { get; init; } = "";
    public string UninstallerPath { get; init; } = "";
    public string UninstallRegistryKey { get; init; } = "";
    public string SilentUninstallCommand { get; init; } = "";
    public string Note { get; init; } = "";
}

/// <summary>
/// Reads the nuspec out of a Velopack Setup.exe without running it, extracting
/// it, or copying the payload onto the heap.
/// </summary>
public static class VelopackMetadataReader
{
    /// <summary>Largest nuspec read from the package; a larger one is refused, not parsed.</summary>
    public const int MetadataLimitBytes = 1024 * 1024;

    private const string ArpHkcu = @"HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    private const string Note =
        "Default per-user location; --installto (-t) changes the install folder and the Update.exe path. "
        + "The Add/Remove Programs version omits prerelease and build labels.";

    // A NuGet id is a single path segment. An invalid manifest id must not be
    // turned into a command line or a registry key.
    private static readonly Regex IdPattern = new(@"^[A-Za-z0-9_][A-Za-z0-9_.-]*$", RegexOptions.Compiled);
    private static readonly Regex VersionPattern = new(@"^(\d+\.\d+\.\d+)(?:[-+][0-9A-Za-z.+-]+)?$", RegexOptions.Compiled);
    private static readonly Regex RootNuspecPattern = new(@"^[^/\\]+\.nuspec$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Reads one Velopack Setup.exe. A file that is not one, or whose header or
    /// manifest does not hold up, comes back with HeaderAvailable false and the
    /// reason in Note; no identity, command or key is invented in that case.
    /// </summary>
    public static VelopackMetadata Read(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var head = new byte[(int)Math.Min(InstallerSignatureScanner.ScanWindowBytes, stream.Length)];
            stream.ReadExactly(head, 0, head.Length);

            var header = InstallerSignatureScanner.TryReadVelopackBundleHeader(head)
                ?? throw new InvalidDataException("Velopack setup bundle signature not found.");
            if (header.Offset <= 0 || header.Length <= 0
                || header.Offset > stream.Length || header.Length > stream.Length - header.Offset)
                throw new InvalidDataException("Velopack bundle offset or length is outside the file, or the package is missing.");

            // The bounded view excludes the setup stub and any trailing
            // signature data, which the ZIP end-directory search would otherwise
            // have to walk back through.
            using var mapping = MemoryMappedFile.CreateFromFile(
                stream, null, 0, MemoryMappedFileAccess.Read, HandleInheritability.None, leaveOpen: true);
            using var view = mapping.CreateViewStream(header.Offset, header.Length, MemoryMappedFileAccess.Read);
            ZipCentralDirectory.Check(view, InstallerSignatureScanner.MaxLayoutEntries, "read");
            using var archive = new ZipArchive(view, ZipArchiveMode.Read, leaveOpen: true);

            var entries = archive.Entries.Where(e => RootNuspecPattern.IsMatch(e.FullName)).ToList();
            if (entries.Count != 1)
                throw new InvalidDataException("Expected one root nuspec in the Velopack package.");
            if (entries[0].Length > MetadataLimitBytes)
                throw new InvalidDataException("Velopack nuspec exceeds the metadata size limit.");

            var nuspec = ReadNuspec(entries[0]);

            var id = Value(nuspec, "id");
            var version = Value(nuspec, "version");
            var versionMatch = VersionPattern.Match(version);
            if (!IdPattern.IsMatch(id) || !versionMatch.Success)
                throw new InvalidDataException("Velopack nuspec has an invalid package id or version.");

            var installDir = @"%LOCALAPPDATA%\" + id;
            var uninstaller = installDir + @"\Update.exe";
            var title = Value(nuspec, "title");

            return new VelopackMetadata
            {
                HeaderAvailable = true,
                BundleOffset = header.Offset,
                BundleLength = header.Length,
                PackageId = id,
                DisplayName = title.Length > 0 ? title : id,
                DisplayVersion = versionMatch.Groups[1].Value,
                PackageVersion = version,
                Publisher = Value(nuspec, "authors"),
                Architecture = Value(nuspec, "machineArchitecture"),
                MainExe = Value(nuspec, "mainExe"),
                Channel = Value(nuspec, "channel"),
                InstallDir = installDir,
                UninstallerPath = uninstaller,
                UninstallRegistryKey = $@"{ArpHkcu}\{id}",
                SilentUninstallCommand = $"\"{uninstaller}\" --uninstall --silent",
                Note = Note
            };
        }
        catch (Exception ex)
        {
            return new VelopackMetadata { Note = $"Velopack metadata unavailable: {ex.Message}" };
        }
    }

    private static Dictionary<string, string> ReadNuspec(ZipArchiveEntry entry)
    {
        using var entryStream = entry.Open();
        using var reader = new StreamReader(entryStream);
        // The compressed entry is read under its own bound as well: the ZIP
        // directory's declared length is not evidence of the real size.
        var buffer = new char[MetadataLimitBytes + 1];
        var count = reader.ReadBlock(buffer, 0, buffer.Length);
        if (count > MetadataLimitBytes)
            throw new InvalidDataException("Velopack nuspec exceeds the metadata size limit.");

        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MetadataLimitBytes
        };
        using var text = new StringReader(new string(buffer, 0, count));
        using var xml = XmlReader.Create(text, settings);
        var document = XDocument.Load(xml);

        var metadata = document.Root?.Elements().FirstOrDefault(e => e.Name.LocalName == "metadata")
            ?? throw new InvalidDataException("Velopack nuspec metadata is missing.");

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var child in metadata.Elements())
            values[child.Name.LocalName] = child.Value.Trim();
        return values;
    }

    private static string Value(Dictionary<string, string> nuspec, string name) =>
        nuspec.TryGetValue(name, out var value) ? value : "";
}
