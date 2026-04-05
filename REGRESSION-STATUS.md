# Packr Regression Test Status — 2026-04-04

**Lab:** CM01 + CLIENT01 (Hyper-V, CM 2509)
**Total definitions:** 117
**Test harness:** `tools/TestHarness/` — headless, SYSTEM context via psexec, 300s timeout, incremental CSV results

---

## Test Harness Results (Final Run)

| Metric | Count |
|--------|-------|
| Stage PASS | 108 |
| Install PASS | 91 |
| Detect PASS | 75 |
| Uninstall PASS | 72 |
| **FULL PASS** | **39** |
| Clean FAIL (uninstall leaves traces) | 36 |
| Detect FAIL | 16 |
| Install FAIL | 13 |
| TIMEOUT | 4 |
| Deferred/Skipped | 8 |

---

## FULL PASS — All Phases (39 apps)

| App | Install Time |
|-----|-------------|
| Amazon Corretto JDK 8/11/17/21/25 (x64+x86) | 5-8s |
| Chrome | 19s |
| draw.io | 6s |
| Eclipse Temurin JDK 8/11/17/21/25 (x64+x86) | 3-7s |
| Eclipse Temurin JRE 8/11/17/21/25 (x64+x86) | 3-4s |
| Everything | 4s |
| Inkscape | 47s |
| KeePass | 6s |
| LibreOffice | 57s |
| Microsoft Edge | 2s (CanUninstall=false) |
| mRemoteNG | 16s |
| Node.js | 11s |
| OLE DB 19 | 3s |
| paint.net | 5s |
| PowerShell 7 | 5s |
| PuTTY | 3s |
| TortoiseGit | 10s |
| TortoiseSVN | 9s |
| VLC | 5s |
| Webex | 5s |
| WinDirStat | 3s |
| XenServer VM Tools | 29s |

## Install + Detect PASS, Clean FAIL (36 apps)

Uninstall runs but leaves ARP traces. Framework-aware uninstall scripts deployed but need per-app refinement.

Adobe Reader, ASP.NET 8, Audacity, Bitwarden, DBeaver, .NET 8 x64/x86, .NET 9, Firefox, GIMP, Git, Greenshot (exit 5), Malwarebytes, Notepad++, Opera, Positron, PostgreSQL 16, Power BI, PowerToys, R, RStudio, ShareX, SoapUI, SSMS 22, Tableau Desktop/Prep/Reader, TeamViewer Host, Vim, VC++ x64/x86, WebView2, WinRAR, WinSCP, Wireshark

## Install PASS, Detect FAIL (10 apps)

| App | Issue |
|-----|-------|
| M365 Apps/Project/Visio x64+x86 (6) | ODT install didn't execute (0ms) — setup.exe not in staged content for harness |
| Obsidian | Per-user NSIS, /D= path not working under SYSTEM |
| pgAdmin 4 | Detection path (runtime subfolder) |
| Postman | Per-user Squirrel app |
| Zoom | MSI detection not matching |

## Install FAIL (13 apps)

| App | Exit | Root Cause |
|-----|------|-----------|
| Anaconda | 2 | Already installed or path conflict |
| AnyDesk | 11341829 | Custom error, needs interactive |
| CCleaner | 111111 | Custom Piriform error |
| Citrix Workspace | 40032 | Missing prereqs (.NET 8 + MSVC) |
| CutePDF | 1 | InnoSetup needs specific args |
| Greenshot | 5 | InnoSetup access denied |
| ODBC 18 | 1603 | Pending reboot or version conflict |
| OLE DB 19 | 1603 | Pending reboot (second run) |
| PostgreSQL 13/14/15/17 | 1 | Port conflict with PG16 |
| Python | 1603 | Already installed same version |
| TeamViewer | 1603 | Already installed (Host present) |

## TIMEOUT (4 apps)

Brave Browser, TreeSize Free, VS Code, PyCharm — interactive dialogs under SYSTEM context

## Deferred/Skipped (8 apps)

7-Zip (harness dependency), .NET 10 (test last), FileZilla (PUP), Foxit (CDN), Slack/Teams (MSIX), VMware Tools (Hyper-V), VS Enterprise/Community (too large)

---

## Next Steps

1. Fix uninstall scripts — per-app investigation for 36 clean-fail apps
2. Fix M365 ODT staging for test harness (setup.exe path)
3. Fix per-user app detection (Obsidian, Postman, Zoom)
4. Resolve install prereq ordering (Citrix needs .NET+MSVC first)
5. Fix timeout apps (increase timeout or fix silent args)
6. Rerun harness after fixes
