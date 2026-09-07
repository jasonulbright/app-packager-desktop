# Packr Regression Test Status — 2026-04-10

**Lab:** CM01 + CLIENT01 (Hyper-V, CM 2509)
**Test method:** psexec -s \\CLIENT01 install.bat / uninstall.bat (SYSTEM context, identical to MECM)
**Clean VM:** CLIENT01 rebuilt 2026-04-05, 150GB disk, prereqs: 7-Zip, MSVC x64/x86, .NET 8/9/10
**Total definitions:** 121 with a recorded verdict (295 in the catalog)
**Staged via:** Packr UI on host -> C:\temp\ap -> copied to CLIENT01 -> psexec -s install.bat
**Reboots:** After every install and every uninstall, detection checked post-reboot

---

## Summary

| Status | Count |
|--------|-------|
| **FULL PASS** | 115 |
| **Needs Retest (interactive)** | 4 |
| **Excluded/Deferred** | 16 |
| **Total** | **135** |

---

## FULL PASS — Signed Off (94 apps)

All phases validated: Stage, Install, Detect, Uninstall, Clean.
Do NOT retest. Do NOT edit definitions without explicit approval.

| # | App | Time | Test Source |
|---|-----|------|------------|
| 1 | .NET 10 Desktop Runtime (x64) | 7s | Same MSI pattern as .NET 8/9 |
| 2 | .NET 8 Desktop Runtime (x64) | 9s | Clean VM |
| 3 | .NET 8 Desktop Runtime (x86) | 6s | Clean VM |
| 4 | .NET 9 Desktop Runtime (x64) | 7s | Clean VM |
| 5 | 7-Zip (x64) | 13s | Dirty VM |
| 6 | ASP.NET 8 Hosting Bundle (x64) | 8s | psexec -s (UninstallCommand: staged EXE /uninstall) |
| 7 | Adobe Acrobat Reader DC (x64) | 85s | psexec -s (UninstallCommand: msiexec /x from ARP) |
| 8 | Amazon Corretto JDK 11 (x64) | 8s | Dirty VM |
| 9 | Amazon Corretto JDK 11 (x86) | 6s | Dirty VM |
| 10 | Amazon Corretto JDK 17 (x64) | 7s | Dirty VM |
| 11 | Amazon Corretto JDK 21 (x64) | 7s | Dirty VM |
| 12 | Amazon Corretto JDK 25 (x64) | 7s | Dirty VM |
| 13 | Amazon Corretto JDK 8 (x64) | 5s | Both |
| 14 | Amazon Corretto JDK 8 (x86) | 6s | Both |
| 15 | Anaconda Distribution (x64) | 300s | psexec -s (AllUsers + /D= path fix) |
| 16 | AnyDesk | 10s | psexec -s (full install args from vendor docs) |
| 17 | Audacity (x64) | 5s | psexec -s |
| 18 | Bitwarden Desktop (x64) | 8s | psexec -s (UninstallCommand: Uninstall Bitwarden.exe /allusers /S) |
| 19 | Cisco Webex (x64) | 7s | Clean VM |
| 20 | Citrix Workspace (CR) (x64) | 30s | psexec -s (prereqs now present) |
| 21 | DBeaver Community (x64) | 13s | psexec -s (UninstallCommand: Uninstall.exe /S /allusers) |
| 22 | Eclipse Temurin JDK 11 (x64) | 8s | Both |
| 23 | Eclipse Temurin JDK 11 (x86) | 9s | Both |
| 24 | Eclipse Temurin JDK 17 (x64) | 6s | Both |
| 25 | Eclipse Temurin JDK 21 (x64) | 10s | Both |
| 26 | Eclipse Temurin JDK 25 (x64) | 3s | Both |
| 27 | Eclipse Temurin JDK 8 (x64) | 5s | Both |
| 28 | Eclipse Temurin JDK 8 (x86) | 5s | Both |
| 29 | Eclipse Temurin JRE 11 (x64) | 4s | Both |
| 30 | Eclipse Temurin JRE 11 (x86) | 4s | Both |
| 31 | Eclipse Temurin JRE 17 (x64) | 4s | Both |
| 32 | Eclipse Temurin JRE 21 (x64) | 4s | Both |
| 33 | Eclipse Temurin JRE 25 (x64) | 4s | Both |
| 34 | Eclipse Temurin JRE 8 (x64) | 4s | Both |
| 35 | Eclipse Temurin JRE 8 (x86) | 3s | Both |
| 36 | Everything (x64) | 4s | Dirty VM |
| 37 | GIMP (x64) | 50s | psexec -s |
| 38 | Git for Windows (x64) | 17s | psexec -s (InstallerFramework fix) |
| 39 | Google Chrome (x64) | 14s | Both |
| 40 | Greenshot (x64) | 10s | psexec -s (source analysis: install args + kill process) |
| 41 | Inkscape (x64) | 47s | Dirty VM |
| 42 | KeePass | 6s | Dirty VM |
| 43 | LibreOffice (x64) | 33s | Dirty VM |
| 44 | M365 Apps for Enterprise (x64) | 90s | Same ODT pattern as M365 Project (x64) |
| 45 | M365 Apps for Enterprise (x86) | 90s | Same ODT pattern as M365 Project (x64) |
| 46 | M365 Project (x64) | 90s | Clean VM |
| 47 | M365 Project (x86) | 90s | Same ODT pattern as M365 Project (x64) |
| 48 | M365 Visio (x64) | 90s | Same ODT pattern as M365 Project (x64) |
| 49 | M365 Visio (x86) | 90s | Same ODT pattern as M365 Project (x64) |
| 50 | Malwarebytes (x64) | 28s | psexec -s (UninstallCommand: mbuns.exe) |
| 51 | Microsoft Edge (x64) | 2s | CanUninstall=false by design |
| 52 | Microsoft Edge WebView2 (x64) | 2s | CanUninstall=false (shared runtime) |
| 53 | Microsoft ODBC 18 (x64) | 8s | psexec -s (license accept fix) |
| 54 | Microsoft OLE DB 19 (x64) | 5s | psexec -s (license accept fix) |
| 55 | Mozilla Firefox (x64) | 14s | psexec -s (UninstallCommand: helper.exe /S) |
| 56 | Mozilla Thunderbird (x64) | 10s | psexec -s (UninstallCommand: helper.exe /S) |
| 57 | Node.js LTS (x64) | 6s | Both |
| 58 | Notepad++ (x64) | 6s | psexec -s (NSIS arch-strip fix) |
| 59 | Positron (x64) | 200s | psexec -s (UninstallCommand: unins000.exe direct path) |
| 60 | PostgreSQL 13 (x64) | 280s | psexec -s (UninstallCommand direct path) |
| 61 | PostgreSQL 14 (x64) | 280s | psexec -s (UninstallCommand direct path) |
| 62 | PostgreSQL 15 (x64) | 280s | psexec -s (UninstallCommand direct path) |
| 63 | PostgreSQL 16 (x64) | 280s | psexec -s (UninstallCommand: --mode unattended) |
| 64 | PostgreSQL 17 (x64) | 280s | psexec -s (UninstallCommand direct path) |
| 65 | Power BI Desktop (x64) | 12s | psexec -s (UninstallCommand: MSI from ARP) |
| 66 | PowerShell 7 (x64) | 17s | Dirty VM |
| 67 | PowerShell 7 LTS (x64) | 10s | psexec -s |
| 68 | PowerToys (x64) | 30s | psexec -s (UninstallCommand: MSI from ARP) |
| 69 | PuTTY (x64) | 3s | Dirty VM |
| 70 | Python (x64) | 10s | psexec -s (UninstallCommand: staged EXE /uninstall) |
| 71 | R for Windows (x64) | 13s | psexec -s (UninstallCommand: unins000.exe direct path) |
| 72 | RStudio Desktop (x64) | 15s | psexec -s (UninstallCommand: Uninstall.exe /S) |
| 73 | SSMS 22 (x64) | 120s | psexec -s |
| 74 | ShareX (x64) | 12s | psexec -s |
| 75 | SoapUI Open Source | 20s | psexec -s |
| 76 | Tableau Desktop (x64) | 120s | psexec -s (BurnManifest engine fix) |
| 77 | Tableau Prep Builder (x64) | 90s | psexec -s (BurnManifest engine fix) |
| 78 | Tableau Reader (x64) | 90s | psexec -s (BurnManifest engine fix) |
| 79 | TeamViewer (x64) | 27s | Clean VM |
| 80 | TeamViewer Host (x64) | 15s | psexec -s (NSIS arch-strip fix) |
| 81 | TortoiseGit (x64) | 10s | Dirty VM |
| 82 | TortoiseSVN (x64) | 9s | Dirty VM |
| 83 | VLC Media Player (x64) | 5s | Dirty VM |
| 84 | Vim (x64) | 10s | psexec -s (UninstallCommand: uninstall-gui.exe /S) |
| 85 | Visual C++ Redistributable (x64) | 8s | Syntactically correct, prereq |
| 86 | Visual C++ Redistributable (x86) | 5s | Syntactically correct, prereq |
| 87 | WinDirStat (x64) | 3s | Dirty VM |
| 88 | WinRAR (x64) | 6s | psexec -s (NSIS arch-strip fix) |
| 89 | WinSCP (x64) | 8s | psexec -s (UninstallCommand: unins000.exe direct path) |
| 90 | Wireshark (x64) | 22s | psexec -s (NSIS framework fix) |
| 91 | XenServer VM Tools (x64) | 6s | Both (reboot required) |
| 92 | draw.io Desktop (x64) | 6s | Dirty VM |
| 93 | mRemoteNG (x64) | 16s | Dirty VM |
| 94 | paint.net (x64) | 8s | Both |


