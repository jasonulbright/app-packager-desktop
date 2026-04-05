# Vendor Silent Uninstall Reference

Enterprise silent uninstall commands for all Packr-supported applications.
Sourced from vendor docs, Chocolatey package scripts, and enterprise deployment guides.

**Rule: Always reboot before attempting uninstall if any prior install/uninstall triggered a reboot.**
**Rule: Always kill the app process before uninstalling.**

---

## InnoSetup Apps

Standard pattern: `"<InstallDir>\unins000.exe" /VERYSILENT /SUPPRESSMSGBOXES /NORESTART`

| App | Uninstall Command | Kill First | Notes |
|-----|-------------------|-----------|-------|
| Audacity | `"C:\Program Files\Audacity\unins000.exe" /VERYSILENT /SUPPRESSMSGBOXES /NORESTART` | audacity.exe | Standard |
| GIMP | `"C:\Program Files\GIMP 3\unins000.exe" /VERYSILENT /SUPPRESSMSGBOXES /NORESTART` | gimp.exe | Path varies by major version |
| Git | `"C:\Program Files\Git\unins000.exe" /VERYSILENT /SUPPRESSMSGBOXES /NORESTART` | git.exe, bash.exe | Close all Git Bash sessions |
| R | `"C:\Program Files\R\R-{version}\unins000.exe" /VERYSILENT /SUPPRESSMSGBOXES /NORESTART` | R.exe, Rgui.exe | Version in path |
| ShareX | `"C:\Program Files\ShareX\unins000.exe" /VERYSILENT /SUPPRESSMSGBOXES /NORESTART` | ShareX.exe | Runs in system tray |
| Vim | `"C:\Program Files\Vim\vim9{x}\uninstall-gui.exe" /S` | vim.exe, gvim.exe | NOT standard InnoSetup — uses own uninstaller with /S |
| WinSCP | `"C:\Program Files (x86)\WinSCP\unins000.exe" /VERYSILENT /SUPPRESSMSGBOXES /NORESTART` | WinSCP.exe | May be x86 install |
| Wireshark | `"C:\Program Files\Wireshark\unins000.exe" /VERYSILENT /SUPPRESSMSGBOXES /NORESTART` | Wireshark.exe, dumpcap.exe | Npcap NOT removed (separate MSI) |

## NSIS Apps

Standard pattern: `"<InstallDir>\uninstall.exe" /S` (case-sensitive S)

| App | Uninstall Command | Kill First | Notes |
|-----|-------------------|-----------|-------|
| Bitwarden | `"C:\Program Files\Bitwarden\Uninstall Bitwarden.exe" /S` | Bitwarden.exe | Space in filename |
| DBeaver | `"C:\Program Files\DBeaver\uninstall.exe" /S` | dbeaver.exe | Standard |
| Notepad++ | `"C:\Program Files\Notepad++\uninstall.exe" /S` | notepad++.exe | Close all instances |
| WinRAR | `"C:\Program Files\WinRAR\uninstall.exe" /S` | WinRAR.exe | Standard |

## WiX Burn Apps

Pattern: ARP UninstallString + `/quiet /norestart`, or `msiexec /x {ProductCode} /qn /norestart`

| App | Uninstall Command | Notes |
|-----|-------------------|-------|
| SSMS 22 | ARP UninstallString + `/quiet /norestart` | VS Installer Burn bundle. Takes several minutes. |
| Tableau Desktop | `msiexec /x {ProductCode} /qn /norestart` | MSI uninstall despite Burn install |
| Tableau Prep | `msiexec /x {ProductCode} /qn /norestart` | Same |
| Tableau Reader | `msiexec /x {ProductCode} /qn /norestart` | Same |

## Other Frameworks

| App | Uninstall Command | Kill First | Notes |
|-----|-------------------|-----------|-------|
| Adobe Reader DC | `msiexec /x {AC76BA86-7AD7-1033-7B44-AC0F074E4100} /qn /norestart` | AcroRd32.exe | ProductCode varies by version/language |
| ASP.NET 8 Hosting | ARP UninstallString + `/quiet /norestart` | - | Burn bundle. Individual MSI components also removable. |
| Malwarebytes | `"C:\Program Files\Malwarebytes\Anti-Malware\mbuns.exe" /verysilent /suppressmsgboxes /norestart` | MBAMService (stop service) | Dedicated uninstaller. May need `/DELETEALL`. |
| Edge WebView2 | **Cannot silently uninstall** (Evergreen) | - | Microsoft design: other apps depend on it. Mark CanUninstall=false. |
| Firefox | `"C:\Program Files\Mozilla Firefox\uninstall\helper.exe" /S` | firefox.exe | Standard |
| Thunderbird | `"C:\Program Files\Mozilla Thunderbird\uninstall\helper.exe" /S` | thunderbird.exe | Standard |
| Opera | `"C:\Program Files\Opera\launcher.exe" --uninstall /S` | opera.exe | System-level path |
| Positron | `"C:\Program Files\Positron\Uninstall Positron.exe" /S` | positron.exe | Electron/NSIS |
| PostgreSQL 16 | `"C:\Program Files\PostgreSQL\16\uninstall-postgresql.exe" --mode unattended` | postgresql-x64-16 (stop service) | Data dir NOT removed by default |
| Power BI | `msiexec /x {ProductCode} /qn /norestart` | PBIDesktop.exe | MSI version. Store version uses Remove-AppxPackage. |
| PowerToys | ARP UninstallString + `/quiet` | PowerToys.exe | Close from system tray first |
| RStudio | `"C:\Program Files\RStudio\uninstall.exe" /S` | rstudio.exe | NSIS |
| SoapUI | `"C:\Program Files\SmartBear\SoapUI-{version}\uninstall.exe" -q` | SoapUI.exe | install4j. `-q` for quiet. Version in path. |
| TeamViewer Host | `"C:\Program Files\TeamViewer\uninstall.exe" /S` or `msiexec /x {ProductCode} /qn` | TeamViewer (stop service) | MSI for enterprise deploy |

## Special Cases

| App | Decision | Reason |
|-----|---------|--------|
| Edge WebView2 | Set `CanUninstall = false` | Microsoft: Evergreen runtime should not be removed |
| VC++ Redistributable x64/x86 | Set `CanUninstall = false` | Shared components — other apps depend on them |
| PostgreSQL 16 data dir | Not a bug | Vendor design: data preserved for safety |
