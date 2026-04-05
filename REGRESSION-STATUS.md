# Packr Regression Test Status — 2026-04-05

**Lab:** CM01 + CLIENT01 (Hyper-V, CM 2509)
**Total definitions:** 117
**Test harness:** Remote (host-based PSRemoting with automated reboots) + Local (C# harness, SYSTEM via psexec)
**Clean VM:** CLIENT01 rebuilt 2026-04-05, prereqs: 7-Zip, MSVC x64/x86, .NET 8/9/10, PsExec

---

## Per-App Status

Legend: P = Pass, F = Fail, S = Skip, T = Timeout, - = Not tested, X = Excluded/Deferred

| # | App | Stage | Install | Detect | Uninstall | Clean | Notes |
|---|-----|-------|---------|--------|-----------|-------|-------|
| 1 | .NET 8 Desktop Runtime (x64) | P | P | P | P | P | FULL PASS (clean VM) |
| 2 | .NET 8 Desktop Runtime (x86) | P | P | P | P | P | FULL PASS (clean VM) |
| 3 | .NET 9 Desktop Runtime (x64) | P | P | P | P | P | FULL PASS (clean VM) |
| 4 | .NET 10 Desktop Runtime (x64) | X | X | X | X | X | Deferred - test last (harness runtime) |
| 5 | 7-Zip (x64) | P | P | P | P | P | FULL PASS. Skipped in harness (dependency) |
| 6 | Adobe Acrobat Reader DC (x64) | P | P | P | P | F | Uninstall leaves ARP traces |
| 7 | Amazon Corretto JDK 8 (x64) | P | P | P | P | P | FULL PASS |
| 8 | Amazon Corretto JDK 8 (x86) | P | P | P | P | P | FULL PASS |
| 9 | Amazon Corretto JDK 11 (x64) | P | P | P | P | P | FULL PASS |
| 10 | Amazon Corretto JDK 11 (x86) | P | P | P | P | P | FULL PASS |
| 11 | Amazon Corretto JDK 17 (x64) | P | P | P | P | P | FULL PASS |
| 12 | Amazon Corretto JDK 21 (x64) | P | P | P | P | P | FULL PASS |
| 13 | Amazon Corretto JDK 25 (x64) | P | P | P | P | P | FULL PASS |
| 14 | Anaconda Distribution (x64) | P | F | - | - | - | Exit 2 - path conflict |
| 15 | AnyDesk | P | F | - | - | - | Exit 11341829 - needs interactive |
| 16 | ASP.NET 8 Hosting Bundle (x64) | P | P | P | F | P | Uninstall exit 1618 (MSI busy) |
| 17 | Audacity (x64) | P | P | P | F | F | InnoSetup uninstall broken |
| 18 | Bitwarden Desktop (x64) | P | P | P | F | F | NSIS uninstall broken |
| 19 | Brave Browser (x64) | P | F | - | - | - | Squirrel hangs under SYSTEM |
| 20 | CCleaner | P | P | F | - | - | Detection fails |
| 21 | Cisco Webex (x64) | P | P | P | P | P | FULL PASS |
| 22 | Citrix Workspace (CR) (x64) | P | F | - | - | - | Missing prereqs (.NET 8 + MSVC) |
| 23 | CPU-Z | P | P | F | - | - | Detection path issue |
| 24 | CutePDF Writer | P | F | - | - | - | InnoSetup silent args |
| 25 | DBeaver Community (x64) | P | P | P | P | F | NSIS uninstall leaves traces |
| 26 | draw.io Desktop (x64) | P | P | P | P | P | FULL PASS |
| 27 | Eclipse Temurin JDK 8 (x64) | P | P | P | P | P | FULL PASS |
| 28 | Eclipse Temurin JDK 8 (x86) | P | P | P | P | P | FULL PASS |
| 29 | Eclipse Temurin JDK 11 (x64) | P | P | P | P | P | FULL PASS |
| 30 | Eclipse Temurin JDK 11 (x86) | P | P | P | P | P | FULL PASS |
| 31 | Eclipse Temurin JDK 17 (x64) | P | P | P | P | P | FULL PASS |
| 32 | Eclipse Temurin JDK 21 (x64) | P | P | P | P | P | FULL PASS |
| 33 | Eclipse Temurin JDK 25 (x64) | P | P | P | P | P | FULL PASS |
| 34 | Eclipse Temurin JRE 8 (x64) | P | P | P | P | P | FULL PASS |
| 35 | Eclipse Temurin JRE 8 (x86) | P | P | P | P | P | FULL PASS |
| 36 | Eclipse Temurin JRE 11 (x64) | P | P | P | P | P | FULL PASS |
| 37 | Eclipse Temurin JRE 11 (x86) | P | P | P | P | P | FULL PASS |
| 38 | Eclipse Temurin JRE 17 (x64) | P | P | P | P | P | FULL PASS |
| 39 | Eclipse Temurin JRE 21 (x64) | P | P | P | P | P | FULL PASS |
| 40 | Eclipse Temurin JRE 25 (x64) | P | P | P | P | P | FULL PASS |
| 41 | Everything (x64) | P | P | P | P | P | FULL PASS |
| 42 | FileZilla Client (x64) | X | X | X | X | X | PUP/adware |
| 43 | Foxit Reader (x64) | X | X | X | X | X | CDN throttle |
| 44 | GIMP (x64) | P | P | P | P | F | InnoSetup uninstall leaves traces |
| 45 | Git for Windows (x64) | P | P | P | P | F | Uninstall leaves traces |
| 46 | Google Chrome (x64) | P | P | P | P | P | FULL PASS (clean VM) |
| 47 | Greenshot (x64) | P | F | - | - | - | InnoSetup exit 5 (access denied) |
| 48 | Inkscape (x64) | P | P | P | P | P | FULL PASS |
| 49 | KeePass | P | P | P | P | P | FULL PASS |
| 50 | LibreOffice (x64) | P | P | P | P | P | FULL PASS |
| 51 | M365 Apps (x64) | P | P | F | - | - | ODT setup.exe didn't run in harness |
| 52 | M365 Apps (x86) | P | P | F | - | - | Same |
| 53 | M365 Project (x64) | P | P | F | - | - | Same |
| 54 | M365 Project (x86) | P | P | F | - | - | Same |
| 55 | M365 Visio (x64) | P | P | F | - | - | Same |
| 56 | M365 Visio (x86) | P | P | F | - | - | Same |
| 57 | Malwarebytes (x64) | P | P | P | P | F | Service persists |
| 58 | Microsoft Edge (x64) | P | P | P | S | S | CanUninstall=false |
| 59 | Microsoft Edge WebView2 (x64) | P | P | P | P | F | Uninstall leaves traces |
| 60 | Microsoft ODBC 18 (x64) | P | F | - | - | - | Exit 1603 |
| 61 | Microsoft OLE DB 19 (x64) | P | P | P | P | P | FULL PASS |
| 62 | Microsoft Teams (x64) | X | X | X | X | X | MSIX - deferred |
| 63 | Mozilla Firefox (x64) | P | P | P | F | F | Uninstall exit 1605 |
| 64 | Mozilla Thunderbird (x64) | P | P | P | F | F | Uninstall exit 1605 |
| 65 | mRemoteNG (x64) | P | P | P | P | P | FULL PASS |
| 66 | Node.js LTS (x64) | P | P | P | P | P | FULL PASS |
| 67 | Notepad++ (x64) | P | P | P | P | F | NSIS uninstall leaves traces |
| 68 | Obsidian (x64) | P | P | F | - | - | Per-user, /D= not working |
| 69 | Opera Browser (x64) | P | P | P | P | F | Uninstall leaves traces |
| 70 | paint.net (x64) | P | P | P | P | P | FULL PASS |
| 71 | pgAdmin 4 (x64) | P | P | F | - | - | Detection path issue |
| 72 | Positron (x64) | P | P | P | P | F | Uninstall leaves traces |
| 73 | PostgreSQL 13 (x64) | P | F | - | - | - | Port conflict |
| 74 | PostgreSQL 14 (x64) | P | F | - | - | - | Port conflict |
| 75 | PostgreSQL 15 (x64) | P | F | - | - | - | Port conflict |
| 76 | PostgreSQL 16 (x64) | P | P | P | P | F | Data dir persists |
| 77 | PostgreSQL 17 (x64) | P | F | - | - | - | Port conflict |
| 78 | Postman (x64) | P | P | F | - | - | Per-user Squirrel |
| 79 | Power BI Desktop (x64) | P | P | P | P | F | Uninstall leaves traces |
| 80 | PowerShell 7 (x64) | P | P | P | P | P | FULL PASS |
| 81 | PowerShell 7 LTS (x64) | P | P | F | - | - | ProductCode detection |
| 82 | PowerToys (x64) | P | P | P | P | F | Uninstall leaves traces |
| 83 | PuTTY (x64) | P | P | P | P | P | FULL PASS |
| 84 | PyCharm Community (x64) | P | T | - | - | - | Timeout under SYSTEM |
| 85 | Python (x64) | P | F | - | - | - | Exit 1603 |
| 86 | R for Windows (x64) | P | P | P | P | F | InnoSetup uninstall leaves traces |
| 87 | RStudio Desktop (x64) | P | P | P | P | F | Uninstall leaves traces |
| 88 | ShareX (x64) | P | P | P | P | F | InnoSetup uninstall leaves traces |
| 89 | Slack | X | X | X | X | X | MSIX - deferred |
| 90 | SoapUI Open Source | P | P | P | P | F | Uninstall leaves traces |
| 91 | SSMS 22 (x64) | P | P | P | P | F | WiX Burn complex uninstall |
| 92 | Sysinternals Suite (x64) | P | P | F | - | - | ZIP, no ARP |
| 93 | Tableau Desktop (x64) | P | P | P | P | F | WiX Burn uninstall |
| 94 | Tableau Prep Builder (x64) | P | P | P | P | F | Same |
| 95 | Tableau Reader (x64) | P | P | P | P | F | Same |
| 96 | TeamViewer (x64) | P | F | - | - | - | Exit 1603 |
| 97 | TeamViewer Host (x64) | P | P | P | P | F | Service persists |
| 98 | TortoiseGit (x64) | P | P | P | P | P | FULL PASS |
| 99 | TortoiseSVN (x64) | P | P | P | P | P | FULL PASS |
| 100 | TreeSize Free | P | T | - | - | - | Timeout under SYSTEM |
| 101 | Vim (x64) | P | P | P | P | F | InnoSetup uninstall |
| 102 | Visual C++ Redistributable (x64) | P | P | P | P | F | Shared components (by design) |
| 103 | Visual C++ Redistributable (x86) | P | P | P | P | F | Same |
| 104 | Visual Studio 2026 Community | X | X | X | X | X | Too large |
| 105 | Visual Studio 2026 Enterprise | X | X | X | X | X | Too large |
| 106 | Visual Studio Code (x64) | P | T | - | - | - | Timeout under SYSTEM |
| 107 | VLC Media Player (x64) | P | P | P | P | P | FULL PASS |
| 108 | VMware Tools (x64) | X | X | X | X | X | Hyper-V |
| 109 | WinDirStat (x64) | P | P | P | P | P | FULL PASS |
| 110 | WinMerge (x64) | F | - | - | - | - | Stage MSI error |
| 111 | WinRAR (x64) | P | P | P | P | F | NSIS uninstall |
| 112 | WinSCP (x64) | P | P | P | P | F | InnoSetup uninstall |
| 113 | Wireshark (x64) | P | P | P | P | F | InnoSetup uninstall |
| 114 | WizTree (x64) | F | - | - | - | - | Stage MSI error |
| 115 | XenCenter (x64) | P | P | F | - | - | Detection issue |
| 116 | XenServer VM Tools (x64) | P | P | P | P | P | FULL PASS |
| 117 | Zoom Workplace (x64) | P | P | F | - | - | MSI detection mismatch |

---

## Summary

| Status | Count |
|--------|-------|
| **FULL PASS** | 42 |
| **Install + Detect PASS, uninstall needs work** | 27 |
| **Install PASS, Detect FAIL** | 12 |
| **Install FAIL** | 12 |
| **Stage FAIL** | 2 |
| **Timeout** | 4 |
| **Excluded/Deferred** | 10 |
| **Not tested (clean run cut short)** | 8 |
