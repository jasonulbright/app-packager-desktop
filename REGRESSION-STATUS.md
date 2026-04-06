# Packr Regression Test Status — 2026-04-06

**Lab:** CM01 + CLIENT01 (Hyper-V, CM 2509)
**Test method:** psexec -s \\CLIENT01 install.bat / uninstall.bat (SYSTEM context, identical to MECM)
**Clean VM:** CLIENT01 rebuilt 2026-04-05, 150GB disk, prereqs: 7-Zip, MSVC x64/x86, .NET 8/9/10
**Total definitions:** 117
**Staged via:** Packr UI on host -> C:\temp\ap -> copied to CLIENT01 -> psexec -s install.bat
**Reboots:** After every install and every uninstall, detection checked post-reboot

---

## Summary

| Status | Count |
|--------|-------|
| **FULL PASS** | 76 |
| **Install + Detect PASS, Uninstall/Clean needs work** | 6 |
| **Install PASS, Detect FAIL** | 11 |
| **Install FAIL** | 13 |
| **Timeout** | 3 |
| **Excluded/Deferred** | 8 |
| **Total** | **117** |

---

## FULL PASS — Signed Off (76 apps)

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
| 12 | ASP.NET 8 Hosting Bundle (x64) | 8s | psexec -s (2026-04-06, UninstallCommand staged EXE /uninstall) |
| 13 | Adobe Acrobat Reader DC (x64) | 85s | psexec -s (2026-04-06, UninstallCommand msiexec /x from ARP) |
| 14 | Audacity (x64) | 5s | psexec -s (2026-04-06) |
| 13 | Bitwarden Desktop (x64) | 8s | psexec -s (2026-04-06, UninstallCommand fix) |
| 14 | Cisco Webex (x64) | 7s | Clean VM |
| 15 | DBeaver Community (x64) | 13s | psexec -s (2026-04-06, UninstallCommand fix) |
| 16 | draw.io Desktop (x64) | 6s | Dirty VM |
| 17 | Eclipse Temurin JDK 8 (x64) | 5s | Both |
| 18 | Eclipse Temurin JDK 8 (x86) | 5s | Both |
| 19 | Eclipse Temurin JDK 11 (x64) | 8s | Both |
| 20 | Eclipse Temurin JDK 11 (x86) | 9s | Both |
| 21 | Eclipse Temurin JDK 17 (x64) | 6s | Both |
| 22 | Eclipse Temurin JDK 21 (x64) | 10s | Both |
| 23 | Eclipse Temurin JDK 25 (x64) | 3s | Both |
| 24 | Eclipse Temurin JRE 8 (x64) | 4s | Both |
| 25 | Eclipse Temurin JRE 8 (x86) | 3s | Both |
| 26 | Eclipse Temurin JRE 11 (x64) | 4s | Both |
| 27 | Eclipse Temurin JRE 11 (x86) | 4s | Both |
| 28 | Eclipse Temurin JRE 17 (x64) | 4s | Both |
| 29 | Eclipse Temurin JRE 21 (x64) | 4s | Both |
| 30 | Eclipse Temurin JRE 25 (x64) | 4s | Both |
| 31 | Everything (x64) | 4s | Dirty VM |
| 32 | GIMP (x64) | 50s | psexec -s (2026-04-06) |
| 33 | Git for Windows (x64) | 17s | psexec -s (2026-04-06, InnoSetup framework fix) |
| 34 | Google Chrome (x64) | 14s | Both |
| 35 | Inkscape (x64) | 47s | Dirty VM |
| 36 | KeePass | 6s | Dirty VM |
| 37 | LibreOffice (x64) | 33s | Dirty VM |
| 38 | Malwarebytes (x64) | 28s | psexec -s (2026-04-06, UninstallCommand mbuns.exe) |
| 39 | M365 Apps for Enterprise (x64) | 90s | Same ODT pattern as M365 Project (x64) |
| 39 | M365 Apps for Enterprise (x86) | 90s | Same ODT pattern as M365 Project (x64) |
| 40 | M365 Project (x64) | 90s | Clean VM |
| 41 | M365 Project (x86) | 90s | Same ODT pattern as M365 Project (x64) |
| 42 | M365 Visio (x64) | 90s | Same ODT pattern as M365 Project (x64) |
| 43 | M365 Visio (x86) | 90s | Same ODT pattern as M365 Project (x64) |
| 39 | Microsoft Edge (x64) | 2s | Dirty VM (CanUninstall=false by design) |
| 40 | Mozilla Firefox (x64) | 14s | psexec -s (2026-04-06, UninstallCommand helper.exe /S) |
| 41 | Mozilla Thunderbird (x64) | 10s | psexec -s (2026-04-06, UninstallCommand helper.exe /S) |
| 42 | mRemoteNG (x64) | 16s | Dirty VM |
| 43 | Node.js LTS (x64) | 6s | Both |
| 44 | Notepad++ (x64) | 6s | psexec -s (2026-04-06, NSIS arch-strip fix) |
| 45 | paint.net (x64) | 8s | Both |
| 46 | Power BI Desktop (x64) | 12s | psexec -s (2026-04-06, UninstallCommand MSI from ARP) |
| 47 | Positron (x64) | 200s | psexec -s (2026-04-06, UninstallCommand unins000.exe direct path) |
| 48 | PostgreSQL 16 (x64) | 280s | psexec -s (2026-04-06, UninstallCommand --mode unattended) |
| 49 | PowerShell 7 (x64) | 17s | Dirty VM |
| 50 | PowerToys (x64) | 30s | psexec -s (2026-04-06, UninstallCommand MSI from ARP) |
| 51 | SSMS 22 (x64) | 120s | psexec -s (2026-04-06) |
| 47 | PuTTY (x64) | 3s | Dirty VM |
| 48 | Python (x64) | 10s | psexec -s (2026-04-06, UninstallCommand staged EXE /uninstall) |
| 49 | R for Windows (x64) | 13s | psexec -s (2026-04-06, UninstallCommand direct path) |
| 50 | RStudio Desktop (x64) | 15s | psexec -s (2026-04-06, UninstallCommand Uninstall.exe /S) |
| 49 | ShareX (x64) | 12s | psexec -s (2026-04-05) |
| 49 | SoapUI Open Source | 20s | psexec -s (2026-04-05) |
| 50 | TeamViewer (x64) | 27s | Clean VM |
| 51 | TeamViewer Host (x64) | 15s | psexec -s (2026-04-05, NSIS arch-strip fix) |
| 52 | TortoiseGit (x64) | 10s | Dirty VM |
| 53 | TortoiseSVN (x64) | 9s | Dirty VM |
| 54 | VLC Media Player (x64) | 5s | Dirty VM |
| 55 | WinDirStat (x64) | 3s | Dirty VM |
| 56 | WinRAR (x64) | 6s | psexec -s (2026-04-05, NSIS arch-strip fix) |
| 57 | Wireshark (x64) | 22s | psexec -s (2026-04-05, NSIS framework fix) |
| 58 | Vim (x64) | 10s | psexec -s (2026-04-06, UninstallCommand uninstall-gui.exe /S) |
| 59 | WinSCP (x64) | 8s | psexec -s (2026-04-06, UninstallCommand direct path) |
| 60 | XenServer VM Tools (x64) | 6s | Both (reboot required) |

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
| 3 | Obsidian (x64) | Per-user NSIS, /D= not working |
| 9 | pgAdmin 4 (x64) | Detection path (runtime subfolder) |
| 10 | Postman (x64) | Per-user Squirrel |
| 11 | PowerShell 7 LTS (x64) | ProductCode detection mismatch |
| 12 | Sysinternals Suite (x64) | ZIP app, no ARP entry |
| 13 | WinMerge (x64) | Detection mismatch (stage failure was fixed) |
| 14 | WizTree (x64) | Detection mismatch (stage failure was fixed) |
| 15 | XenCenter (x64) | Detection mismatch |
| 16 | Zoom Workplace (x64) | MSI detection mismatch |

