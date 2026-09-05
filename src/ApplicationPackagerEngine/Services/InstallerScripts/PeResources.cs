using System.Text;
using System.Text.RegularExpressions;

namespace ApplicationPackagerEngine.Services.InstallerScripts;

/// <summary>
/// Reads resources out of a PE image without loading it: the loader table an
/// Inno Setup stub carries as RCDATA 11111, and the embedded manifest whose
/// requestedExecutionLevel says whether the stub elevates.
/// </summary>
public static class PeResources
{
    public const int RtRcData = 10;
    public const int RtManifest = 24;

    /// <summary>
    /// Returns the bytes of one resource (first language entry), or null when
    /// the file is not a PE image, has no resource directory, or the entry is
    /// absent or larger than maxSize. A name of -1 takes the first name entry
    /// under the type.
    /// </summary>
    public static byte[]? Read(string path, int type, int name = -1, long maxSize = 1024 * 1024)
    {
        try
        {
            using var fs = File.OpenRead(path);
            using var br = new BinaryReader(fs);
            if (fs.Length < 0x40) return null;
            fs.Position = 0;
            if (br.ReadUInt16() != 0x5A4D) return null;
            fs.Position = 0x3C;
            var peOffset = br.ReadUInt32();
            if (peOffset + 24 > fs.Length) return null;
            fs.Position = peOffset;
            if (br.ReadUInt32() != 0x00004550) return null;
            fs.Position = peOffset + 6;
            var numSections = br.ReadUInt16();
            fs.Position = peOffset + 20;
            var optSize = br.ReadUInt16();
            var optStart = peOffset + 24;
            fs.Position = optStart;
            var magic = br.ReadUInt16();
            var dirOffset = magic == 0x20B ? 112 : 96;
            fs.Position = optStart + dirOffset + (2 * 8);
            var resRva = br.ReadUInt32();
            var resSize = br.ReadUInt32();
            if (resRva == 0 || resSize == 0) return null;

            var sections = new List<(uint Va, uint Vs, uint Raw)>();
            var secTable = optStart + optSize;
            for (var i = 0; i < numSections; i++)
            {
                fs.Position = secTable + (i * 40) + 8;
                var virtualSize = br.ReadUInt32();
                var virtualAddr = br.ReadUInt32();
                var rawSize = br.ReadUInt32();
                var rawPtr = br.ReadUInt32();
                sections.Add((virtualAddr, Math.Max(virtualSize, rawSize), rawPtr));
            }

            long ToFile(uint rva)
            {
                foreach (var s in sections)
                {
                    if (rva >= s.Va && rva < s.Va + s.Vs) return s.Raw + (rva - s.Va);
                }
                return -1;
            }

            var resBase = ToFile(resRva);
            if (resBase < 0) return null;

            List<(uint Id, uint Offset)> ReadDir(long dirFileOffset)
            {
                fs.Position = dirFileOffset + 12;
                var named = br.ReadUInt16();
                var ids = br.ReadUInt16();
                var entries = new List<(uint, uint)>();
                for (var i = 0; i < named + ids; i++)
                {
                    fs.Position = dirFileOffset + 16 + (i * 8);
                    var id = br.ReadUInt32();
                    var off = br.ReadUInt32();
                    entries.Add((id, off));
                }
                return entries;
            }

            var typeEntry = ReadDir(resBase).FirstOrDefault(e => (e.Id & 0x80000000) == 0 && e.Id == (uint)type);
            if (typeEntry == default) return null;
            var nameDir = resBase + (typeEntry.Offset & 0x7FFFFFFF);
            var nameEntries = ReadDir(nameDir);
            var nameEntry = name < 0
                ? nameEntries.FirstOrDefault()
                : nameEntries.FirstOrDefault(e => (e.Id & 0x80000000) == 0 && e.Id == (uint)name);
            if (nameEntry == default) return null;
            var langDir = resBase + (nameEntry.Offset & 0x7FFFFFFF);
            var langEntry = ReadDir(langDir).FirstOrDefault();
            if (langEntry == default) return null;
            if ((langEntry.Offset & 0x80000000) != 0) return null;
            var dataEntry = resBase + langEntry.Offset;
            fs.Position = dataEntry;
            var dataRva = br.ReadUInt32();
            var dataSize = br.ReadUInt32();
            var dataFile = ToFile(dataRva);
            if (dataFile < 0 || dataSize <= 0 || dataSize > maxSize) return null;
            if (dataFile + dataSize > fs.Length) return null;
            fs.Position = dataFile;
            return br.ReadBytes((int)dataSize);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// requestedExecutionLevel from the embedded manifest: asInvoker,
    /// highestAvailable or requireAdministrator; "" when the file carries no
    /// manifest or no execution level.
    /// </summary>
    public static string ReadRequestedExecutionLevel(string path)
    {
        var bytes = Read(path, RtManifest);
        if (bytes is null) return "";
        var text = Encoding.UTF8.GetString(bytes);
        var m = Regex.Match(text, "requestedExecutionLevel[^>]*\\blevel\\s*=\\s*[\"']([A-Za-z]+)[\"']");
        return m.Success ? m.Groups[1].Value : "";
    }
}
