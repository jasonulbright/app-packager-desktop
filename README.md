# SRL Packr

**Enterprise application packaging automation for MECM/Intune.**

SRL Packr by Signal Ridge Labs automates the full lifecycle of third-party application packaging: version detection, installer download, staging, and MECM Application creation with detection rules.

## Status

**v2.1.0-alpha7** — 90 of 116 apps signed off (install + detection validated on CM01 lab). Engine features: automated detection derivation via installer metadata extraction (7z + per-framework manifest readers), MSIX deployment type, per-user deployment, auto-distribute/deploy, SQLite version history, PSADT wrapper generation. See [REGRESSION-STATUS.md](REGRESSION-STATUS.md) for per-app pipeline status.

## Stack

- .NET 10, WPF-UI 4.2.0, CommunityToolkit.Mvvm 8.4.2
- ApplicationPackagerEngine class library (scraper plugins, staging pipeline, MECM target)
- PowerShell 5.1 subprocess for ConfigurationManager cmdlets

## Architecture

```
ApplicationPackagerUI (WPF)          ← user interface (WPF-UI 4.2.0 Fluent)
  └─ ApplicationPackagerEngine (DLL) ← core logic (portable .NET 10)
       ├─ Scrapers/                  ← IVersionScraper plugins (HttpRegex, JsonQuery, FileVersion, CdnCab)
       ├─ Services/                  ← Download, MSI inspection, 7z extraction, installer ID,
       │                                staging, MECM packaging, PSADT wrappers, SQLite history
       ├─ Models/                    ← AppDefinition, StageManifest, InstallerMetadata, InstallerParameter
       └─ Definitions/              ← JSON app definitions (116 apps, one per file)
```

## Scraper Types

| Scraper | Method | Apps |
|---------|--------|------|
| `HttpRegexScraper` | Fetch page/API, regex extract | 50+ |
| `JsonQueryScraper` | JSON API path queries | 35+ |
| `FileVersionScraper` | Download installer, read EXE/MSI/MSIX properties | 10 |
| `CdnCabScraper` | Office CDN CAB/VersionDescriptor.xml | 6 |
| `WingetScraper` | Winget package manager (stub) | — |

## JSON App Definition

```json
{
  "Schema": 1,
  "Id": "unique-opaque-id",
  "Vendor": "Google",
  "Application": "Google Chrome",
  "InstallerType": "MSI",
  "VersionScraper": {
    "Type": "HttpRegex",
    "Url": "https://api.example.com/versions",
    "Pattern": "\"version\":\"(\\d+\\.\\d+\\.\\d+\\.\\d+)\""
  },
  "DownloadUrl": "https://example.com/installer-{version}.msi",
  "Detection": {
    "Type": "RegistryKeyValue",
    "RegistryKeyTemplate": "SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\{ProductCode}"
  }
}
```

## License

Proprietary. Copyright Signal Ridge Labs.
