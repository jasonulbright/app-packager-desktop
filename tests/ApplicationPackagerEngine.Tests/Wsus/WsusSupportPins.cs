namespace ApplicationPackagerEngine.Tests.Wsus;

/// <summary>
/// The WSUS result of each definition whose compatibility check reads the Add/Remove
/// Programs entry out of the staged MSI. The check on a stand-in installer cannot decide
/// between its two results for these, so the result of the real installer is recorded here.
/// A definition of this kind that is not in the table fails the test.
/// </summary>
internal static class WsusSupportPins
{
    private const string NotMappable = "No (DetectionNotMappable)";

    private static readonly string[] MsiCarriesAUsableEntry =
    [
        "7zip.json", "agentransack.json", "amazondcv.json", "amazonworkspaces.json", "arduinoide.json", "asperaconnect.json",
        "awscli.json", "awssamcli.json", "awstools.json", "awsvpnclient.json", "axcrypt.json", "azurecli.json",
        "azurefunctionscore.json", "azurepowershell.json", "blender.json", "boxdrive.json", "calibre.json",
        "chefworkstation.json", "chrome-x64.json", "chromeremotedesktophost.json", "clockify.json", "cloudflarewarp.json",
        "cmake.json", "colourcontrastanalyser.json", "corretto-jdk11-x64.json", "corretto-jdk11-x86.json",
        "corretto-jdk17-x64.json", "corretto-jdk21-x64.json", "corretto-jdk25-x64.json", "corretto-jdk8-x64.json",
        "corretto-jdk8-x86.json", "cryptomator.json", "cura.json", "dbbrowsersqlite.json", "dngrep.json", "draftable.json",
        "drawio.json", "duodesktop.json", "everything.json", "gcpw.json", "geogebra.json", "githubcli.json", "go.json",
        "grepwin.json", "gsudo.json", "iapdesktop.json", "imageglass.json", "inkscape.json", "intunedebugtoolkit.json",
        "keepass.json", "keepassxc.json", "kreya.json", "liberica-jdk21.json", "libreoffice.json", "mariadb-server.json",
        "mattermost.json", "mongodbcompass.json", "mremoteng.json", "msodbcsql18.json", "msoledb.json",
        "mysqlconnectornet.json", "naps2.json", "netbird.json", "netlogo.json", "networkmanager.json", "nodejs.json",
        "openvpn.json", "ownclouddesktop.json", "paintdotnet.json", "pandoc.json", "parallelsclient.json", "pdf24creator.json",
        "pdfsam.json", "powershell7.json", "powershell7lts.json", "putty.json", "pwsafe.json", "qgis-ltr.json", "qgis.json",
        "rancherdesktop.json", "redshiftodbc.json", "remotedesktopmanager.json", "renderdoc.json", "rocketchat.json",
        "rvtools.json", "screentogif.json", "semeru-jdk11.json", "semeru-jdk17.json", "semeru-jdk8.json", "semeru-jre11.json",
        "semeru-jre17.json", "semeru-jre8.json", "sharepointonlinemanagementshell.json", "smartty.json",
        "softerraldapbrowser.json", "synologydriveclient.json", "tabulareditor2.json", "tailscale.json", "teamviewer.json",
        "temurin-jdk11-x64.json", "temurin-jdk11-x86.json", "temurin-jdk17-x64.json", "temurin-jdk21-x64.json",
        "temurin-jdk25-x64.json", "temurin-jdk8-x64.json", "temurin-jdk8-x86.json", "temurin-jre11-x64.json",
        "temurin-jre11-x86.json", "temurin-jre17-x64.json", "temurin-jre21-x64.json", "temurin-jre25-x64.json",
        "temurin-jre8-x64.json", "temurin-jre8-x86.json", "tightvnc.json", "tortoisegit.json", "tortoisehg.json",
        "tortoisesvn.json", "urbackupclient.json", "vagrant.json", "veracrypt.json", "vlc.json", "vscodium.json", "webex.json",
        "wireguard.json", "xencenter.json", "xenservervmtools.json", "yubicoauthenticator.json", "yubicopivtool.json",
        "yubikeymanagercli.json", "zeal.json", "zoom.json", "zulip.json", "zulu-jdk11.json", "zulu-jdk17.json",
        "zulu-jdk8.json", "zulu-jre11.json", "zulu-jre17.json", "zulu-jre8.json"
    ];

    private static readonly string[] MsiCarriesNoUsableEntry =
    [
        "audacity.json", "musescore.json", "nextcloud.json", "rustdesk.json", "smathstudio.json"
    ];

    public static readonly IReadOnlyDictionary<string, string> Expected =
        MsiCarriesAUsableEntry.Select(name => (name, value: "Yes"))
            .Concat(MsiCarriesNoUsableEntry.Select(name => (name, value: NotMappable)))
            .ToDictionary(pair => pair.name, pair => pair.value, StringComparer.OrdinalIgnoreCase);
}
