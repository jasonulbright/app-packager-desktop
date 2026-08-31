using System.IO.Compression;
using System.Text;
using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests;

/// <summary>Builds synthetic .intunewin packages for the reader and publish tests.</summary>
internal static class IntuneWinFixture
{
    internal const string PayloadText = "encrypted-payload-bytes";

    internal static string DetectionXml(string name = "Contoso App", string fileName = "IntunePackage.intunewin",
        string setupFile = "install.bat", long unencryptedSize = 4242) =>
        $"""
        <?xml version="1.0" encoding="utf-8"?>
        <ApplicationInfo ToolVersion="1.8.4">
          <Name>{name}</Name>
          <UnencryptedContentSize>{unencryptedSize}</UnencryptedContentSize>
          <FileName>{fileName}</FileName>
          <SetupFile>{setupFile}</SetupFile>
          <EncryptionInfo>
            <EncryptionKey>ENC-KEY</EncryptionKey>
            <MacKey>MAC-KEY</MacKey>
            <InitializationVector>IV</InitializationVector>
            <Mac>MAC</Mac>
            <ProfileIdentifier>ProfileVersion1</ProfileIdentifier>
            <FileDigest>DIGEST</FileDigest>
            <FileDigestAlgorithm>SHA256</FileDigestAlgorithm>
          </EncryptionInfo>
        </ApplicationInfo>
        """;

    internal static string Create(string directory, bool includeMetadata = true, bool includePayload = true, string? detectionXml = null)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"package-{Guid.NewGuid():N}.intunewin");
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);

        if (includeMetadata)
        {
            var entry = archive.CreateEntry(IntuneWinPackageReader.MetadataEntry);
            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            writer.Write(detectionXml ?? DetectionXml());
        }
        if (includePayload)
        {
            var entry = archive.CreateEntry(IntuneWinPackageReader.PayloadEntry);
            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            writer.Write(PayloadText);
        }
        return path;
    }
}

public sealed class IntuneWinPackageReaderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "packr-intunewin-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void ReadMetadata_ReturnsAllFields()
    {
        var path = IntuneWinFixture.Create(_dir);
        var meta = new IntuneWinPackageReader().ReadMetadata(path);

        Assert.Equal("Contoso App", meta.Name);
        Assert.Equal("IntunePackage.intunewin", meta.FileName);
        Assert.Equal("install.bat", meta.SetupFile);
        Assert.Equal(4242, meta.UnencryptedContentSize);
        Assert.Equal("ENC-KEY", meta.EncryptionKey);
        Assert.Equal("MAC-KEY", meta.MacKey);
        Assert.Equal("IV", meta.InitializationVector);
        Assert.Equal("MAC", meta.Mac);
        Assert.Equal("ProfileVersion1", meta.ProfileIdentifier);
        Assert.Equal("DIGEST", meta.FileDigest);
        Assert.Equal("SHA256", meta.FileDigestAlgorithm);
    }

    [Fact]
    public void ExtractPayload_WritesInnerEntry()
    {
        var path = IntuneWinFixture.Create(_dir);
        var destination = Path.Combine(_dir, "payload.bin");

        var size = new IntuneWinPackageReader().ExtractPayload(path, destination);

        Assert.Equal(IntuneWinFixture.PayloadText, File.ReadAllText(destination));
        Assert.Equal(new FileInfo(destination).Length, size);
    }

    [Fact]
    public void ReadMetadata_MissingFile_Throws()
    {
        var reader = new IntuneWinPackageReader();
        Assert.Throws<FileNotFoundException>(() => reader.ReadMetadata(Path.Combine(_dir, "nope.intunewin")));
    }

    [Fact]
    public void ReadMetadata_NonZip_Throws()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "plain.intunewin");
        File.WriteAllText(path, "this is not a zip archive");

        var ex = Assert.Throws<InvalidDataException>(() => new IntuneWinPackageReader().ReadMetadata(path));
        Assert.Contains("not a valid .intunewin package", ex.Message);
    }

    [Fact]
    public void ReadMetadata_MissingDetectionXml_Throws()
    {
        var path = IntuneWinFixture.Create(_dir, includeMetadata: false);
        var ex = Assert.Throws<InvalidDataException>(() => new IntuneWinPackageReader().ReadMetadata(path));
        Assert.Contains("Detection.xml not found", ex.Message);
    }

    [Fact]
    public void ReadMetadata_NoEncryptionInfo_Throws()
    {
        var xml = "<?xml version=\"1.0\" encoding=\"utf-8\"?><ApplicationInfo><Name>X</Name></ApplicationInfo>";
        var path = IntuneWinFixture.Create(_dir, detectionXml: xml);

        var ex = Assert.Throws<InvalidDataException>(() => new IntuneWinPackageReader().ReadMetadata(path));
        Assert.Contains("no EncryptionInfo", ex.Message);
    }

    [Fact]
    public void ExtractPayload_MissingPayloadEntry_Throws()
    {
        var path = IntuneWinFixture.Create(_dir, includePayload: false);
        var ex = Assert.Throws<InvalidDataException>(
            () => new IntuneWinPackageReader().ExtractPayload(path, Path.Combine(_dir, "out.bin")));
        Assert.Contains("Encrypted payload not found", ex.Message);
    }
}