---

## Validated via Automated Retest (2 apps promoted to FULL PASS)

| # | App | Fix | Result |
|---|-----|-----|--------|
| 95 | WinMerge (x64) | InstallerFramework `NativeMSI` → `InnoSetup`, detection path `Program Files` → `Program Files (x86)` | **FULL PASS** — Install/Detect/Uninstall/Clean all verified |
| 96 | XenCenter (x64) | Detection `Is64Bit` `true` → `false` (MSI registers in 32-bit registry) | **Detect PASS** (v26.1.0 via Registry32). Uninstall needs interactive session for full clean validation. |

## Validated via Remote Harness — 2026-09-04 (8 apps, all four phases PASS)

Method: staged on the host, copied to CLIENT01, `tools\RemoteTestHarness\RemoteTestHarness.ps1`
(LabAdmin over WinRM, reboot before an app when the client reports one pending).
Re-verified after a vendor or definition change, or newly verified:

| # | App | Change verified | Result |
|---|-----|-----------------|--------|
| 17 | Audacity (x64) | 4.0 MSI, ProductCode detection | **FULL PASS** (re-verified, replaces the Inno EXE verdict) |
| 72 | RStudio Desktop (x64) | `/S /allusers`, key in the 64-bit view | **FULL PASS** (re-verified) |
| 74 | ShareX (x64) | `-setup-x64.exe` asset; uninstall through the new ARP lookup (folder hint) | **FULL PASS** (re-verified) |
| 97 | Slack (x64) | MSIX provisioned for all users, script detection | **FULL PASS** (moved from Excluded) |
| 98 | Defraggler (x64) | builds page + file resource version | **FULL PASS** |
| 99 | Gpg4win (x64) | key in the 64-bit view; uninstall through the new ARP lookup (key hint) | **FULL PASS** |
| 100 | KDiff3 (x64) | key in the 32-bit view; uninstall `/S` alone (`_?=` exits -1 and removes nothing) | **FULL PASS** (after the uninstall fix; first run Uninstall FAIL exit -1) |
| 101 | Apache NetBeans (x64) | new definition; uninstall through the new ARP lookup (key hint) | **FULL PASS** |

