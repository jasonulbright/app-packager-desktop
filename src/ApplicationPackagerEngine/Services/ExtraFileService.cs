using System.IO.Compression;
using System.Text.RegularExpressions;
using ApplicationPackagerEngine.Models;

namespace ApplicationPackagerEngine.Services;

/// <summary>What the install wrapper of an MSIX needs to know about the extra files.</summary>
public sealed record MsixExtras(IReadOnlyList<string> DependencyFolders, string? LicenseFileName)
{
    /// <summary>The content folder that holds one sub folder per processor architecture.</summary>
    public const string DependenciesFolder = "Dependencies";

    public bool IsEmpty => DependencyFolders.Count == 0 && string.IsNullOrEmpty(LicenseFileName);
}

/// <summary>
/// Extra files of an MSIX definition: a dependency package set and a license file that Add-AppxProvisionedPackage
/// takes beside the package. The service resolves each address and SHA-256, downloads, verifies, and extracts a
/// dependency ZIP into Dependencies\&lt;architecture&gt;. A file without a verified SHA-256 is used only when the
/// definition does not require one, and the log says so.
/// </summary>
public static class ExtraFileService
{
    public const string License = "License";
    public const string DependencyArchive = "DependencyArchive";

    private static readonly string[] PackageExtensions = [".appx", ".msix"];

