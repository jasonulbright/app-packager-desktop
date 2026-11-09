using System.Text;
using ApplicationPackagerEngine.Models;

namespace ApplicationPackagerEngine.Services;

/// <summary>
/// Which shipped definitions verify a downloaded payload, and how. A definition verifies the payload by a pinned SHA-256
/// (<c>PayloadSha256</c>), a SHA-256 read from the version feed (<c>VersionScraper.PayloadHashQuery</c>) or from a vendor
/// checksum file (<c>PayloadSha256Url</c>), and by a required Authenticode signature (<c>RequireValidSignature</c>).
/// <c>RequirePayloadHash</c> makes a missing hash a refusal. A definition with none of these stages after the file-type check only.
/// The text is generated; <c>docs/reference/payload-verification.md</c> holds it and a test fails when the two differ.
/// </summary>
public static class PayloadVerificationReport
{
    /// <summary>How one definition verifies its payload.</summary>
    public sealed record Entry(string File, string Application, bool PinnedHash, bool FeedHash, bool ChecksumFile, bool RequireHash, bool RequireSignature)
    {
        /// <summary>True when a SHA-256 is pinned, read from the version feed or read from a checksum file.</summary>
        public bool HasHashSource => PinnedHash || FeedHash || ChecksumFile;
    }

    /// <summary>Reads how a definition verifies its payload.</summary>
    public static Entry Describe(string fileName, AppDefinition def) => new(
        fileName, def.Application,
        PinnedHash: !string.IsNullOrWhiteSpace(def.PayloadSha256),
        FeedHash: !string.IsNullOrWhiteSpace(def.VersionScraper?.PayloadHashQuery),
        ChecksumFile: !string.IsNullOrWhiteSpace(def.PayloadSha256Url),
        RequireHash: def.RequirePayloadHash,
        RequireSignature: def.RequireValidSignature);

    /// <summary>The Markdown page for a set of definitions, sorted by file name; line ends are LF.</summary>
    public static string Build(IEnumerable<Entry> entries)
    {
        var all = entries.OrderBy(e => e.File, StringComparer.OrdinalIgnoreCase).ToList();
        var hashed = all.Where(e => e.HasHashSource).ToList();
        var signed = all.Where(e => e.RequireSignature).ToList();
        var neither = all.Count(e => !e.HasHashSource && !e.RequireSignature);

        var text = new StringBuilder();
        void Line(string value = "") => text.Append(value).Append('\n');

        Line("# Payload verification");
        Line();
        Line("This page is generated from the shipped definitions. A test fails when it differs from them. It lists which definitions verify the file that they download, and how.");
        Line();
        Line("## What the stage checks");
        Line();
        Line("- **Always:** the file type of the download. An EXE must start with `MZ`, an MSI or MSP with the OLE header, a ZIP with `PK`, an MSIX must be a valid package. This finds an HTML page that a vendor endpoint returned in place of the installer. It does not find a replaced or tampered file.");
        Line("- **When a SHA-256 is known:** the stage compares the file with it and refuses a mismatch. A SHA-256 is known when the definition pins it (`PayloadSha256`), the version feed publishes it (`VersionScraper.PayloadHashQuery`) or a vendor checksum file lists it (`PayloadSha256Url`).");
        Line("- **When the definition sets `RequirePayloadHash`:** a missing SHA-256 refuses the stage.");
        Line("- **When the definition sets `RequireValidSignature`:** the download must carry a valid Authenticode signature. An installer from a local source folder is always checked.");
        Line("- **When no SHA-256 is known:** the stage run log says `Payload SHA256 NOT verified`, and the file passes after the file-type check.");
        Line();
        Line("## Counts");
        Line();
        Line("| Item | Definitions |");
        Line("|---|---|");
        Line($"| Shipped definitions | {all.Count} |");
        Line($"| With a pinned SHA-256 (`PayloadSha256`) | {all.Count(e => e.PinnedHash)} |");
        Line($"| With a SHA-256 from the version feed (`PayloadHashQuery`) | {all.Count(e => e.FeedHash)} |");
        Line($"| With a SHA-256 from a checksum file (`PayloadSha256Url`) | {all.Count(e => e.ChecksumFile)} |");
        Line($"| With any SHA-256 source | {hashed.Count} |");
        Line($"| That require a SHA-256 (`RequirePayloadHash`) | {all.Count(e => e.RequireHash)} |");
        Line($"| That require a valid signature (`RequireValidSignature`) | {signed.Count} |");
        Line($"| With no SHA-256 source and `RequireValidSignature` not set | {neither} |");
        Line();
        Line("The counts describe the settings of the definitions. An installer from a local source folder is always checked for a valid signature; the last count does not include that check.");
        Line();
        Line("## Definitions with a SHA-256 source");
        Line();
        if (hashed.Count == 0) Line("None.");
        else
        {
            Line("| Definition | Pinned | Version feed | Checksum file | Required |");
            Line("|---|---|---|---|---|");
            foreach (var e in hashed)
                Line($"| `{e.File}` | {Mark(e.PinnedHash)} | {Mark(e.FeedHash)} | {Mark(e.ChecksumFile)} | {Mark(e.RequireHash)} |");
        }
        Line();
        Line("## Definitions that require a valid signature");
        Line();
        if (signed.Count == 0) Line("None.");
        else foreach (var e in signed) Line($"- `{e.File}`");
        return text.ToString();
    }

    private static string Mark(bool value) => value ? "Yes" : "";
}
