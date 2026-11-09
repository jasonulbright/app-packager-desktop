# Network calls

This page lists every outbound connection that App Packager Desktop makes.

The app has no telemetry. The code has no analytics call. A search of the source for web addresses finds no outbound address other than the ones on this page.

Every call starts from a user action, except where the table says "background".

## Columns

- **Trigger**: "User" means that you start the call with a button, a command or a command-line run. "Background" means that the app calls without a user action.
- **Settings**: the settings or variables that change the call. See [Settings reference](settings.md).

## Internet calls

| Host (kind) | Purpose | Trigger | Settings |
|---|---|---|---|
| Vendor version and download hosts. The addresses are in each definition (`VersionScraper.Url`, `DownloadUrl`). The app calls them with `curl.exe`. | Version check and installer download | User: Check Latest, Stage, Publish, One Click, command line | The definition. `FetchRetries` and `FetchMaxTimeSeconds` in a definition. |
| `api.github.com` | Version check for the definitions that read GitHub releases (101 of 319 definitions) | User | `APP_PACKAGER_GITHUB_TOKEN`, `GITHUB_TOKEN`, or `gh auth login` raise the rate limit |
| `api.github.com/rate_limit`, `api.github.com/user` | The `Refresh` button of the GitHub API section in Settings | User | The same token sources |
| `api.github.com/repos/jasonulbright/app-packager-desktop/releases/latest`, and `github.com/jasonulbright/app-packager-desktop/releases/download/` for the Setup exe and `checksums.txt` | Update check and update download. The app checks the SHA-256 value and the signature. No token is sent. | Background at start, once; User: `Check for updates`, `Download` | `CheckForUpdatesAtStartup` |
| `api.github.com/repos/jasonulbright/app-packager-icons/releases/latest` and its assets | Icon pack download. The app checks the SHA-256 value. | User: `Download icon pack` | None |
| `github.com/microsoft/Microsoft-Win32-Content-Prep-Tool/raw/v1.8.7/IntuneWinAppUtil.exe` | Download of the Win32 Content Prep Tool from the tagged release 1.8.7. The app checks the pinned SHA-256 and the publisher signature. | User: `Download` in Settings, or the first Intune build | None |
| `officecdn.microsoft.com` (`/pr/wsus/setup.exe`, `/pr/{channel guid}/Office/Data/v64.cab`) | The Office Deployment Tool and the version descriptor of Microsoft 365 | User | `M365Channel` |
| `downloads.dell.com/catalog/CatalogPC.cab` | The Dell catalog scraper | User | The definition |
| `login.microsoftonline.com` | Entra sign-in (token) for Intune publishing | User: Publish to Intune | `Intune.TenantId`, `Intune.ClientId`, `Intune.ClientSecretProtected` |
| `graph.microsoft.com/v1.0` | Intune application and content upload calls | User: Publish to Intune | The same |
| The Azure storage address that Graph returns at run time | The Intune content upload. The code has no fixed storage host. | User: Publish to Intune | The same |
| `services.nvd.nist.gov/rest/json/cves/2.0` | CVE query | User: Check CVEs, lookup after Check ConfigMgr, `--cve-report` | `Vulnerability.NvdApiKeyProtected`, `NVD_API_KEY`, `Vulnerability.LookupAfterCheckCm`, `Vulnerability.CacheTtlMinutes` |
| `cvedb.shodan.io/cves` | CVE query when the NVD query failed | User | The same |
| `api.first.org/data/v1/epss` | EPSS scores. One request for each 100 CVE IDs. | User | The same |
| `www.cisa.gov/sites/default/files/feeds/known_exploited_vulnerabilities.json` | Known Exploited Vulnerabilities catalog. One time in 24 hours. | User | The same |
| The webhook host that you set (https only) | The run summary of a CVE report run | User: `--cve-report` with `--webhook-url` or the configuration | `--webhook-url`, `Notifications.WebhookUrl` in `monitor-config.json` |
| The timestamp server that you set (http or https) | Authenticode timestamp for signed scripts | User: Stage or Publish with signing | `ScriptSigning.TimestampServer`, `TimestampRequired` |

The CVE requests use https. No other process than the window or `AppPackagerBuild.exe` sends them. The log shows each request address. It does not show the API key. It shows only the host of the webhook address.

## Calls to your own systems