    /// <summary>Refuses a definition whose extra files cannot be staged, before any download.</summary>
    public static void Validate(AppDefinition def)
    {
        var files = def.ExtraFiles ?? [];
        if (files.Length == 0) return;
        if (!def.InstallerType.Equals("MSIX", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"{def.Application} has extra files, which only an MSIX definition takes.");

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var kinds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            var kind = (file.Kind ?? "").Trim();
            if (!kind.Equals(License, StringComparison.OrdinalIgnoreCase) && !kind.Equals(DependencyArchive, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"{def.Application}: the extra file kind '{file.Kind}' is not {License} or {DependencyArchive}.");
            if (!kinds.Add(kind))
                throw new InvalidOperationException($"{def.Application}: more than one extra file of kind {kind}.");
            if (string.IsNullOrWhiteSpace(file.FileName) || file.FileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
                || file.FileName.Any(c => c > 0x7F))
                throw new InvalidOperationException($"{def.Application}: the extra file name '{file.FileName}' is not a plain ASCII file name.");
            if (!names.Add(file.FileName))
                throw new InvalidOperationException($"{def.Application}: two extra files are named {file.FileName}.");
            if (string.IsNullOrWhiteSpace(file.DownloadUrl) && string.IsNullOrWhiteSpace(file.DownloadUrlQuery))
                throw new InvalidOperationException($"{def.Application}: the extra file {file.FileName} has no download address.");
            if (kind.Equals(DependencyArchive, StringComparison.OrdinalIgnoreCase))
            {
                if (file.ArchiveFolders is not { Length: > 0 })
                    throw new InvalidOperationException($"{def.Application}: the dependency archive {file.FileName} names no architecture folder.");
                if (file.ArchiveFolders.Any(f => !Regex.IsMatch(f ?? "", "^[A-Za-z0-9]+$")))
                    throw new InvalidOperationException($"{def.Application}: an architecture folder of {file.FileName} is not letters and digits.");
            }
        }
    }

    /// <summary>The folders and the license name the wrappers use; no download happens.</summary>
    public static MsixExtras Describe(AppDefinition def)
    {
        var files = def.ExtraFiles ?? [];
        var folders = files.Where(f => f.Kind.Equals(DependencyArchive, StringComparison.OrdinalIgnoreCase))
            .SelectMany(f => f.ArchiveFolders ?? []).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var license = files.FirstOrDefault(f => f.Kind.Equals(License, StringComparison.OrdinalIgnoreCase))?.FileName;
        return new MsixExtras(folders, license);
    }

    /// <summary>
    /// The download address: the first match of <see cref="ExtraFileConfig.DownloadUrlQuery"/> in the feed (named group "url", else group 1),
    /// or the DownloadUrl template. Null when a query matches nothing.
    /// </summary>
    public static string? ResolveUrl(ExtraFileConfig file, string? feed, string rawVersion)
    {
        if (!string.IsNullOrWhiteSpace(file.DownloadUrlQuery))
        {
            if (feed is null) return null;
            var match = Regex.Match(feed, file.DownloadUrlQuery.Replace("{version}", Regex.Escape(rawVersion), StringComparison.OrdinalIgnoreCase));
            if (!match.Success) return null;
            var named = match.Groups["url"];
            return named.Success ? named.Value : match.Groups.Count > 1 ? match.Groups[1].Value : match.Value;
        }
        return file.DownloadUrl.Replace("{version}", rawVersion, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The SHA-256 for the file: the pin, else the capture of <see cref="ExtraFileConfig.HashQuery"/> in the feed. Null when none.</summary>
    public static string? ResolveHash(ExtraFileConfig file, string? feed, string rawVersion)
    {
        if (!string.IsNullOrWhiteSpace(file.Sha256)) return file.Sha256.Trim();
        if (string.IsNullOrWhiteSpace(file.HashQuery) || feed is null) return null;
        var match = Regex.Match(feed, file.HashQuery.Replace("{version}", Regex.Escape(rawVersion), StringComparison.OrdinalIgnoreCase));
        if (!match.Success) return null;
        var named = match.Groups["hash"];
        return named.Success ? named.Value : match.Groups.Count > 1 ? match.Groups[1].Value : match.Value;
    }

    /// <summary>
    /// Extracts the dependency packages of each folder to destination\&lt;folder&gt;. Only .appx and .msix entries of the named
    /// folders are taken. A folder without a package, and an entry that would land outside the destination, refuse the stage.
    /// Returns the relative paths (folder\file) that were written.
    /// </summary>
    public static IReadOnlyList<string> ExtractDependencies(string zipPath, IEnumerable<string> folders, string destination)
    {
        var written = new List<string>();
        var root = Path.GetFullPath(destination).TrimEnd('\\') + "\\";
        using var zip = ZipFile.OpenRead(zipPath);
        foreach (var folder in folders)
        {
            var count = 0;
            foreach (var entry in zip.Entries)
            {
                var name = entry.FullName.Replace('\\', '/');
                var slash = name.IndexOf('/');
                if (slash <= 0 || !name[..slash].Equals(folder, StringComparison.OrdinalIgnoreCase)) continue;
                var rest = name[(slash + 1)..];
                if (rest.Length == 0 || rest.Contains('/')) continue;
                if (!PackageExtensions.Contains(Path.GetExtension(rest), StringComparer.OrdinalIgnoreCase)) continue;
                var target = Path.GetFullPath(Path.Combine(destination, folder, rest));
                if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"A dependency entry extracts outside the stage folder: {entry.FullName}");
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                entry.ExtractToFile(target, overwrite: true);
                written.Add($"{folder}\\{rest}");
                count++;
            }
            if (count == 0)
                throw new InvalidOperationException($"The dependency archive {Path.GetFileName(zipPath)} holds no package in the folder {folder}.");
        }
        return written;
    }

    /// <summary>
    /// Downloads, verifies and places every extra file in the content folder. <paramref name="download"/> fetches an address to a path.
    /// A mismatching SHA-256, or a missing one that the file requires, refuses the stage and removes what the file left.
    /// <paramref name="recordVerified"/> receives each kept file and its SHA-256.
    /// </summary>
    public static async Task StageAsync(
        AppDefinition def, string contentPath, string? feed, string rawVersion,
        Func<string, string, CancellationToken, Task> download,
        Action<string>? onProgress, Action<string, string>? recordVerified, CancellationToken ct)
    {
        Validate(def);
        foreach (var file in def.ExtraFiles ?? [])
        {
            var url = ResolveUrl(file, feed, rawVersion)
                ?? throw new InvalidOperationException($"The version source lists no download address for {file.FileName}.");
            var hash = ResolveHash(file, feed, rawVersion);
            if (string.IsNullOrWhiteSpace(hash) && file.RequireHash)
                throw new InvalidOperationException($"The version source carries no SHA256 for {file.FileName}; the file cannot be verified.");

            var path = Path.Combine(contentPath, file.FileName);
            if (File.Exists(path)) File.Delete(path);
            onProgress?.Invoke($"Downloading {file.FileName}...");
            await download(url, path, ct);
            try
            {
                StagingService.VerifyPayloadHash(path, hash, onProgress);
            }
            catch
            {
                try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                throw;
            }

            if (file.Kind.Equals(DependencyArchive, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var written = ExtractDependencies(path, file.ArchiveFolders!, Path.Combine(contentPath, MsixExtras.DependenciesFolder));
                    foreach (var item in written) onProgress?.Invoke($"Dependency staged: {item}");
                }
                finally
                {
                    try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                }
            }
            else if (!string.IsNullOrWhiteSpace(hash))
            {
                recordVerified?.Invoke(path, hash);
            }
        }
    }
}
