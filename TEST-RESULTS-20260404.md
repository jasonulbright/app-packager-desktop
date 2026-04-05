# Test Results — 2026-04-04

**Environment:** CLIENT01 (Hyper-V VM, CM 2509), SYSTEM context via psexec
**Harness:** tools/TestHarness, ps1 direct execution, 300s timeout per app

## Summary

| Category | Count |
|----------|-------|
| **FULL PASS** (Install + Detect + Uninstall + Clean) | 37 |
| **Install + Detect PASS, Clean FAIL** (uninstall script issue) | 20 |
| **Install PASS, Detect FAIL** (detection rule issue) | 10 |
| **Stage FAIL** (7z not found — now installed, rerun) | 14 |
| **Install FAIL** (installer args or environment) | 8 |
| **TIMEOUT** (hung installer or interactive dialog) | 4 |
| **Deferred** (MSIX, PUP, hypervisor, .NET 10) | 7 |

## FULL PASS (37)

7-Zip, Amazon Corretto JDK 11/17/21/25/8 (x64+x86), draw.io, Everything, Inkscape, KeePass, LibreOffice, mRemoteNG, OLE DB 19, Node.js, paint.net, PowerShell 7, PuTTY, TortoiseGit, TortoiseSVN, VLC, Webex, WinDirStat, Eclipse Temurin JDK 11/17/21/25/8 (x64+x86), Eclipse Temurin JRE 11/17/21/25/8 (x64+x86), XenServer VM Tools, Microsoft Edge (CanUninstall=false)

## Install + Detect PASS, Clean FAIL (20) — uninstall scripts need fixing

Adobe Reader, Git, Malwarebytes, Notepad++, Opera, Positron, PostgreSQL 16, Power BI, PowerToys, R, RStudio, ShareX, SoapUI, SSMS 22, Tableau Desktop/Prep/Reader, TeamViewer Host, Vim, WebView2, WinRAR, WinSCP, Wireshark, VC++ x64/x86

## Install PASS, Detect FAIL (10) — detection rules need fixing

- M365 Apps x64/x86, Project x64/x86, Visio x64/x86 — ODT detection (installed 0ms = didn't actually run)
- Obsidian — per-user install to wrong path
- pgAdmin 4 — detection path (runtime subfolder)
- Postman — per-user Squirrel app
- PS7 LTS — ProductCode changed
- Sysinternals — ZIP, no ARP entry
- XenCenter — detection issue
- Zoom — MSI detection not matching

## Stage FAIL — 7z not found (14) — FIXED, rerun needed

7z was not installed on CLIENT01 when these apps staged. Now installed. Rerun will stage+test these.

## Install FAIL (8)

| App | Exit | Cause |
|-----|------|-------|
| Anaconda | 2 | Already installed or path conflict |
| CCleaner | 111111 | Custom Piriform error |
| Citrix Workspace | 40032 | Citrix custom error |
| ODBC 18 | 1603 | MSI fatal error (already installed same version) |
| PostgreSQL 13/14/15/17 | 1 | Port conflict with PG16 already running |
| Python | 1603 | Already installed same version |
| TeamViewer | 1603 | Already installed (TeamViewer Host present) |

## TIMEOUT (4)

Brave Browser, TreeSize Free, VS Code, PyCharm — installer hung or interactive dialog under SYSTEM

## Next Steps

1. Fix uninstall scripts for the 20 "Clean FAIL" apps
2. Rerun with 7z installed for the 14 stage failures
3. Fix detection for the 10 detect-fail apps
4. Investigate install failures — most are "already installed" conflicts
5. Increase timeout or fix silent args for the 4 timeout apps
