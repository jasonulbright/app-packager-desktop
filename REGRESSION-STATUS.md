# Packr Regression Test Status — 2026-04-05

**Lab:** CM01 + CLIENT01 (Hyper-V, CM 2509)
**Total definitions:** 117
**Test harness:** Remote (host-based PSRemoting with automated reboots) + Local (C# harness, SYSTEM via psexec)
**Clean VM:** CLIENT01 rebuilt 2026-04-05, prereqs: 7-Zip, MSVC x64/x86, .NET 8/9/10, PsExec
**Data sources:** TEST-RESULTS-FINAL-20260404.csv (dirty VM), TEST-RESULTS-CLEAN-VM-DEFINITIVE.csv (clean VM)
**Engine fixes (2026-04-05):** NSIS uninstall ARP matching by app name (not download filename), MSI uninstall via ProductCode from manifest, 7 definition framework/args corrections. CLIENT01 rebuilt with 150GB disk.

---

## Summary

| Status | Count |
|--------|-------|
| **FULL PASS** | 45 |
| **Install + Detect PASS, Uninstall/Clean needs work** | 32 |
| **Install PASS, Detect FAIL** | 16 |
| **Install FAIL** | 13 |
| **Timeout** | 3 |
| **Excluded/Deferred** | 8 |
| **Total** | **117** |

---

## FULL PASS — Signed Off (45 apps)

All phases validated: Stage, Install, Detect, Uninstall, Clean.
Do NOT retest. Do NOT edit definitions without explicit approval.

| # | App | Install Time | Test Source |
|---|-----|-------------|------------|
| 1 | .NET 8 Desktop Runtime (x64) | 9s | Clean VM |
| 2 | .NET 8 Desktop Runtime (x86) | 6s | Clean VM |
| 3 | .NET 9 Desktop Runtime (x64) | 7s | Clean VM |
| 4 | 7-Zip (x64) | 13s | Dirty VM |
| 5 | Amazon Corretto JDK 8 (x64) | 5s | Both |
| 6 | Amazon Corretto JDK 8 (x86) | 6s | Both |
| 7 | Amazon Corretto JDK 11 (x64) | 8s | Dirty VM |
| 8 | Amazon Corretto JDK 11 (x86) | 6s | Dirty VM |
| 9 | Amazon Corretto JDK 17 (x64) | 7s | Dirty VM |
| 10 | Amazon Corretto JDK 21 (x64) | 7s | Dirty VM |
| 11 | Amazon Corretto JDK 25 (x64) | 7s | Dirty VM |
| 12 | Cisco Webex (x64) | 7s | Clean VM |
| 13 | draw.io Desktop (x64) | 6s | Dirty VM |
| 14 | Eclipse Temurin JDK 8 (x64) | 5s | Both |
| 15 | Eclipse Temurin JDK 8 (x86) | 5s | Both |
| 16 | Eclipse Temurin JDK 11 (x64) | 8s | Both |
| 17 | Eclipse Temurin JDK 11 (x86) | 9s | Both |
| 18 | Eclipse Temurin JDK 17 (x64) | 6s | Both |
| 19 | Eclipse Temurin JDK 21 (x64) | 10s | Both |
| 20 | Eclipse Temurin JDK 25 (x64) | 3s | Both |
| 21 | Eclipse Temurin JRE 8 (x64) | 4s | Both |
| 22 | Eclipse Temurin JRE 8 (x86) | 3s | Both |
| 23 | Eclipse Temurin JRE 11 (x64) | 4s | Both |
| 24 | Eclipse Temurin JRE 11 (x86) | 4s | Both |
| 25 | Eclipse Temurin JRE 17 (x64) | 4s | Both |
| 26 | Eclipse Temurin JRE 21 (x64) | 4s | Both |
| 27 | Eclipse Temurin JRE 25 (x64) | 4s | Both |
| 28 | Everything (x64) | 4s | Dirty VM |
| 29 | Google Chrome (x64) | 14s | Both |
| 30 | Inkscape (x64) | 47s | Dirty VM |
| 31 | KeePass | 6s | Dirty VM |
| 32 | LibreOffice (x64) | 33s | Dirty VM |
| 33 | M365 Project (x64) | 90s | Clean VM |
| 34 | Microsoft Edge (x64) | 2s | Dirty VM (CanUninstall=false by design) |
| 35 | mRemoteNG (x64) | 16s | Dirty VM |
| 36 | Node.js LTS (x64) | 6s | Both |
| 37 | paint.net (x64) | 8s | Both |
| 38 | PowerShell 7 (x64) | 17s | Dirty VM |
| 39 | PuTTY (x64) | 3s | Dirty VM |
| 40 | TeamViewer (x64) | 27s | Clean VM |
| 41 | TortoiseGit (x64) | 10s | Dirty VM |
| 42 | TortoiseSVN (x64) | 9s | Dirty VM |
| 43 | VLC Media Player (x64) | 5s | Dirty VM |
| 44 | WinDirStat (x64) | 3s | Dirty VM |
| 45 | XenServer VM Tools (x64) | 6s | Both (reboot required) |

---

## Install Failures (13 apps)

| # | App | Exit Code | Notes |
|---|-----|-----------|-------|
| 1 | Anaconda Distribution (x64) | 2 | Path conflict |
| 2 | AnyDesk | 11341829 | Needs interactive session |
| 3 | Brave Browser (x64) | -1 | Squirrel hangs under SYSTEM |
| 4 | Citrix Workspace (CR) (x64) | 40032 | Missing prereqs (.NET 8 + MSVC present but still fails) |
| 5 | CutePDF Writer | 1 | InnoSetup silent args |
| 6 | Greenshot (x64) | 5 | InnoSetup access denied |
| 7 | Microsoft ODBC 18 (x64) | 1603 | Pending reboot or MSI conflict |
| 8 | Microsoft OLE DB 19 (x64) | 1603 | Pending reboot or MSI conflict (fails in all CSV runs) |
| 9 | PostgreSQL 13 (x64) | 1 | Port conflict with PG16 |
| 10 | PostgreSQL 14 (x64) | 1 | Port conflict |
| 11 | PostgreSQL 15 (x64) | 1 | Port conflict |
| 12 | PostgreSQL 17 (x64) | 1 | Port conflict |
| 13 | Tableau Prep Builder (x64) | 1603 | Clean VM fails; dirty VM passes (likely needs Tableau shared components) |

---

## Detection Failures (16 apps)

Install succeeds but detection rule does not match.

| # | App | Notes |
|---|-----|-------|
| 1 | CCleaner | Detection rule mismatch (dirty VM: install failed exit 111111; clean VM: install pass, detect fail) |
| 2 | CPU-Z | Detection path issue |
| 3 | M365 Apps for Enterprise (x64) | ODT setup.exe didn't run in harness |
| 4 | M365 Apps for Enterprise (x86) | Same |
| 5 | M365 Project (x86) | Same (x64 variant is FULL PASS on clean VM) |
| 6 | M365 Visio (x64) | Same |
| 7 | M365 Visio (x86) | Same |
| 8 | Obsidian (x64) | Per-user NSIS, /D= not working |
| 9 | pgAdmin 4 (x64) | Detection path (runtime subfolder) |
| 10 | Postman (x64) | Per-user Squirrel |
| 11 | PowerShell 7 LTS (x64) | ProductCode detection mismatch |
| 12 | Sysinternals Suite (x64) | ZIP app, no ARP entry |
| 13 | WinMerge (x64) | Detection mismatch (stage failure was fixed) |
| 14 | WizTree (x64) | Detection mismatch (stage failure was fixed) |
| 15 | XenCenter (x64) | Detection mismatch |
| 16 | Zoom Workplace (x64) | MSI detection mismatch |

---

## Uninstall / Clean Failures (32 apps)

Install and detection pass. Uninstall runs but leaves traces (ARP entries, services, data).
Where dirty/clean VM results differ, both are noted.

| # | App | Uninstall (Dirty) | Uninstall (Clean) | Clean | Notes |
|---|-----|-------------------|-------------------|-------|-------|
| 1 | Adobe Acrobat Reader DC (x64) | PASS | PASS | FAIL | ARP traces persist |
| 2 | ASP.NET 8 Hosting Bundle (x64) | PASS | FAIL (1618) | Mixed | MSI busy on clean VM; clean FAIL on dirty, PASS on clean |
| 3 | Audacity (x64) | PASS | FAIL (exit 1) | FAIL | InnoSetup uninstall broken on clean VM |
| 4 | Bitwarden Desktop (x64) | PASS | FAIL (exit 1) | FAIL | NSIS uninstall broken on clean VM |
| 5 | DBeaver Community (x64) | PASS | WinRM error | FAIL | Dirty VM only |
| 6 | GIMP (x64) | PASS | WinRM error | FAIL | Dirty VM only |
| 7 | Git for Windows (x64) | PASS | FAIL (exit 1) | FAIL | InnoSetup uninstall leaves traces |
| 8 | Malwarebytes (x64) | PASS | WinRM error | FAIL | Service persists |
| 9 | Microsoft Edge WebView2 (x64) | PASS | WinRM error | FAIL | Traces persist |
| 10 | Mozilla Firefox (x64) | FAIL (1605) | WinRM error | FAIL | Uninstall exit 1605 both runs |
| 11 | Mozilla Thunderbird (x64) | FAIL (1605) | FAIL (1605) | FAIL | Uninstall exit 1605 both runs |
| 12 | Notepad++ (x64) | PASS | FAIL (exit 1) | FAIL | NSIS uninstall leaves traces |
| 13 | Opera Browser (x64) | PASS | FAIL (exit 1) | FAIL | Traces persist |
| 14 | Positron (x64) | PASS | FAIL (exit 1) | FAIL | Traces persist |
| 15 | PostgreSQL 16 (x64) | PASS | WinRM error | FAIL | Data dir persists |
| 16 | Power BI Desktop (x64) | PASS | WinRM error | FAIL | Traces persist |
| 17 | PowerToys (x64) | PASS | WinRM error | FAIL | Traces persist |
| 18 | Python (x64) | PASS | FAIL (exit 1) | FAIL | Was incorrectly listed as Install FAIL; install works fine |
| 19 | R for Windows (x64) | PASS | FAIL (exit 1) | FAIL | InnoSetup uninstall leaves traces |
| 20 | RStudio Desktop (x64) | PASS | WinRM error | FAIL | Traces persist |
| 21 | ShareX (x64) | PASS | WinRM error | FAIL | InnoSetup uninstall leaves traces |
| 22 | SoapUI Open Source | PASS | WinRM error | FAIL | Traces persist |
| 23 | SSMS 22 (x64) | PASS | WinRM error | FAIL | WiX Burn complex uninstall |
| 24 | Tableau Desktop (x64) | PASS | WinRM error | FAIL | Traces persist |
| 25 | Tableau Reader (x64) | PASS | FAIL (exit 1) | FAIL | Traces persist |
| 26 | TeamViewer Host (x64) | PASS | FAIL (exit 1) | FAIL | Service persists |
| 27 | Vim (x64) | PASS | WinRM error | FAIL | InnoSetup uninstall leaves traces |
| 28 | Visual C++ Redistributable (x64) | PASS | PASS | FAIL | Shared components persist by design |
| 29 | Visual C++ Redistributable (x86) | PASS | PASS | FAIL | Shared components persist by design |
| 30 | WinRAR (x64) | PASS | WinRM error | FAIL | Traces persist |
| 31 | WinSCP (x64) | PASS | WinRM error | FAIL | InnoSetup uninstall leaves traces |
| 32 | Wireshark (x64) | PASS | WinRM error | FAIL | Traces persist |

---

## Timeout (3 apps)

| # | App | Notes |
|---|-----|-------|
| 1 | PyCharm Community (x64) | NSIS hangs under SYSTEM |
| 2 | TreeSize Free | InnoSetup hangs under SYSTEM |
| 3 | Visual Studio Code (x64) | NSIS hangs under SYSTEM |

---

## Excluded / Deferred (8 apps)

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

---

## Changes from previous version (reconciliation notes)

Reconciled against all 5 CSV data files on 2026-04-05. Previous version was interrupted by rate limiting and had stale/incorrect categorizations.

| Change | Detail |
|--------|--------|
| M365 Project (x64) | Detect FAIL -> **FULL PASS** (DEFINITIVE clean VM: all 4 phases pass) |
| TeamViewer (x64) | Timeout -> **FULL PASS** (DEFINITIVE clean VM: all 4 phases pass; only failed on dirty VM when Host was present) |
| Python (x64) | Install FAIL -> **Uninstall/Clean** (install and detect pass in both runs; exit 1603 was wrong) |
| Microsoft OLE DB 19 (x64) | FULL PASS -> **Install FAIL** (exit 1603 in all CSV runs; no passing data exists) |
| WinMerge (x64) | Stage FAIL -> **Detect FAIL** (stage was fixed; installs but detection mismatch) |
| WizTree (x64) | Stage FAIL -> **Detect FAIL** (stage was fixed; installs but detection mismatch) |
| Tableau Prep Builder (x64) | Uninstall/Clean -> **Install FAIL** (clean VM: exit 1603; dirty VM with Tableau Desktop present: passes) |
| VC++ Redist (x64/x86) | Excluded -> **Uninstall/Clean** (real test data exists; clean fail is by design) |
| Summary counts | Fixed: totals now add to 117; "Other Detection Issues" section merged into Detection Failures |
| FULL PASS count | 42 -> **45** (was also miscounted; table had 44 rows but header said 42) |
| Dirty vs Clean VM | Added Uninstall (Dirty) and Uninstall (Clean) columns to show where results diverge |