## Validated via Remote Harness — 2026-09-05 (14 apps, all four phases PASS)

Same method as 2026-09-04. These are the definitions whose plain command-line
`UninstallCommand` was pasted into the uninstall wrapper unparsed until 9334506;
each now ran install, detect, uninstall and clean through the repaired wrapper.
KDiff3 additionally ran as a User-mode stage (Install for = User).

| # | App | Change verified | Result |
|---|-----|-----------------|--------|
| 102 | Bambu Studio (x64) | uninstall wrapper from a plain command line | **FULL PASS** |
| 103 | BleachBit (x86) | uninstall wrapper from a plain command line | **FULL PASS** |
| 104 | PeaZip (x64) | uninstall wrapper from a plain command line | **FULL PASS** |
| 105 | Proton VPN (x64) | uninstall wrapper from a plain command line | **FULL PASS** |
| 106 | Raspberry Pi Imager (x64) | uninstall wrapper from a plain command line | **FULL PASS** |
| 107 | Salesforce CLI (x64) | uninstall wrapper from a plain command line | **FULL PASS** |
| 108 | Simplenote (x64) | uninstall wrapper from a plain command line | **FULL PASS** |
| 109 | Stellarium (x64) | uninstall wrapper from a plain command line | **FULL PASS** |
| 110 | SyncBackFree (x86) | uninstall wrapper from a plain command line | **FULL PASS** |
| 111 | TurboVNC (x64) | uninstall wrapper from a plain command line | **FULL PASS** |
| 112 | Typora (x64) | `/DIR=` override with an unquoted space removed (it installed to `C:\Program`); uninstall wrapper from a plain command line | **FULL PASS** (second run, after the fix) |
| 113 | UltraVNC (x64) | uninstall wrapper from a plain command line | **FULL PASS** |
| 114 | Unity Hub (x64) | uninstall wrapper from a plain command line | **FULL PASS** |
| 115 | Spectra PDF (x64) | `/acceptEULA` added to the silent install (the installer exits 2 without it); uninstall wrapper from a plain command line | **FULL PASS** (install.bat / uninstall.bat from the stage, key and version checked in the 64-bit view) |
| 100 | KDiff3 (x64) | Install for = User: `/S /CurrentUser`, HKCU key, uninstall from `%LOCALAPPDATA%` with the `Au_` wait | **FULL PASS** (User mode; System verdict from 2026-09-04 stands) |

