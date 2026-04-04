# Packr Regression Test Status — 2026-04-04

**Lab:** CM01 + CLIENT01 (Hyper-V, CM 2509)
**Total definitions:** 116
**Methodology:** Once an app passes Install + Detection, it is SIGNED OFF. Do not retest or redeploy. Next phase: Uninstall validation.

---

## Per-App Validation Table

Legend: Y = Pass, N = Fail, - = Not tested, D = Deferred, X = Excluded

| # | App | Check Latest | Check MECM | Stage | Package | Distribute | Deploy | Install | Detect | Uninstall |
|---|-----|-------------|-----------|-------|---------|-----------|--------|---------|--------|-----------|
| 1 | .NET 8 Desktop Runtime (x86+x64) | Y | Y | Y | Y | Y | Y | Y | Y | - |
| 2 | .NET 9 Desktop Runtime (x64) | Y | Y | Y | Y | Y | Y | Y | Y | - |
| 3 | .NET 10 Desktop Runtime (x64) | Y | Y | Y | Y | Y | Y | Y | Y | - |
| 4 | 7-Zip (x64) | Y | Y | Y | Y | Y | Y | Y | Y | - |
| 5 | Adobe Acrobat Reader DC (x64) | Y | Y | Y | Y | Y | Y | Y | Y | - |
| 6 | Amazon Corretto JDK 8 (x64) | Y | Y | Y | Y | Y | Y | Y | Y | - |
| 7 | Amazon Corretto JDK 8 (x86) | Y | Y | Y | Y | Y | Y | Y | Y | - |
| 8 | Amazon Corretto JDK 11 (x64) | Y | Y | Y | Y | Y | Y | Y | Y | - |
| 9 | Amazon Corretto JDK 11 (x86) | Y | Y | Y | Y | Y | Y | Y | Y | - |
| 10 | Amazon Corretto JDK 17 (x64) | Y | Y | Y | Y | Y | Y | Y | Y | - |
| 11 | Amazon Corretto JDK 21 (x64) | Y | Y | Y | Y | Y | Y | Y | Y | - |
| 12 | Amazon Corretto JDK 25 (x64) | Y | Y | Y | Y | Y | Y | Y | Y | - |
| 13 | Anaconda Distribution (x64) | Y | Y | Y | Y | Y | Y | Y | Y | - |
| 14 | AnyDesk | Y | Y | Y | Y | D | D | D | D | - |
| 15 | ASP.NET 8 Server Hosting Bundle (x64) | Y | Y | Y | Y | Y | Y | Y | Y | - |
| 16 | Audacity (x64) | Y | Y | Y | Y | Y | Y | Y | Y | - |
| 17 | Bitwarden Desktop (x64) | Y | Y | Y | Y | Y | Y | Y | Y | - |
| 18 | Brave Browser (x64) | Y | Y | Y | Y | Y | Y | Y | Y | - |
| 19 | CCleaner | Y | Y | Y | Y | Y | Y | Y | Y | - |
| 20 | Cisco Webex (x64) | Y | Y | Y | Y | Y | Y | Y | Y | - |
| 21 | Citrix Workspace (CR) (x64) | Y | Y | Y | Y | Y | Y | Y | Y | - |
| 22 | CPU-Z | Y | Y | Y | Y | D | D | D | D | - |
| 23 | CutePDF Writer | Y | Y | Y | Y | Y | Y | Y | Y | - |
| 24 | DBeaver Community (x64) | Y | Y | Y | Y | Y | Y | Y | Y | - |
| 25 | draw.io Desktop (x64) | Y | Y | Y | Y | Y | Y | Y | Y | - |
| 26 | Eclipse Temurin JDK 8 (x64) | Y | Y | Y | Y | Y | Y | Y | Y | - |
| 27 | Eclipse Temurin JDK 8 (x86) | Y | Y | Y | Y | Y | Y | Y | Y | - |
| 28 | Eclipse Temurin JDK 11 (x64) | Y | Y | Y | Y | Y | Y | Y | Y | - |
| 29 | Eclipse Temurin JDK 11 (x86) | Y | Y | Y | Y | Y | Y | Y | Y | - |
| 30 | Eclipse Temurin JDK 17 (x64) | Y | Y | Y | Y | Y | Y | Y | Y | - |
| 31 | Eclipse Temurin JDK 21 (x64) | Y | Y | Y | Y | Y | Y | Y | Y | - |
| 32 | Eclipse Temurin JDK 25 (x64) | Y | Y | Y | Y | Y | Y | Y | Y | - |
| 33 | Eclipse Temurin JRE 8 (x64) | Y | Y | Y | Y | Y | Y | Y | Y | - |
| 34 | Eclipse Temurin JRE 8 (x86) | Y | Y | Y | Y | Y | Y | Y | Y | - |
| 35 | Eclipse Temurin JRE 11 (x64) | Y | Y | Y | Y | Y | Y | Y | Y | - |
| 36 | Eclipse Temurin JRE 11 (x86) | Y | Y | Y | Y | Y | Y | Y | Y | - |
| 37 | Eclipse Temurin JRE 17 (x64) | Y | Y | Y | Y | Y | Y | Y | Y | - |
| 38 | Eclipse Temurin JRE 21 (x64) | Y | Y | Y | Y | Y | Y | Y | Y | - |
| 39 | Eclipse Temurin JRE 25 (x64) | Y | Y | Y | Y | Y | Y | Y | Y | - |
| 40 | Everything (x64) | Y | Y | Y | Y | Y | Y | Y | Y | - |
| 41 | FileZilla Client (x64) | X | X | X | X | X | X | X | X | X |
| 42 | Foxit Reader (x64) | Y | Y | X | X | X | X | X | X | X |
| 43 | GIMP (x64) | Y | Y | Y | Y | Y | Y | Y | Y | - |
| 44 | Git for Windows (x64) | Y | Y | Y | Y | Y | Y | Y | Y | - |
| 45 | Google Chrome (x64) | Y | Y | Y | Y | Y | Y | Y | Y | - |
| 46 | Greenshot (x64) | Y | Y | Y | Y | Y | Y | Y | Y | - |
| 47 | Inkscape (x64) | Y | Y | Y | Y | Y | Y | Y | Y | - |
| 48 | KeePass | Y | Y | Y | Y | Y | Y | Y | Y | - |
| 49 | LibreOffice (x64) | Y | Y | Y | Y | Y | Y | Y | Y | - |
| 50 | M365 Apps for Enterprise (x64) | Y | Y | Y | Y | Y | Y | Y | Y | - |
| 51 | M365 Apps for Enterprise (x86) | Y | Y | Y | Y | Y | Y | Y | Y | - |
| 52 | M365 Project (x64) | Y | Y | Y | Y | Y | Y | Y | Y | - |
| 53 | M365 Project (x86) | Y | Y | Y | Y | Y | Y | Y | Y | - |
| 54 | M365 Visio (x64) | Y | Y | Y | Y | Y | Y | Y | Y | - |
| 55 | M365 Visio (x86) | Y | Y | Y | Y | Y | Y | Y | Y | - |
| 56 | Malwarebytes (x64) | Y | Y | Y | Y | Y | Y | Y | Y | - |
| 57 | Microsoft Edge (x64) | Y | Y | Y | Y | Y | Y | Y | Y | - |
| 58 | Microsoft Edge WebView2 Runtime (x64) | Y | Y | Y | Y | Y | Y | Y | Y | - |
| 59 | Microsoft ODBC Driver 18 (x64) | Y | Y | Y | Y | Y | Y | Y | Y | - |
| 60 | Microsoft OLE DB Driver 19 (x64) | Y | Y | Y | Y | Y | Y | Y | Y | - |
| 61 | Microsoft Teams (x64) | Y | Y | Y | X | X | X | X | X | X |
| 62 | Mozilla Firefox (x64) | Y | Y | Y | Y | Y | Y | Y | Y | - |
| 63 | Mozilla Thunderbird (x64) | Y | Y | Y | Y | Y | Y | Y | Y | - |
| 64 | mRemoteNG (x64) | Y | Y | Y | Y | Y | Y | Y | Y | - |
| 65 | Node.js LTS (x64) | Y | Y | Y | Y | Y | Y | Y | Y | - |
| 66 | Notepad++ (x64) | Y | Y | Y | Y | Y | Y | Y | Y | - |
| 67 | Obsidian (x64) | Y | Y | Y | Y | D | D | D | D | - |
| 68 | Opera Browser (x64) | Y | Y | Y | Y | Y | Y | Y | Y | - |
| 69 | paint.net (x64) | Y | Y | Y | Y | Y | Y | Y | Y | - |
| 70 | pgAdmin 4 (x64) | Y | Y | Y | Y | D | D | D | D | - |
| 71 | Positron (x64) | Y | Y | Y | Y | Y | Y | Y | Y | - |
| 72 | Postman (x64) | Y | Y | Y | Y | D | D | D | D | - |
| 73 | PostgreSQL 13 (x64) | Y | Y | Y | Y | D | D | D | D | - |
| 74 | PostgreSQL 14 (x64) | Y | Y | Y | Y | D | D | D | D | - |
| 75 | PostgreSQL 15 (x64) | Y | Y | Y | Y | D | D | D | D | - |
| 76 | PostgreSQL 16 (x64) | Y | Y | Y | Y | D | D | D | D | - |
| 77 | PostgreSQL 17 (x64) | Y | Y | Y | Y | D | D | D | D | - |
| 78 | Power BI Desktop (x64) | Y | Y | Y | Y | Y | Y | Y | Y | - |
| 79 | PowerShell 7 (x64) | Y | Y | Y | Y | Y | Y | Y | Y | - |
| 80 | PowerShell 7 LTS (x64) | Y | Y | Y | Y | Y | Y | Y | Y | - |
| 81 | PowerToys (x64) | Y | Y | Y | Y | Y | Y | Y | Y | - |
| 82 | PuTTY (x64) | Y | Y | Y | Y | Y | Y | Y | Y | - |
| 83 | PyCharm Community (x64) | Y | Y | Y | Y | Y | Y | Y | Y | - |
| 84 | Python (x64) | Y | Y | Y | Y | Y | Y | Y | Y | - |
| 85 | R for Windows (x64) | Y | Y | Y | Y | Y | Y | Y | Y | - |
| 86 | RStudio Desktop (x64) | Y | Y | Y | Y | Y | Y | Y | Y | - |
| 87 | ShareX (x64) | Y | Y | Y | Y | Y | Y | Y | Y | - |
| 88 | Slack | Y | Y | X | X | X | X | X | X | X |
| 89 | SoapUI Open Source | Y | Y | Y | Y | Y | Y | Y | Y | - |
| 90 | SSMS 22 (x64) | Y | Y | Y | Y | Y | Y | Y | Y | - |
| 91 | Sysinternals Suite (x64) | Y | Y | Y | Y | D | D | D | D | - |
| 92 | Tableau Desktop (x64) | Y | Y | Y | X | X | X | X | X | X |
| 93 | Tableau Prep Builder (x64) | Y | Y | Y | X | X | X | X | X | X |
| 94 | Tableau Reader (x64) | Y | Y | Y | X | X | X | X | X | X |
| 95 | TeamViewer (x64) | Y | Y | Y | Y | Y | Y | Y | Y | - |
| 96 | TeamViewer Host (x64) | Y | Y | Y | Y | Y | Y | Y | Y | - |
| 97 | TortoiseGit (x64) | Y | Y | Y | Y | Y | Y | Y | Y | - |
| 98 | TortoiseSVN (x64) | Y | Y | Y | Y | Y | Y | Y | Y | - |
| 99 | TreeSize Free | Y | Y | Y | Y | Y | Y | Y | Y | - |
| 100 | Vim (x64) | Y | Y | Y | Y | Y | Y | Y | Y | - |
| 101 | Visual C++ Redistributable (x64) | Y | Y | Y | Y | Y | Y | Y | Y | - |
| 102 | Visual C++ Redistributable (x86) | Y | Y | Y | Y | Y | Y | Y | Y | - |
| 103 | Visual Studio 2026 Community (x64) | Y | Y | Y | Y | X | X | X | X | X |
| 104 | Visual Studio 2026 Enterprise (x64) | Y | Y | Y | Y | X | X | X | X | X |
| 105 | Visual Studio Code (x64) | Y | Y | Y | Y | Y | Y | Y | Y | - |
| 106 | VLC Media Player (x64) | Y | Y | Y | Y | Y | Y | Y | Y | - |
| 107 | VMware Tools (x64) | Y | Y | Y | Y | X | X | X | X | X |
| 108 | WinDirStat (x64) | Y | Y | Y | X | X | X | X | X | X |
| 109 | WinMerge (x64) | Y | Y | Y | X | X | X | X | X | X |
| 110 | WinRAR (x64) | Y | Y | Y | Y | Y | Y | Y | Y | - |
| 111 | WinSCP (x64) | Y | Y | Y | Y | Y | Y | Y | Y | - |
| 112 | Wireshark (x64) | Y | Y | Y | Y | Y | Y | Y | Y | - |
| 113 | WizTree (x64) | Y | Y | Y | X | X | X | X | X | X |
| 114 | XenCenter (x64) | Y | Y | Y | Y | Y | Y | Y | Y | - |
| 115 | XenServer VM Tools (x64) | Y | Y | Y | Y | Y | Y | Y | Y | - |
| 116 | Zoom Workplace (x64) | Y | Y | Y | Y | D | D | D | D | - |

---

## Summary

| Status | Count |
|--------|-------|
| Signed off (Install + Detect) | 90 |
| Deferred (install/distribution issues) | 11 |
| Excluded (architecture changes needed) | 13 |
| Vendor-gated (manual download) | 2 |
| Uninstall validated | 0 |

---

## Next Steps

1. **Uninstall validation** — test uninstall scripts for all 90 signed-off apps
2. **InstallerFramework field** — add to JSON schema, populate all definitions
3. **Installer metadata extraction** — 7z as core service; read installer manifests to auto-derive detection rules
4. **Resolve deferred apps** — per-user deployment, ZIP install scripts, installer arg fixes
5. **MSIX deployment type** — native CM MSIX support for Teams and Slack
6. **Burn bundle extraction** — 7-Zip + MSI inspection for Tableau apps
