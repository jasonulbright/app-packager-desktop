# SRL Packr

**Enterprise application packaging automation for MECM/Intune.**

SRL Packr by Signal Ridge Labs automates the full lifecycle of third-party application packaging: version detection, installer download, staging, and MECM Application creation with detection rules.

## Status

**v2.1.0-alpha3** — 101 of 117 apps validated end-to-end on CM01 lab (Check Latest, Check MECM, Stage, Package).

## Stack

- .NET 10, WPF-UI 4.2.0, CommunityToolkit.Mvvm 8.4.2
- ApplicationPackagerEngine class library (scraper plugins, staging pipeline, MECM target)
- PowerShell 5.1 subprocess for ConfigurationManager cmdlets

## Architecture

```
ApplicationPackagerUI (WPF)          ← user interface
  └─ ApplicationPackagerEngine (DLL) ← core logic
       ├─ Scrapers/                  ← IVersionScraper plugins (HttpRegex, JsonQuery, FileVersion)
       ├─ Services/                  ← Download, MSI inspection, staging, MECM packaging
       ├─ Models/                    ← AppDefinition, StageManifest, detection configs
       └─ Definitions/              ← JSON app definitions (one per app)
```

## Scraper Types

| Scraper | Method | Apps |
|---------|--------|------|
| `HttpRegexScraper` | Fetch page/API, regex extract | 50+ |
| `JsonQueryScraper` | JSON API path queries | 35+ |
| `FileVersionScraper` | Download installer, read EXE/MSI properties | 8 |
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