| Host (kind) | Purpose | Trigger | Settings |
|---|---|---|---|
| The SMS Provider (DCOM and WinRM, through the ConfigMgr console module) | All ConfigMgr reads and writes | User | `SiteCode`, `ProviderMachine` |
| The SMS Provider, `https://<provider>/AdminService/...` | CMPivot (Pivots and Scripts) | User | `ProviderMachine` (a host name without `https://` or a path) |
| The SMS Provider, CIM over DCOM then WinRM | Discover: reads the site definition to find the SQL Server name | User | `SiteCode`, `ProviderMachine` |
| The SQL Server of the site database (Windows authentication, read only) | Discover inventory. Site Hygiene Live Client Health and Inactive Devices. The `Test SQL` button. | User. Background for the Live auto-refresh. | `DiscoverySqlServer`, `DiscoverySqlDatabase`, `SiteHygieneLive.TrustSqlCertificate`, `SiteHygieneLive.RefreshMinutes` |
| The WSUS server, port 8530 or 8531 (administration API), and its shares `UpdateServicesPackages` and `WSUSContent` | Publish, approve, decline, expire, remove, import | User | `Wsus.ServerName`, `Wsus.PortNumber`, `Wsus.UseSsl` |
| File servers named by content source paths | The content source probe (Site Hygiene check APP-04). Off by default. At most 200 paths. | User | `SiteHygiene.ProbeContentSources` |
| `FileShareRoot` (SMB) | Content copy for `Publish to ConfigMgr` | User | `FileShareRoot`, `ContentLayout` |
| The audit and ring folders (UNC allowed) | Shared logs of Deployment Helper and Rings | User | `DeploymentHelper.AuditLogPath`, `RingsFolder`, `RunsFolder` |

## SQL views that Discover and Site Hygiene Live read

- Discover: `v_GS_ADD_REMOVE_PROGRAMS`, `v_GS_ADD_REMOVE_PROGRAMS_64`, `v_R_System`, `v_Applications`, `v_ConfigurationItems`.
- Site Hygiene Live: `v_CH_ClientSummary`, `v_R_System`.

Discover sends no request to the internet.

## Calls that only open a link

The app opens these links in your browser. The app sends no request to the linked site.

- NVD, CVE.org, EUVD, OpenCVE, cvedetails, OSV, Shodan CVEDB, CISA KEV and the vendor advisory, from the CVE window.
- `https://github.com/jasonulbright/app-packager-desktop/releases`, from `Releases` in Settings.
- `catalog.update.microsoft.com`, from `Open the catalog` in the WSUS catalog import window. The WSUS server downloads the metadata itself.

## Local processes that are not network calls

- `gh auth token` runs to find a GitHub token when no token variable is set.
- `curl.exe` runs for version checks and downloads.
- `powershell.exe` (Windows PowerShell 5.1) runs the ConfigMgr session, Discover, the detection script host and other scripts.

## Configured time limits

These values are fixed in the code. They are limits, not predictions.

| Call | Limit |
|---|---|
| HttpRegex page fetch (default for each attempt) | 25 seconds. A definition can set `FetchMaxTimeSeconds`. |
| HttpRegex pause between retries | 5 seconds. A retry happens only after a timeout or an HTTP 5xx answer. A definition can set `FetchRetries` (default 0). |
| JsonQuery | 25 seconds |
| FileVersion | 120 seconds |
| CdnCab, Dell catalog, HeadProbe | 30, 180 and 20 seconds |
| Installer download | 1 retry (2 attempts), 5 seconds between attempts. The code sets no time limit for the download itself. |
| Content Prep Tool run | 900 seconds |
| Intune: wait for the upload address | 600 seconds, polled every 3 seconds. The commit poll runs every 5 seconds. |
| ConfigMgr session: connect, one command | 2 minutes and 5 minutes |
| Discover scan | 600 seconds. The SQL connect limit is 30 seconds. |
| Site Hygiene Live SQL command | 120 seconds |
| AdminService connect (CMPivot) | 15 seconds. The app refuses an answer larger than 64 MB. |
| CVE HTTP client | 5 minutes. Response cap 64 MiB. At most 3 redirects. |
| NVD rate limit used by the app | 5 requests in 30 seconds without a key, 50 with a key |
| GitHub API status check | 20 seconds |

## Related pages

- [Permissions reference](permissions.md)
- [Data folders](data-folders.md)
- [CVE lookup](../core/cve-lookup.md)

## Links

- [What is the administration service in Configuration Manager?](https://learn.microsoft.com/intune/configmgr/develop/adminservice/overview): Explains the administration service, the REST API of the SMS Provider.
- [Authentication and authorization basics](https://learn.microsoft.com/graph/auth/auth-concepts): Explains how an app gets an access token for Graph.
- [FIRST.Org REST API: GET /epss](https://api.first.org/epss/): Documents the API call that returns EPSS scores.
- [Known Exploited Vulnerabilities Catalog](https://www.cisa.gov/known-exploited-vulnerabilities-catalog): Lists the CVEs that attackers are known to use.
- [Ports used in Configuration Manager](https://learn.microsoft.com/intune/configmgr/core/plan-design/hierarchy/ports): Lists the ports that ConfigMgr uses.