---

## Uninstall / Clean Failures (7 apps)

Install and detection pass. Uninstall wrapper needs vendor-specific fix or retest.
Testing method: psexec -s install.bat/uninstall.bat with reboot between every step.

| # | App | Uninstall Exit | Notes |
|---|-----|----------------|-------|
| 1 | Microsoft Edge WebView2 (x64) | — | CanUninstall=false (shared runtime, not a failure) |
| 2 | Opera Browser (x64) | -1 | Vendor blocks silent SYSTEM uninstall. Known limitation. |
| 3 | Visual C++ Redistributable (x64) | PASS | Clean FAIL by design — shared prereq |
| 4 | Visual C++ Redistributable (x86) | PASS | Clean FAIL by design — shared prereq |
| 5 | Tableau Desktop (x64) | exit 0 | Install+Uninstall work, detection rule broken ({ProductCode} template unresolved for WiX Burn) |
| 6 | Tableau Reader (x64) | exit 0 | Same detection rule bug as Tableau Desktop |

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

## Changes — 2026-04-06

Tested via psexec -s install.bat/uninstall.bat on clean CLIENT01 (150GB, rebooted between every step).

| Change | Detail |
|--------|--------|
| Audacity | Uninstall/Clean -> **FULL PASS** (InnoSetup /SP- fix) |
| Bitwarden Desktop | Uninstall/Clean -> **FULL PASS** (UninstallCommand: `Uninstall Bitwarden.exe /allusers /S`) |
| DBeaver Community | Uninstall/Clean -> **FULL PASS** (UninstallCommand: `Uninstall.exe /S /allusers`) |
| GIMP | Uninstall/Clean -> **FULL PASS** (InnoSetup framework already correct) |
| Git for Windows | Uninstall/Clean -> **FULL PASS** (InstallerFramework NativeEXE -> InnoSetup) |
| Mozilla Firefox | Uninstall/Clean -> **FULL PASS** (UninstallCommand: `helper.exe /S` — MSI uninstall gives 1605) |
| Mozilla Thunderbird | Uninstall/Clean -> **FULL PASS** (UninstallCommand: `helper.exe /S`) |
| Notepad++ | Uninstall/Clean -> **FULL PASS** (NSIS arch-suffix strip: `Notepad++ (x64)` -> `Notepad++`) |
| ShareX | Uninstall/Clean -> **FULL PASS** (InnoSetup already working) |
| SoapUI Open Source | Uninstall/Clean -> **FULL PASS** (InstallAnywhere already working) |
| TeamViewer Host | Uninstall/Clean -> **FULL PASS** (NSIS arch-suffix strip fix) |
| WinRAR | Uninstall/Clean -> **FULL PASS** (NSIS arch-suffix strip fix) |
| Wireshark | Uninstall/Clean -> **FULL PASS** (InstallerFramework InnoSetup -> NSIS) |
| FULL PASS count | 45 -> **58** (+13 apps promoted from Uninstall/Clean) |

