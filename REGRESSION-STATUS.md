# Packr Regression Test Status — 2026-04-05

**Lab:** CM01 + CLIENT01 (Hyper-V, CM 2509)
**Total definitions:** 117
**Test harness:** Remote (host-based PSRemoting with automated reboots) + Local (C# harness, SYSTEM via psexec)
**Clean VM:** CLIENT01 rebuilt 2026-04-05, prereqs: 7-Zip, MSVC x64/x86, .NET 8/9/10, PsExec

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

---

## Install Failures (12 apps)

| # | App | Stage | Install | Detect | Uninstall | Clean | Notes |
|---|-----|-------|---------|--------|-----------|-------|-------|
| 1 | Anaconda Distribution (x64) | P | F | - | - | - | Exit 2 - path conflict |
| 2 | AnyDesk | P | F | - | - | - | Exit 11341829 - needs interactive |
| 3 | Brave Browser (x64) | P | F | - | - | - | Squirrel hangs under SYSTEM |
| 4 | Citrix Workspace (CR) (x64) | P | F | - | - | - | Missing prereqs (.NET 8 + MSVC) |
| 5 | CutePDF Writer | P | F | - | - | - | InnoSetup silent args |
| 6 | Greenshot (x64) | P | F | - | - | - | InnoSetup exit 5 (access denied) |
| 7 | Microsoft ODBC 18 (x64) | P | F | - | - | - | Exit 1603 - pending reboot |
| 8 | PostgreSQL 13 (x64) | P | F | - | - | - | Port conflict with PG16 |
| 9 | PostgreSQL 14 (x64) | P | F | - | - | - | Port conflict |
| 10 | PostgreSQL 15 (x64) | P | F | - | - | - | Port conflict |
| 11 | PostgreSQL 17 (x64) | P | F | - | - | - | Port conflict |
| 12 | Python (x64) | P | F | - | - | - | Exit 1603 - version conflict |

## Detection Failures (12 apps)

| # | App | Stage | Install | Detect | Uninstall | Clean | Notes |
|---|-----|-------|---------|--------|-----------|-------|-------|
| 1 | CCleaner | P | P | F | - | - | Detection rule mismatch |
| 2 | CPU-Z | P | P | F | - | - | Detection path issue |
| 3 | M365 Apps (x64) | P | P | F | - | - | ODT setup.exe didn't run in harness |
| 4 | M365 Apps (x86) | P | P | F | - | - | Same |
| 5 | M365 Project (x64) | P | P | F | - | - | Same |
| 6 | M365 Project (x86) | P | P | F | - | - | Same |
| 7 | M365 Visio (x64) | P | P | F | - | - | Same |
| 8 | M365 Visio (x86) | P | P | F | - | - | Same |
| 9 | Obsidian (x64) | P | P | F | - | - | Per-user, /D= not working |
| 10 | pgAdmin 4 (x64) | P | P | F | - | - | Detection path (runtime subfolder) |
| 11 | Postman (x64) | P | P | F | - | - | Per-user Squirrel |
| 12 | PowerShell 7 LTS (x64) | P | P | F | - | - | ProductCode detection |

## Uninstall / Clean Failures (27 apps)

Install and detection pass. Uninstall runs but leaves traces (ARP entries, services, data).

| # | App | Stage | Install | Detect | Uninstall | Clean | Notes |
|---|-----|-------|---------|--------|-----------|-------|-------|
| 1 | Adobe Acrobat Reader DC (x64) | P | P | P | P | F | ARP traces |
| 2 | ASP.NET 8 Hosting Bundle (x64) | P | P | P | F | P | Uninstall exit 1618 (MSI busy) |
| 3 | Audacity (x64) | P | P | P | F | F | InnoSetup uninstall broken |
| 4 | Bitwarden Desktop (x64) | P | P | P | F | F | NSIS uninstall broken |
| 5 | DBeaver Community (x64) | P | P | P | P | F | NSIS uninstall leaves traces |
| 6 | GIMP (x64) | P | P | P | P | F | InnoSetup uninstall leaves traces |
| 7 | Git for Windows (x64) | P | P | P | P | F | Uninstall leaves traces |
| 8 | Malwarebytes (x64) | P | P | P | P | F | Service persists |
| 9 | Microsoft Edge WebView2 (x64) | P | P | P | P | F | Uninstall leaves traces |
| 10 | Mozilla Firefox (x64) | P | P | P | F | F | Uninstall exit 1605 |
| 11 | Mozilla Thunderbird (x64) | P | P | P | F | F | Uninstall exit 1605 |
| 12 | Notepad++ (x64) | P | P | P | P | F | NSIS uninstall leaves traces |
| 13 | Opera Browser (x64) | P | P | P | P | F | Uninstall leaves traces |
| 14 | Positron (x64) | P | P | P | P | F | Uninstall leaves traces |
| 15 | PostgreSQL 16 (x64) | P | P | P | P | F | Data dir persists |
| 16 | Power BI Desktop (x64) | P | P | P | P | F | Uninstall leaves traces |
| 17 | PowerToys (x64) | P | P | P | P | F | Uninstall leaves traces |
| 18 | R for Windows (x64) | P | P | P | P | F | InnoSetup uninstall leaves traces |
| 19 | RStudio Desktop (x64) | P | P | P | P | F | Uninstall leaves traces |
| 20 | ShareX (x64) | P | P | P | P | F | InnoSetup uninstall leaves traces |
| 21 | SoapUI Open Source | P | P | P | P | F | Uninstall leaves traces |
| 22 | SSMS 22 (x64) | P | P | P | P | F | WiX Burn complex uninstall |
| 23 | Tableau Desktop (x64) | P | P | P | P | F | WiX Burn uninstall |
| 24 | Tableau Prep Builder (x64) | P | P | P | P | F | Same |
| 25 | Tableau Reader (x64) | P | P | P | P | F | Same |
| 26 | TeamViewer Host (x64) | P | P | P | P | F | Service persists |
| 27 | Vim (x64) | P | P | P | P | F | InnoSetup uninstall |

## Timeout (4 apps)

| # | App | Stage | Install | Notes |
|---|-----|-------|---------|-------|
| 1 | PyCharm Community (x64) | P | T | NSIS hangs under SYSTEM |
| 2 | TreeSize Free | P | T | InnoSetup hangs under SYSTEM |
| 3 | Visual Studio Code (x64) | P | T | NSIS hangs under SYSTEM |
| 4 | TeamViewer (x64) | P | F | Exit 1603 - TeamViewer Host present |

## Stage Failures (2 apps)

| # | App | Notes |
|---|-----|-------|
| 1 | WinMerge (x64) | MSI inspection error |
| 2 | WizTree (x64) | MSI inspection error |

## Other Detection Issues (3 apps)

| # | App | Stage | Install | Detect | Notes |
|---|-----|-------|---------|--------|-------|
| 1 | Sysinternals Suite (x64) | P | P | F | ZIP, no ARP entry |
| 2 | XenCenter (x64) | P | P | F | Detection mismatch |
| 3 | Zoom Workplace (x64) | P | P | F | MSI detection mismatch |

## Excluded / Deferred (10 apps)

| # | App | Reason |
|---|-----|--------|
| 1 | .NET 10 Desktop Runtime (x64) | Test last (harness runtime dependency) |
| 2 | FileZilla Client (x64) | PUP/adware in installer |
| 3 | Foxit Reader (x64) | CDN throttling |
| 4 | Microsoft Teams (x64) | MSIX deployment type needed |
| 5 | Slack | MSIX deployment type needed |
| 6 | Visual Studio 2026 Community | Too large for lab VM |
| 7 | Visual Studio 2026 Enterprise | Too large for lab VM |
| 8 | VMware Tools (x64) | Can't install on Hyper-V |
| 9 | Visual C++ Redistributable (x64) | Shared components persist by design |
| 10 | Visual C++ Redistributable (x86) | Same |

---

## FULL PASS — Signed Off (42 apps)

All phases validated: Stage, Install, Detect, Uninstall, Clean.
Do NOT retest. Do NOT edit definitions without explicit approval.

| # | App | Install Time | Test Source |
|---|-----|-------------|------------|
| 1 | .NET 8 Desktop Runtime (x64) | 9s | Clean VM |
| 2 | .NET 8 Desktop Runtime (x86) | 6s | Clean VM |
| 3 | .NET 9 Desktop Runtime (x64) | 7s | Clean VM |
| 4 | 7-Zip (x64) | 13s | Dirty VM |
| 5 | Amazon Corretto JDK 8 (x64) | 8s | Both |
| 6 | Amazon Corretto JDK 8 (x86) | 5-8s | Both |
| 7 | Amazon Corretto JDK 11 (x64) | 7s | Both |
| 8 | Amazon Corretto JDK 11 (x86) | 4-6s | Both |
| 9 | Amazon Corretto JDK 17 (x64) | 7s | Both |
| 10 | Amazon Corretto JDK 21 (x64) | 7s | Both |
| 11 | Amazon Corretto JDK 25 (x64) | 7s | Both |
| 12 | Cisco Webex (x64) | 5s | Dirty VM |
| 13 | draw.io Desktop (x64) | 6s | Both |
| 14 | Eclipse Temurin JDK 8 (x64) | 5s | Both |
| 15 | Eclipse Temurin JDK 8 (x86) | 3s | Both |
| 16 | Eclipse Temurin JDK 11 (x64) | 7s | Both |
| 17 | Eclipse Temurin JDK 11 (x86) | 4s | Both |
| 18 | Eclipse Temurin JDK 17 (x64) | 6s | Both |
| 19 | Eclipse Temurin JDK 21 (x64) | 7s | Both |
| 20 | Eclipse Temurin JDK 25 (x64) | 6s | Both |
| 21 | Eclipse Temurin JRE 8 (x64) | 3s | Both |
| 22 | Eclipse Temurin JRE 8 (x86) | 3s | Both |
| 23 | Eclipse Temurin JRE 11 (x64) | 4s | Both |
| 24 | Eclipse Temurin JRE 11 (x86) | 3s | Both |
| 25 | Eclipse Temurin JRE 17 (x64) | 4s | Both |
| 26 | Eclipse Temurin JRE 21 (x64) | 4s | Both |
| 27 | Eclipse Temurin JRE 25 (x64) | 4s | Both |
| 28 | Everything (x64) | 4-10s | Both |
| 29 | Google Chrome (x64) | 14-19s | Clean VM |
| 30 | Inkscape (x64) | 47s | Both |
| 31 | KeePass | 6s | Both |
| 32 | LibreOffice (x64) | 33-57s | Both |
| 33 | Microsoft Edge (x64) | 2s | CanUninstall=false (by design) |
| 34 | Microsoft OLE DB 19 (x64) | 3s | Dirty VM |
| 35 | mRemoteNG (x64) | 8-16s | Both |
| 36 | Node.js LTS (x64) | 8-11s | Both |
| 37 | paint.net (x64) | 5s | Both |
| 38 | PowerShell 7 (x64) | 5s | Both |
| 39 | PuTTY (x64) | 3s | Both |
| 40 | TortoiseGit (x64) | 10s | Both |
| 41 | TortoiseSVN (x64) | 9s | Both |
| 42 | VLC Media Player (x64) | 5s | Both |
| 43 | WinDirStat (x64) | 3s | Dirty VM |
| 44 | XenServer VM Tools (x64) | 29s | Both |