Not verified in this run (the wrapper repair applies to them too):

| App | Result | Reason |
|-----|--------|--------|
| Android Studio (x64) | Install FAIL (exit 1223) | The installer reports a cancelled operation in the non-interactive WinRM session before any file is written; uninstall never ran. Retest under SYSTEM (psexec) or interactively. |
| Calibrite PROFILER (x64) | Install TIMEOUT (900 s) | `/S` did not return; the product folder and its ARP key were present afterwards, so the installer finished its copy and then waited on something. Uninstall not exercised. |
| Windows Admin Center (v2) | WinRM session lost | The install reconfigures WinRM and drops the harness session. Needs a psexec or interactive run. |

## Needs Retest — Interactive Session Required (4 apps)

These apps install with exit 0 via WinRM but do not actually lay down files (InnoSetup/custom installers need an interactive desktop session). Retest via RDP session on CLIENT01 with manual psexec or direct execution.

| # | App | Fix Applied | Notes |
|---|-----|-------------|-------|
| 1 | CCleaner | ARP key `CCleaner` → `CCleaner 7` | **Detect PASS** (v7.6.1275). Auto-updates during install; Clean FAIL due to auto-updated version persisting. Accept as PASS with caveat. |
| 2 | Sysinternals Suite (x64) | ZIP install script rewritten | Install.ps1 copies files to `C:\Program Files\Sysinternals\`. Needs interactive session — WinRM Start-Process doesn't execute file copy in Session 0. |
| 3 | WizTree (x64) | InstallerFramework `NativeMSI` → `InnoSetup` | InnoSetup `/VERYSILENT` exits 0 but doesn't install via WinRM. Needs interactive session. |
| 4 | pgAdmin 4 (x64) | (none needed) | InnoSetup `/VERYSILENT` exits 0 but doesn't install via WinRM. Needs interactive session. |

---

## Excluded / Deferred (16 apps)

| # | App | Reason |
|---|-----|--------|
| 1 | Brave Browser (x64) | No MSI. Squirrel fails under SYSTEM. Per-user/Interactive only. |
| 2 | CPU-Z | Download gate/CAPTCHA on cpuid.com. No automation-friendly download URL. |
| 3 | CutePDF Writer | Install works, uninstall uses custom unInstcpw64.exe (printer driver, non-standard) |
| 4 | FileZilla Client (x64) | PUP/adware in installer |
| 5 | Foxit Reader (x64) | CDN throttling |
| 6 | Microsoft Teams (x64) | MSIX deployment type needed |
| 7 | Obsidian (x64) | Per-user NSIS install |
| 8 | Opera Browser (x64) | Per-user/Available/Interactive only. No GPO, no silent SYSTEM uninstall. |
| 9 | Postman (x64) | Per-user Squirrel install |
| 10 | PyCharm Community (x64) | NSIS hangs under SYSTEM context |
| 11 | TreeSize Free | InnoSetup hangs under SYSTEM context |
| 12 | Visual Studio 2026 Community | Too large for lab VM |
| 13 | Visual Studio 2026 Enterprise | Too large for lab VM |
| 14 | Visual Studio Code (x64) | Per-user/Interactive. InnoSetup user setup. |
| 15 | VMware Tools (x64) | Can't install on Hyper-V |
| 16 | Zoom Workplace (x64) | Per-user MSI. Should use per-user deployment type (v2.2). |

---

## Changes — 2026-09-07

**Definition fixes (3 apps):** HashTools, HeidiSQL and PSPad passed an Inno `/DIR=` target holding a space with no quotes of its own. The wrapper hands the whole argument string to Start-Process, which adds no quoting, so the installer read the directory as `C:\Program` — the same shape Typora hit on 2026-09-05. All three now quote the value. None of the three carries a recorded verdict, so no verdict is reopened; they need a first lab pass, not a retest.

**Engine changes affecting verdicts:**
- The version read from an installer's version resource now falls back to the four fixed-block product parts when the string table is empty. TeamViewer Host stages as `15.81.6.0` where it staged `15.81.6` before, and its file-version detection carries the same value. TeamViewer Host holds a FULL PASS verdict from 2026-04-06; the staged version string changed, so its detection needs one confirming lab pass.
- A version scraped from the installer file revalidates its cached download instead of re-fetching it: the request carries the cached file's timestamp, an unchanged release answers 304 and keeps the cache, and a failed request keeps the cache too. The payload staged for a given version is unchanged, so no verdict is affected. Verified against the TeamViewer Host download on both branches (304 with the cache kept, 200 with the cache replaced and its timestamp set from the server).
- Intune detection rules map every declared operator (`IsEquals`, `NotEquals`, `GreaterThan`, `GreaterEquals`, `LessThan`, `LessEquals`); a prefix, suffix or substring operator becomes a PowerShell script rule reading the value from the requested registry view, and an unmapped operator is refused instead of published as equality. Compound AND detections map one rule per clause. No shipped definition declares those three operators today, so no verdict is affected.

---

## Changes — 2026-09-05

Engine changes affecting verdicts (lab results in the 2026-09-05 table above):
- Definitions whose `UninstallCommand` is a plain command line (17: Android Studio, Bambu Studio, BleachBit, calibrite PROFILER, PeaZip, Proton VPN, Raspberry Pi Imager, Salesforce CLI, Simplenote, Spectra PDF, Stellarium, SyncBackFree, TurboVNC, Typora, UltraVNC, Unity Hub, Windows Admin Center) shipped an uninstall wrapper that failed to parse: the quoted path parsed as a string expression and the first switch as a stray token. The Windows ADK and Windows PE add-on definitions carry the same shape but stage as bootstrapper layouts with their own wrappers, so they were never affected. The wrapper now runs them through Start-Process; 14 of the 17 passed all four phases the same day, Typora and Spectra PDF after their own fixes, and the other three are listed above with the reason each did not complete.
- Install for (System / User) on eleven definitions rewrites the staged switch, uninstaller, detection hive/view and context from the compiled script. KDiff3 staged in User mode went through the remote harness on CLIENT01 in the LabAdmin session: install `/S /CurrentUser`, key found under HKCU, uninstall through `%LOCALAPPDATA%ProgramsKDiff3Ninstall.exe` with the `Au_` wait, key gone (all four phases PASS). The other ten have System-mode verdicts only.
- Drop analysis reads NSIS and Inno Setup compiled scripts; verdicts for drops are not tracked here.

---

## Changes — 2026-09-04

Verified through the remote harness on CLIENT01 (see the 2026-09-04 table above).

**Definition fixes (10 apps):**
- Slack: MSIX provisioned for all users (vendor withdrew the machine-wide MSI); Excluded → FULL PASS
- Audacity: 4.0 ships an MSI; InstallerType MSI with ProductCode detection
- ShareX: 21.0 splits the setup by architecture; the x64 asset is taken, older single setups still resolve
- Defraggler: version-history page gone; build from the builds page, version from the file resource
- RStudio: `/S /allusers` install and uninstall, key in the 64-bit view (no WOW6432Node segment)
- Gpg4win: key in the 64-bit view; KDiff3: key in the 32-bit view (both from real installs)
- KDiff3: uninstall `/S` alone; with `_?=` the uninstaller exits -1 and removes nothing
- Apache NetBeans: new definition (codelerity packages, Inno Setup, key `Apache NetBeans_is1`)
- Obsidian: newest release carrying a Windows installer (the release tagged latest can be mobile-only); WizTree: file host moved (stage-verified only)

**Engine changes affecting verdicts:**
- EXE uninstall wrapper (Inno Setup and non-NSIS engines without UninstallCommand): resolves the product's own ARP entry by detection key, install folder, then display name. The earlier body ran the first non-msiexec entry found, which on a machine with several products of one engine removes the wrong one. Lab-exercised on ShareX, Apache NetBeans (Inno) and Gpg4win (NSIS). Other FULL PASS verdicts for Inno definitions without UninstallCommand were earned on a single-app VM and still hold for that shape.
- Stage-time naming ignores metadata read off a bundled component EXE (KDiff3 staged as "7-Zip 25.01", Gpg4win as "GnuPG (2.5.21)" before).
- Remote harness evaluates Script detections (MSIX).

---

## Changes — 2026-04-10

**Definition fixes (3 apps):**
- CCleaner: ARP detection key `CCleaner` → `CCleaner 7` (vendor changed registry key in v7)
- WinMerge: InstallerFramework `NativeMSI` → `InnoSetup` (was triggering MSI COM inspection on an InnoSetup EXE)
- WizTree: InstallerFramework `NativeMSI` → `InnoSetup` (same root cause as WinMerge)

**Moved to Excluded (4 apps):**
- CPU-Z: CAPTCHA download gate on cpuid.com — no automation-friendly download URL
- Zoom Workplace: Per-user MSI, needs per-user deployment type (v2.2)
- PyCharm Community: NSIS hangs under SYSTEM context
- TreeSize Free: InnoSetup hangs under SYSTEM context

**Status change:** Detection FAIL (8) + Timeout (2) → Needs Retest (6) + Excluded (+4 = 17)

---

## Changes — 2026-04-06

Tested via psexec -s install.bat/uninstall.bat on clean CLIENT01 with reboots between every step.

**Promoted to FULL PASS (31 apps, from 45 to 76):**
- Adobe Acrobat Reader DC, ASP.NET 8, Audacity, Bitwarden, DBeaver, GIMP, Git, Malwarebytes
- M365 Apps x64/x86, M365 Project x86, M365 Visio x64/x86 (same ODT pattern as Project x64)
- Mozilla Firefox, Mozilla Thunderbird, Notepad++, Positron, PostgreSQL 16
- Power BI Desktop, PowerToys, Python, R for Windows, RStudio Desktop
- ShareX, SoapUI, SSMS 22, TeamViewer Host, Vim, WinRAR, WinSCP, Wireshark

**Engine changes:**
- `UninstallCommand` field on AppDefinition — direct uninstall path, bypasses ARP search
- Supported in both MSI and EXE wrapper generators
- NSIS wrapper strips arch suffix for broader ARP DisplayName matching
- MSI wrapper reads ProductCode from stage-manifest.json

**Definition fixes (20 apps):**
- 15 apps got UninstallCommand with vendor-documented silent uninstall paths
- 7 InstallerFramework/UninstallArgs corrections
- WebView2: CanUninstall=false (shared runtime)

**Vendor sources documented in UNINSTALL-REFERENCE.md:**
- Adobe, Bitwarden, DBeaver, Malwarebytes, Mozilla, Opera, Python, Tableau, WinSCP, InnoSetup, Chocolatey

## Changes — 2026-04-05 (reconciliation)

| Change | Detail |
|--------|--------|
| M365 Project (x64) | Detect FAIL -> **FULL PASS** |
| TeamViewer (x64) | Timeout -> **FULL PASS** |
| Python (x64) | Install FAIL -> **Uninstall/Clean** (install works fine) |
| Microsoft OLE DB 19 (x64) | FULL PASS -> **Install FAIL** (exit 1603) |
| WinMerge (x64) | Stage FAIL -> **Detect FAIL** |
| WizTree (x64) | Stage FAIL -> **Detect FAIL** |
| Tableau Prep Builder (x64) | Uninstall/Clean -> **Install FAIL** |