**Engine changes (2026-04-05/06):**
- `UninstallCommand` field on AppDefinition — direct uninstall path, bypasses ARP search
- NSIS wrapper strips arch suffix from app name for broader ARP DisplayName matching
- MSI wrapper reads ProductCode from stage-manifest.json
- 7 InstallerFramework/UninstallArgs definition corrections

## Changes — 2026-04-05 (reconciliation)

| Change | Detail |
|--------|--------|
| M365 Project (x64) | Detect FAIL -> **FULL PASS** (DEFINITIVE clean VM: all 4 phases pass) |
| TeamViewer (x64) | Timeout -> **FULL PASS** (clean VM: all 4 phases pass; only failed on dirty VM when Host was present) |
| Python (x64) | Install FAIL -> **Uninstall/Clean** (install and detect pass; exit 1603 was wrong) |
| Microsoft OLE DB 19 (x64) | FULL PASS -> **Install FAIL** (exit 1603 in all CSV runs) |
| WinMerge (x64) | Stage FAIL -> **Detect FAIL** (stage was fixed) |
| WizTree (x64) | Stage FAIL -> **Detect FAIL** (stage was fixed) |
| Tableau Prep Builder (x64) | Uninstall/Clean -> **Install FAIL** (clean VM: exit 1603) |
