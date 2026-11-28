using ApplicationPackagerUI.Models;

namespace MockProof;

/// <summary>The rows of the mock images (assets/grids.js of the mock): the same names, vendors, versions, states and CVE texts.</summary>
internal static class MockData
{
    private static readonly (string Name, string Vendor, string Current, string Latest, string State, string Cve)[] Apps =
    [
        ("Audacity (x64)", "Audacity", "3.7.9", "4.0.2", "update", "0 found"),
        ("Notepad++ (x64)", "Don HO", "", "8.9.9.1", "new", "No site version"),
        (".NET 8 Desktop Runtime (x64)", "Microsoft", "", "8.0.34", "new", "No site version"),
        ("AIMP (x64)", "AIMP DevTeam", "", "5.40.2731", "new", "No site version"),
        ("7-Zip (x64)", "7-Zip", "26.06", "26.06", "current", "No update"),
        ("Adobe Acrobat (64-bit)", "Adobe Inc.", "", "26.003.22110", "new", "No site version"),
        ("PuTTY (x64)", "Simon Tatham", "", "0.85", "new", "No site version"),
        ("Pidgin (x64)", "Pidgin", "", "", "unchecked", "Not checked"),
        ("PicPick (x64)", "NGWIN", "", "", "unchecked", "Not checked"),
        ("pgAdmin 4 (x64)", "pgAdmin Development Team", "", "", "unchecked", "Not checked"),
        ("PeaZip (x64)", "Giorgio Tani", "", "", "unchecked", "Not checked"),
        ("PDFsam Basic (x64)", "Sober Lemur S.r.l.", "", "", "unchecked", "CPE missing"),
        ("PDFgear (x64)", "PDFgear Software", "", "", "unchecked", "CPE missing"),
        (".NET 10 Desktop Runtime (x64)", "Microsoft", "", "", "unchecked", "Not checked"),
        ("PDF24 Creator (x64)", "geek software GmbH", "", "", "unchecked", "CPE missing"),
        ("Mozilla Firefox (x64)", "Mozilla", "", "", "unchecked", "Not checked"),
        ("VLC media player (x64)", "VideoLAN", "", "", "unchecked", "Not checked"),
        ("Git (x64)", "Git for Windows", "", "", "unchecked", "Not checked"),
        ("Google Chrome (x64)", "Google", "", "", "unchecked", "Not checked"),
        ("paint.net (x64)", "dotPDN LLC", "", "", "unchecked", "Not checked"),
    ];

    private static readonly string[] Marks = ["AU", "NP", "N8", "AI", "7Z", "AC", "PT", "PD", "PP", "PG", "PZ", "PB", "PF", "10", "24", "FF", "VC", "GI", "GO", "PA"];

    /// <summary>The line under the grid of the mock.</summary>
    public const string LastCheck = "CVE check complete · 1 checked · 5 skipped because ConfigMgr had no current version";

    public static List<PackagerInfo> Rows(bool everyCpe = false)
    {
        var rows = new List<PackagerInfo>();
        for (var i = 0; i < Apps.Length; i++)
        {
            var a = Apps[i];
            var row = new PackagerInfo
            {
                Uid = "mock" + i,
                Mark = Marks[i],
                Application = a.Name,
                Vendor = a.Vendor,
                CMName = a.Name,
                // A definition with a CPE shows the state of its last lookup; one without shows "CPE missing".
                Cpe = a.Cve == "CPE missing" && !everyCpe ? "" : "cpe:2.3:a:mock:mock:*:*:*:*:*:*:*:*",
            };
            row.CurrentVersion = a.Current;
            row.LatestVersion = a.Latest;
            row.Status = a.State switch
            {
                "update" => "Update available",
                "new" => "Absent from ConfigMgr",
                "current" => "Up to date",
                _ => "Ready",
            };
            row.CveCount = a.Cve switch { "0 found" => "0", "Not checked" or "CPE missing" => a.Cve == "CPE missing" ? "CPE missing" : "", _ => a.Cve };
            row.IsSelected = i == 0;
            rows.Add(row);
        }
        return rows;
    }
}
