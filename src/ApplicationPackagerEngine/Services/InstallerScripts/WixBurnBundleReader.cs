namespace ApplicationPackagerEngine.Services.InstallerScripts;

/// <summary>
/// Reads the bundle id from the BURN_SECTION header at the start of a WiX Burn
/// bundle's .wixburn PE section: magic 0x00F14300, a version DWORD, then the bundle
/// GUID. Burn registers its Add/Remove Programs entry under that GUID.
/// </summary>
public static class WixBurnBundleReader
{
    public const uint SectionMagic = 0x00F14300;

    /// <summary>
    /// The bundle id in registry form ({XXXXXXXX-...}, upper case), or null when the
    /// file is not a PE image, carries no .wixburn section, or the header is not Burn's.
    /// </summary>
    public static string? ReadBundleId(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var reader = new BinaryReader(stream);
            if (stream.Length < 0x40) return null;
            stream.Seek(0x3C, SeekOrigin.Begin);
            var peOffset = reader.ReadUInt32();
            if (peOffset == 0 || peOffset > stream.Length - 24) return null;
            stream.Seek(peOffset, SeekOrigin.Begin);
            if (reader.ReadUInt32() != 0x00004550) return null;

            reader.ReadUInt16();
            var sectionCount = reader.ReadUInt16();
            reader.ReadUInt32();
            reader.ReadUInt32();
            reader.ReadUInt32();
            var optionalHeaderSize = reader.ReadUInt16();
            reader.ReadUInt16();
            // A corrupt header can claim up to 65535 sections.
            if (sectionCount == 0 || sectionCount > 128) return null;
            stream.Seek(optionalHeaderSize, SeekOrigin.Current);

            uint burnOffset = 0;
            for (var i = 0; i < sectionCount; i++)
            {
                var name = System.Text.Encoding.ASCII.GetString(reader.ReadBytes(8)).TrimEnd('\0');
                reader.ReadUInt32();
                reader.ReadUInt32();
                reader.ReadUInt32();
                var rawPointer = reader.ReadUInt32();
                stream.Seek(16, SeekOrigin.Current);
                if (name == ".wixburn") { burnOffset = rawPointer; break; }
            }
            if (burnOffset == 0 || burnOffset > stream.Length - 24) return null;

            stream.Seek(burnOffset, SeekOrigin.Begin);
            if (reader.ReadUInt32() != SectionMagic) return null;
            reader.ReadUInt32();
            return new Guid(reader.ReadBytes(16)).ToString("B").ToUpperInvariant();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }
}
