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
| Vim | `"C:\Program Files\Vim\vim9x\unins000.exe" /VERYSILENT /SUPPRESSMSGBOXES /NORESTART` | vim.exe, gvim.exe | InnoSetup. Inner folder version-specific (vim91, vim92). |
| WinSCP | `"C:\Program Files (x86)\WinSCP\unins000.exe" /VERYSILENT /SUPPRESSMSGBOXES /NORESTART` | WinSCP.exe | InnoSetup (NOT NSIS). ARP key: `winscp3_is1` (legacy name). |

## NSIS Apps

Standard pattern: `"<InstallDir>\uninstall.exe" /S` (case-sensitive S)

| App | Uninstall Command | Kill First | Notes |
|-----|-------------------|-----------|-------|
| Bitwarden | `"C:\Program Files\Bitwarden\Uninstall Bitwarden.exe" /S` | Bitwarden.exe | Space in filename |
| DBeaver | `"C:\Program Files\DBeaver\Uninstaller.exe" /S /allusers` | dbeaver.exe | `/allusers` scope flag required |
| Notepad++ | `"C:\Program Files\Notepad++\uninstall.exe" /S` | notepad++.exe | Close all instances |
| WinRAR | `"C:\Program Files\WinRAR\uninstall.exe" /S` | WinRAR.exe | Standard |
| Wireshark | `"C:\Program Files\Wireshark\uninstall.exe" /S` | Wireshark.exe, dumpcap.exe | NSIS (NOT InnoSetup). Npcap separate: `"C:\Program Files\Npcap\uninstall.exe" /S` |

## WiX Burn Apps

Pattern: ARP UninstallString + `/quiet /norestart`, or `msiexec /x {ProductCode} /qn /norestart`

| App | Uninstall Command | Notes |
|-----|-------------------|-------|
| ASP.NET 8 Hosting | ARP UninstallString + `/uninstall /quiet /norestart` | Burn bundle. Stop IIS first. |
| Power BI Desktop | Package Cache EXE + `/uninstall /quiet /norestart` | Standalone MSI retired. Locate via `C:\ProgramData\Package Cache\*\PBIDesktopSetup_x64.exe` |
| PowerToys | Package Cache EXE + `/uninstall /quiet /norestart` | Kill 8+ PowerToys processes first |
| Python | Package Cache EXE + `/uninstall /quiet` | Python Launcher is separate ARP entry |
| SSMS 22 | `"...\Installer\setup.exe" uninstall --quiet --productId Microsoft.VisualStudio.Product.Ssms --channelId SSMS.22.SSMS.Release --noweb --norestart` | VS Installer engine (not standard Burn). Leaves VS Installer behind. |
| Tableau Desktop | Package Cache EXE + `/uninstall /quiet /norestart` | Close `tableau.exe` first |
| Tableau Prep | Package Cache EXE + `/uninstall /quiet /norestart` | Same |
| Tableau Reader | Package Cache EXE + `/uninstall /quiet /norestart` | Same |

## Other Frameworks

| App | Uninstall Command | Kill First | Notes |
|-----|-------------------|-----------|-------|
| Adobe Reader DC | `msiexec /x {ProductCode} /qn /norestart` | AcroRd32.exe | ProductCode varies by version/language. Read from ARP or stage-manifest.json. |
| Malwarebytes | `"...\Malwarebytes\Anti-Malware\mbuns.exe" /VERYSILENT /NORESTART /NOSURVEY` | MBAMService (stop), mbamtray.exe | Dedicated uninstaller `mbuns.exe`. Driver needs reboot. |
| Edge WebView2 | `setup.exe --uninstall --msedgewebview --system-level --force-uninstall` | - | Version in path. `--force-uninstall` required. May break dependent apps. |
| Firefox | `"C:\Program Files\Mozilla Firefox\uninstall\helper.exe" /S` | firefox.exe | Do NOT use `msiexec /x` (MSI is thin wrapper, gives 1605). |
| Thunderbird | `"C:\Program Files\Mozilla Thunderbird\uninstall\helper.exe" /S` | thunderbird.exe | Same as Firefox. Do NOT use `msiexec /x`. |
| Opera | `opera.exe --uninstall --runimmediately --deleteuserprofile=1` | opera.exe | No fully silent switch (vendor removed it). Brief progress dialog. |
| Positron | `"C:\Program Files\Positron\unins000.exe" /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP-` | positron.exe | InnoSetup (NOT NSIS). |
| PostgreSQL 16 | `"...\PostgreSQL\16\uninstall-postgresql.exe" --mode unattended` | postgresql-x64-16 (stop service) | BitRock. Data dir NOT removed by design. |
| RStudio | `"C:\Program Files\RStudio\Uninstall.exe" /S` | rstudio.exe | NSIS. May be under `Posit\RStudio` on newer versions. |
| SoapUI | `"...\SmartBear\SoapUI-{version}\uninstall.exe" -q` | SoapUI.exe | InstallAnywhere. `-q` for quiet (not `/q`). Version in path. |
| TeamViewer Host | `"C:\Program Files\TeamViewer\uninstall.exe" /S` | TeamViewer (stop service) | Stop service first: `net stop TeamViewer` |

## Special Cases

| App | Decision | Reason |
|-----|---------|--------|
| Edge WebView2 | Set `CanUninstall = false` | Microsoft: Evergreen runtime should not be removed |
| VC++ Redistributable x64/x86 | Set `CanUninstall = false` | Shared components — other apps depend on them |
| PostgreSQL 16 data dir | Not a bug | Vendor design: data preserved for safety |
