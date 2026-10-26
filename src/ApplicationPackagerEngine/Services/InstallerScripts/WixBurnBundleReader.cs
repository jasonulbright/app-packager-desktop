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
    public static string? ReadBundleId(string path) => ReadSection(path)?.BundleId;

    /// <summary>The largest attached UX container that is copied out to read the manifest.</summary>
    public const long MaxUxContainerBytes = 32L * 1024 * 1024;

    /// <summary>The largest bundle manifest that is read.</summary>
    public const long MaxManifestBytes = 8L * 1024 * 1024;

    /// <summary>
    /// The BURN_SECTION header: the bundle id, then the stub size (the offset of the first
    /// attached container, the UX container) and the container sizes; null when absent.
    /// </summary>
    public static BurnSection? ReadSection(string path)
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
            var bundleId = new Guid(reader.ReadBytes(16)).ToString("B").ToUpperInvariant();
            long uxOffset = 0, uxSize = 0;
            if (burnOffset <= stream.Length - 56)
            {
                uxOffset = reader.ReadUInt32();
                stream.Seek(16, SeekOrigin.Current);
                var containers = reader.ReadUInt32();
                if (containers >= 1) uxSize = reader.ReadUInt32();
            }
            return new BurnSection(bundleId, uxOffset, uxSize);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// Reads the bundle manifest (file "0" of the attached UX cabinet) through the Windows setup
    /// API. The cabinet is copied to <paramref name="workFolder"/> first; nothing in it is run.
    /// Returns null with the reason when the manifest is not available.
    /// </summary>
    public static BurnManifest? ReadManifest(string path, string workFolder, out string reason)
    {
        reason = "";
        var section = ReadSection(path);
        if (section is null) { reason = "The bundle has no Burn section."; return null; }
        long length;
        try { length = new FileInfo(path).Length; }
        catch (IOException) { reason = "The bundle could not be read."; return null; }
        if (section.UxOffset <= 0 || section.UxSize <= 0 || section.UxOffset > length || section.UxSize > length - section.UxOffset)
        {
            reason = "The bundle's UX container is outside the file.";
            return null;
        }
        if (section.UxSize > MaxUxContainerBytes) { reason = "The bundle's UX container is larger than the read limit."; return null; }

        var cab = Path.Combine(workFolder, "ux-" + Guid.NewGuid().ToString("N")[..8] + ".cab");
        var manifestPath = Path.Combine(workFolder, "manifest-" + Guid.NewGuid().ToString("N")[..8] + ".xml");
        try
        {
            using (var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
            using (var target = new FileStream(cab, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                source.Position = section.UxOffset;
                var buffer = new byte[81920];
                var left = section.UxSize;
                while (left > 0)
                {
                    var read = source.Read(buffer, 0, (int)Math.Min(buffer.Length, left));
                    if (read <= 0) break;
                    target.Write(buffer, 0, read);
                    left -= read;
                }
            }
            if (!CabinetFile.ExtractOne(cab, "0", manifestPath, MaxManifestBytes) || !File.Exists(manifestPath))
            {
                reason = "The bundle manifest was not found in the UX container.";
                return null;
            }
            var settings = new System.Xml.XmlReaderSettings { DtdProcessing = System.Xml.DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaxManifestBytes };
            using var reader = System.Xml.XmlReader.Create(manifestPath, settings);
            var doc = System.Xml.Linq.XDocument.Load(reader);
            if (doc.Root?.Name.LocalName != "BurnManifest") { reason = "The UX container holds no bundle manifest."; return null; }
            var registration = doc.Root.Elements().FirstOrDefault(e => e.Name.LocalName == "Registration");
            var perMachine = (string?)registration?.Attribute("PerMachine");
            return new BurnManifest(
                perMachine is null ? null : perMachine.Equals("yes", StringComparison.OrdinalIgnoreCase),
                (string?)registration?.Attribute("Id") ?? "",
                (string?)registration?.Attribute("Version") ?? "",
                (string?)registration?.Attribute("ExecutableName") ?? "");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException or InvalidOperationException)
        {
            reason = "The bundle manifest could not be read.";
            return null;
        }
        finally
        {
            foreach (var file in new[] { cab, manifestPath })
                try { File.Delete(file); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}

/// <summary>The BURN_SECTION facts: the bundle id and the position and size of the UX container.</summary>
public sealed record BurnSection(string BundleId, long UxOffset, long UxSize);

/// <summary>The Registration facts of a bundle manifest. PerMachine is null when the manifest does not say.</summary>
public sealed record BurnManifest(bool? PerMachine, string RegistrationId, string Version, string ExecutableName);
