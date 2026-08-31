using System.IO.Compression;
using System.Xml.Linq;

namespace ApplicationPackagerEngine.Services;

/// <summary>
/// Metadata and encryption info read from a .intunewin package.
/// </summary>
public sealed record IntuneWinMetadata(
    string Name,
    string FileName,
    string SetupFile,
    long UnencryptedContentSize,
    string EncryptionKey,
    string MacKey,
    string InitializationVector,
    string Mac,
    string ProfileIdentifier,
    string FileDigest,
    string FileDigestAlgorithm);

/// <summary>
/// Reads a .intunewin package. The file is a zip whose
/// IntuneWinPackage/Metadata/Detection.xml carries the fields the Graph commit action needs
/// plus the setup file name and unencrypted size; the bytes uploaded to Azure Storage are the
/// inner IntuneWinPackage/Contents/IntunePackage.intunewin, not the outer zip.
/// </summary>
public sealed class IntuneWinPackageReader
{
    public const string MetadataEntry = "IntuneWinPackage/Metadata/Detection.xml";
    public const string PayloadEntry = "IntuneWinPackage/Contents/IntunePackage.intunewin";

    public IntuneWinMetadata ReadMetadata(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"IntuneWin file not found: {path}", path);

        using var zip = OpenPackage(path);
        var entry = zip.GetEntry(MetadataEntry)
            ?? throw new InvalidDataException($"Detection.xml not found inside {path}; not a valid .intunewin package.");

        XDocument doc;
        using (var stream = entry.Open())
            doc = XDocument.Load(stream);

        var info = doc.Root
            ?? throw new InvalidDataException($"Detection.xml inside {path} is empty.");
        var ns = info.Name.Namespace;
        var enc = info.Element(ns + "EncryptionInfo")
            ?? throw new InvalidDataException("Detection.xml carries no EncryptionInfo; the package cannot be committed.");

        string Val(XElement parent, string name) => parent.Element(parent.Name.Namespace + name)?.Value ?? "";

        var sizeText = Val(info, "UnencryptedContentSize");
        _ = long.TryParse(sizeText, out var size);

        return new IntuneWinMetadata(
            Name: Val(info, "Name"),
            FileName: Val(info, "FileName"),
            SetupFile: Val(info, "SetupFile"),
            UnencryptedContentSize: size,
            EncryptionKey: Val(enc, "EncryptionKey"),
            MacKey: Val(enc, "MacKey"),
            InitializationVector: Val(enc, "InitializationVector"),
            Mac: Val(enc, "Mac"),
            ProfileIdentifier: Val(enc, "ProfileIdentifier"),
            FileDigest: Val(enc, "FileDigest"),
            FileDigestAlgorithm: Val(enc, "FileDigestAlgorithm"));
    }

    /// <summary>Extracts the encrypted payload to <paramref name="destination"/> and returns its size.</summary>
    public long ExtractPayload(string path, string destination)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"IntuneWin file not found: {path}", path);

        using var zip = OpenPackage(path);
        var entry = zip.GetEntry(PayloadEntry)
            ?? throw new InvalidDataException($"Encrypted payload not found inside {path}; not a valid .intunewin package.");

        entry.ExtractToFile(destination, overwrite: true);
        return new FileInfo(destination).Length;
    }

    private static ZipArchive OpenPackage(string path)
    {
        try
        {
            return ZipFile.OpenRead(path);
        }
        catch (InvalidDataException ex)
        {
            throw new InvalidDataException($"{path} is not a valid .intunewin package (not a zip archive).", ex);
        }
    }
}
