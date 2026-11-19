# Supported applications

The catalog holds 326 application definitions. The list comes from the definition files in `src/ApplicationPackagerEngine/Definitions`. The **Application** column is the name that shows in the grid.

PowerShell 7 comes in three definitions, one for each release channel of Microsoft Learn ([PowerShell Support Lifecycle](https://learn.microsoft.com/en-us/powershell/scripting/install/powershell-support-lifecycle)): **PowerShell 7 (x64)** is the Long Term Servicing (LTS) line, today 7.6 (supported until 14-Nov-2028), as the Microsoft MSI for all users, with remoting and the all-users scope. **PowerShell 7 Stable (MSIX, per user)** is the Stable release (7.7), as the Microsoft MSIX package: a single-user installation without PowerShell remoting. The Learn page still lists 7.7 as a preview, although 7.7.0 is published. **PowerShell 7 (Devolutions, third party)** is a third-party MSI that Devolutions builds from the Microsoft payload; Microsoft does not support it. The definition `powershell7lts` (PowerShell 7.4) was removed because 7.4 is out of support since 10 Nov 2026; its settings move to PowerShell 7 (x64).

| Vendor | Application | Architecture | Installer type |
|---|---|---|---|
| 2BrightSparks | SyncBackFree | x86 | EXE |
| 7-Zip | 7-Zip (x64) | x64 | MSI |
| Acro Software Inc. | CutePDF Writer | x64 | EXE |
| Adobe Inc. | Adobe Acrobat (64-bit) | x64 | EXE |
| AIMP DevTeam | AIMP (x64) | x64 | EXE |
| Aivar Annamaa | Thonny (x64) | x64 | EXE |
| AM Crypto | VeraCrypt (x64) | x64 | MSI |
| Amazon | Amazon Corretto JDK 11 | x64 | MSI |
| Amazon | Amazon Corretto JDK 11 (x86) | x86 | MSI |
| Amazon | Amazon Corretto JDK 17 | x64 | MSI |
| Amazon | Amazon Corretto JDK 21 | x64 | MSI |
| Amazon | Amazon Corretto JDK 25 | x64 | MSI |
| Amazon | Amazon Corretto JDK 8 | x64 | MSI |
| Amazon | Amazon Corretto JDK 8 (x86) | x86 | MSI |
| Amazon | AWS Command Line Interface (x64) | x64 | MSI |
| Amazon | AWS Tools for Windows (x64) | x64 | MSI |
| Amazon | AWS VPN Client (x64) | x64 | MSI |
| Amazon Web Services | Amazon DCV Client | x64 | MSI |
| Amazon Web Services | Amazon Redshift ODBC Driver (x64) | x64 | MSI |
| Amazon Web Services | Amazon WorkSpaces | x64 | MSI |
| Amazon Web Services | AWS SAM CLI (x64) | x64 | MSI |
| Amazon Web Services | AWS Session Manager Plugin (x64) | x64 | EXE |
| Anaconda, Inc. | Anaconda Distribution (x64) | x64 | EXE |
| Ansgar Becker | HeidiSQL (x64) | x64 | EXE |
| Antibody Software | WizTree (x64) | x64 | EXE |
| AnyDesk Software GmbH | AnyDesk | x64 | EXE |
| Apache Software Foundation | Apache NetBeans (x64) | x64 | EXE |
| Arduino | Arduino IDE (x64) | x64 | MSI |
| Audacity | Audacity (x64) | x64 | MSI |
| Automattic | Simplenote (x64) | x64 | EXE |
| AxCrypt | AxCrypt (x64) | x64 | MSI |
| Azul | Azul Zulu JDK 11 (x64) | x64 | MSI |
| Azul | Azul Zulu JDK 17 (x64) | x64 | MSI |
| Azul | Azul Zulu JDK 8 (x64) | x64 | MSI |
| Azul | Azul Zulu JRE 11 (x64) | x64 | MSI |
| Azul | Azul Zulu JRE 17 (x64) | x64 | MSI |
| Azul | Azul Zulu JRE 8 (x64) | x64 | MSI |
| Baldur Karlsson | RenderDoc (x64) | x64 | MSI |
| Bambu Lab | Bambu Studio (x64) | x64 | EXE |
| BellSoft | Liberica JDK 21 (x64) | x64 | MSI |
| Binary Fortress Software | HashTools (x64) | x64 | EXE |
| Bitwarden Inc | Bitwarden Desktop (x64) | x64 | EXE |
| BleachBit | BleachBit (x86) | x86 | EXE |
| Blender Foundation | Blender (x64) | x64 | MSI |
| BornToBeRoot | NETworkManager (x64) | x64 | MSI |
| Box | Box Drive (x64) | x64 | MSI |
| Brave Software | Brave Browser (x64) | x64 | EXE |
| Bruno Software | Bruno (x64) | x64 | MSI |
| Broadcom | VMware Tools (x64) | x64 | EXE |
| CAKE.com | Clockify (x64) | x64 | MSI |
| Calibrite | Calibrite PROFILER (x64) | x64 | EXE |
| Charles Lechasseur | Path Copy Copy (x64) | x64 | EXE |
| Chef Software | Chef Workstation (x64) | x64 | MSI |
| Cisco | Cisco Webex (x64) | x64 | MSI |
| Cisco | Duo Desktop (x64) | x64 | MSI |
| Citrix (Cloud Software Group) | Citrix Workspace (CR) (x64) | x64 | EXE |
| Cloud Software Group | Citrix Workspace LTSR (ARM64) | ARM64 | EXE |
| Cloud Software Group | Citrix Workspace LTSR (x64) | x64 | EXE |
| Cloud Software Group | Citrix Workspace LTSR (x86) | x86 | EXE |
| Cloud Software Group | XenCenter (x64) | x64 | MSI |
| Cloud Software Group | XenServer VM Tools (x64) | x64 | MSI |
| CloudCompare Project | CloudCompare (x64) | x64 | EXE |
| Cloudflare | Cloudflare WARP (x64) | x64 | MSI |
| Code Sector | TeraCopy (x64) | x64 | EXE |
| Corporation for Digital Scholarship | Zotero (x64) | x64 | EXE |
| CPUID | CPU-Z | x64 | EXE |
| CPUID | HWMonitor (x64) | x64 | EXE |
| DAX Studio | DAX Studio (x64) | x64 | EXE |
| DB Browser for SQLite Team | DB Browser for SQLite (x64) | x64 | MSI |
| DBeaver Corp | DBeaver Community (x64) | x64 | EXE |
| DbVis Software AB | DbVisualizer (x64) | x64 | EXE |
| Dell | RVTools (x64) | x64 | MSI |
| Dell Inc. | Dell Command Update (x64) | x64 | EXE |
| Devolutions | PowerShell 7 (Devolutions, third party) | x64 | MSI |
| Devolutions | Remote Desktop Manager (x64) | x64 | MSI |
| DisplayLink | DisplayLink Graphics (x64) | x64 | EXE |
| dnGREP | dnGREP (x64) | x64 | MSI |
| Dominik Reichl | KeePass | x64 | MSI |
| Don HO | Notepad++ (x64) | x64 | EXE |
| dotPDN LLC | paint.net (x64) | x64 | MSI |
| Draftable | Draftable Desktop (x64) | x64 | MSI |
| Duong Dieu Phap | ImageGlass (x64) | x64 | MSI |
| Eclipse Adoptium | Eclipse Temurin JDK 11 | x64 | MSI |
| Eclipse Adoptium | Eclipse Temurin JDK 11 (x86) | x86 | MSI |
| Eclipse Adoptium | Eclipse Temurin JDK 17 | x64 | MSI |
| Eclipse Adoptium | Eclipse Temurin JDK 21 | x64 | MSI |
| Eclipse Adoptium | Eclipse Temurin JDK 25 | x64 | MSI |
| Eclipse Adoptium | Eclipse Temurin JDK 8 | x64 | MSI |
| Eclipse Adoptium | Eclipse Temurin JDK 8 (x86) | x86 | MSI |
| Eclipse Adoptium | Eclipse Temurin JRE 11 | x64 | MSI |
| Eclipse Adoptium | Eclipse Temurin JRE 11 (x86) | x86 | MSI |
| Eclipse Adoptium | Eclipse Temurin JRE 17 | x64 | MSI |
| Eclipse Adoptium | Eclipse Temurin JRE 21 | x64 | MSI |
| Eclipse Adoptium | Eclipse Temurin JRE 25 | x64 | MSI |
| Eclipse Adoptium | Eclipse Temurin JRE 8 | x64 | MSI |
| Eclipse Adoptium | Eclipse Temurin JRE 8 (x86) | x86 | MSI |
| FileZilla Project | FileZilla Client (x64) | x64 | EXE |
| Foxit Software | Foxit PDF Reader (x64) | x64 | EXE |
| FreeCAD Team | FreeCAD (x64) | x64 | EXE |
| g10 Code GmbH | Gpg4win (x64) | x64 | EXE |
| geek software GmbH | PDF24 Creator (x64) | x64 | MSI |
| Gephi Consortium | Gephi (x64) | x64 | EXE |
| gerardog | gsudo (x64) | x64 | MSI |
| Giorgio Tani | PeaZip (x64) | x64 | EXE |
| GitHub | GitHub CLI (x64) | x64 | MSI |
| GitHub | GitHub Desktop (User) | x64 | EXE |
| GlavSoft | TightVNC (x64) | x64 | MSI |
| GN Audio A/S | Jabra Direct (x86) | x86 | EXE |
| Google | Android Studio (x64) | x64 | EXE |
| Google | Chrome Remote Desktop Host (x64) | x64 | MSI |
| Google | Go Programming Language (x64) | x64 | MSI |
| Google | Google Chrome | x64 | MSI |
| Google | Google Credential Provider for Windows (x64) | x64 | MSI |
| Google | Google Drive for desktop (x64) | x64 | EXE |
| Google | IAP Desktop (x64) | x64 | MSI |
| Graphviz | Graphviz (x64) | x64 | EXE |
| Greenshot | Greenshot (x64) | x64 | EXE |
| HandBrake Team | HandBrake (x64) | x64 | EXE |
| HashiCorp | Vagrant (x64) | x64 | MSI |
| Henri Wahl | Nagstamon (x64) | x64 | EXE |
| IBM | IBM Aspera Connect (x64) | x64 | MSI |
| IBM | IBM Semeru Runtime Open Edition JDK 11 (x64) | x64 | MSI |
| IBM | IBM Semeru Runtime Open Edition JDK 17 (x64) | x64 | MSI |
| IBM | IBM Semeru Runtime Open Edition JDK 8 (x64) | x64 | MSI |
| IBM | IBM Semeru Runtime Open Edition JRE 11 (x64) | x64 | MSI |
| IBM | IBM Semeru Runtime Open Edition JRE 17 (x64) | x64 | MSI |
| IBM | IBM Semeru Runtime Open Edition JRE 8 (x64) | x64 | MSI |
| Inkscape Project | Inkscape | x64 | MSI |
| International GeoGebra Institute | GeoGebra Classic 6 (x64) | x64 | MSI |
| Irfan Skiljan | IrfanView (x64) | x64 | EXE |
| iterate GmbH | Cyberduck (x64) | x64 | EXE |
| JAM Software | TreeSize Free | x64 | EXE |
| Jan De Dobbeleer | Oh My Posh (x64) | x64 | MSIX |
| Jan Fiala | PSPad (x64) | x64 | EXE |
| Jason Ulbright | AppPackager Suite (User) | x64 | EXE |
| Jason Ulbright | Signing Suite (x64) | x64 | EXE |
| JetBrains | DataGrip (x64) | x64 | EXE |
| JetBrains | GoLand (x64) | x64 | EXE |
| JetBrains | PyCharm (x64) | x64 | EXE |
| JetBrains | WebStorm (x64) | x64 | EXE |
| JGraph Ltd | draw.io Desktop (x64) | x64 | MSI |
| John MacFarlane | Pandoc (x64) | x64 | MSI |
| Kai Kramer | KeyStore Explorer (x64) | x64 | EXE |
| Karakun AG | OpenWebStart (x64) | x64 | EXE |
| KDE | Krita (x64) | x64 | EXE |
| KDE e.V. | KDiff3 (x64) | x64 | EXE |
| KeePassXC Team | KeePassXC (x64) | x64 | MSI |
| Kitware | CMake (x64) | x64 | MSI |
| Kovid Goyal | calibre (x64) | x64 | MSI |
| Laurent Cozic | Joplin (x64) | x64 | EXE |
| Malwarebytes | Malwarebytes (x64) | x64 | EXE |
| Marcin Szeniak | Bulk Crap Uninstaller (x64) | x64 | EXE |
| MariaDB | MariaDB Server (x64) | x64 | MSI |
| Martin Prikryl | WinSCP (x64) | x64 | EXE |
| Mattermost | Mattermost Desktop (x64) | x64 | MSI |
| Meltytech | Shotcut (x64) | x64 | EXE |
| Microsoft | .NET 10 Desktop Runtime (x64) | x64 | EXE |
| Microsoft | .NET 11 Desktop Runtime (x64) | x64 | EXE |
| Microsoft | .NET 8 Desktop Runtime (x64) | x64 | EXE |
| Microsoft | .NET 8 Desktop Runtime (x86) | x86 | EXE |
| Microsoft | .NET 9 Desktop Runtime (x64) | x64 | EXE |
| Microsoft | .NET 9 Desktop Runtime (x86) | x86 | EXE |
| Microsoft | App Installer (WinGet) | x64 | MSIX |
| Microsoft | ASP.NET 10 Server Hosting Bundle (x64) | x64 | EXE |
| Microsoft | ASP.NET 11 Server Hosting Bundle (x64) | x64 | EXE |
| Microsoft | ASP.NET 8 Server Hosting Bundle (x64) | x64 | EXE |
| Microsoft | ASP.NET 9 Server Hosting Bundle (x64) | x64 | EXE |
| Microsoft | Azure CLI (x64) | x64 | MSI |
| Microsoft | Azure Functions Core Tools (x64) | x64 | MSI |
| Microsoft | Azure PowerShell (x64) | x64 | MSI |
| Microsoft | Azure Storage Explorer (x64) | x64 | EXE |
| Microsoft | M365 Apps for Enterprise (x64) | x64 | ODT |
| Microsoft | M365 Apps for Enterprise (x86) | x86 | ODT |
| Microsoft | M365 Project (x64) | x64 | ODT |
| Microsoft | M365 Project (x86) | x86 | ODT |
| Microsoft | M365 Visio (x64) | x64 | ODT |
| Microsoft | M365 Visio (x86) | x86 | ODT |
| Microsoft | Microsoft Edge | x64 | MSI |
| Microsoft | Microsoft Edge WebView2 Runtime (x64) | x64 | EXE |
| Microsoft | Microsoft ODBC Driver 18 for SQL Server (x64) | x64 | MSI |
| Microsoft | Microsoft OLE DB Driver 19 (x64) | x64 | MSI |
| Microsoft | Microsoft OpenJDK 17 (EXE, per user) | x64 | EXE |
| Microsoft | Microsoft OpenJDK 17 (EXE) | x64 | EXE |
| Microsoft | Microsoft OpenJDK 17 (MSI, per user) | x64 | MSI |
| Microsoft | Microsoft OpenJDK 17 (MSI) | x64 | MSI |
| Microsoft | Microsoft OpenJDK 21 (EXE, per user) | x64 | EXE |
| Microsoft | Microsoft OpenJDK 21 (EXE) | x64 | EXE |
| Microsoft | Microsoft OpenJDK 21 (MSI, per user) | x64 | MSI |
| Microsoft | Microsoft OpenJDK 21 (MSI) | x64 | MSI |
| Microsoft | Microsoft OpenJDK 25 (EXE, per user) | x64 | EXE |
| Microsoft | Microsoft OpenJDK 25 (EXE) | x64 | EXE |
| Microsoft | Microsoft OpenJDK 25 (MSI, per user) | x64 | MSI |
| Microsoft | Microsoft OpenJDK 25 (MSI) | x64 | MSI |
| Microsoft | Microsoft Teams (x64) | x64 | EXE |
| Microsoft | Power BI Desktop (x64) | x64 | EXE |
| Microsoft | PowerShell 7 (x64) | x64 | MSI |
| Microsoft | PowerShell 7 Stable (MSIX, per user) | x64 | MSIX |
| Microsoft | PowerToys (x64) | x64 | EXE |
| Microsoft | SharePoint Online Management Shell (x64) | x64 | MSI |
| Microsoft | SQL Server 2022 Express (x64) | x64 | EXE |
| Microsoft | SQL Server Management Studio 22 (x64) | x64 | EXE |
| Microsoft | Sysinternals Suite (x64) | x64 | ZIP |
| Microsoft | Visual C++ v14 Redistributable (x64) | x64 | EXE |
| Microsoft | Visual C++ v14 Redistributable (x86) | x86 | EXE |
| Microsoft | Visual Studio 2026 Community (x64) | x64 | EXE |
| Microsoft | Visual Studio 2026 Enterprise (x64) | x64 | EXE |
| Microsoft | Visual Studio Code (System) (x64) | x64 | EXE |
| Microsoft | Visual Studio Code (User) (x64) | x64 | EXE |
| Microsoft | Windows ADK for Windows 11 | x64 | EXE |
| Microsoft | Windows Admin Center (v2) | x64 | EXE |
| Microsoft | Windows PE add-on for the Windows ADK | x64 | EXE |
| MongoDB | MongoDB Compass (x64) | x64 | MSI |
| Mozilla | Mozilla Firefox (x64) | x64 | MSI |
| Mozilla | Mozilla Firefox ESR (x64) | x64 | MSI |
| Mozilla | Mozilla Thunderbird (x64) | x64 | MSI |
| mRemoteNG | mRemoteNG (x64) | x64 | MSI |
| MSEndpointMgr | Intune Debug Toolkit (x64) | x64 | MSI |
| MuleSoft | Anypoint Studio (x64) | x64 | EXE |
| MuseScore | MuseScore Studio (x64) | x64 | MSI |
| Mythicsoft | Agent Ransack (x64) | x64 | MSI |
| NAPS2 | NAPS2 (x64) | x64 | MSI |
| NetBird | NetBird (x64) | x64 | MSI |
| Nextcloud GmbH | Nextcloud Desktop Client (x64) | x64 | MSI |
| NGWIN | PicPick (x64) | x64 | EXE |
| Nicke Manarin | ScreenToGif (x64) | x64 | MSI |
| NoMachine | NoMachine (x64) | x64 | EXE |
| Northwestern University | NetLogo (x64) | x64 | MSI |
| NV Access | NVDA | x86 | EXE |
| NVIDIA | NVIDIA Graphics Driver - GeForce Game Ready (x64) | x64 | EXE |
| NVIDIA | NVIDIA Graphics Driver - RTX Enterprise (x64) | x64 | EXE |
| Obsidian | Obsidian (x64) | x64 | EXE |
| Ocenaudio Team | ocenaudio (x64) | x64 | EXE |
| Omnissa | Omnissa Horizon Client (x64) | x64 | EXE |
| OpenJS Foundation | Node.js LTS (x64) | x64 | MSI |
| OpenShot Studios, LLC | OpenShot Video Editor (x64) | x64 | EXE |
| OpenVPN Inc. | OpenVPN (x64) | x64 | MSI |
| Opera Software | Opera Browser (x64) | x64 | EXE |
| Oracle | MySQL Connector/NET | x86 | MSI |
| ownCloud GmbH | ownCloud Desktop Client (x64) | x64 | MSI |
| Parallels | Parallels Client (x64) | x64 | MSI |
| pdfforge GmbH | PDFCreator (x64) | x64 | EXE |
| PDFgear Software | PDFgear (x64) | x64 | EXE |
| pgAdmin Development Team | pgAdmin 4 (x64) | x64 | EXE |
| Pidgin | Pidgin (x64) | x64 | EXE |
| Piriform Software Ltd. | CCleaner | x64 | EXE |
| Piriform Software Ltd. | Defraggler (x64) | x64 | EXE |
| Posit PBC | Positron (x64) | x64 | EXE |
| Posit PBC | RStudio Desktop (x64) | x64 | EXE |
| PostgreSQL Global Development Group | PostgreSQL 13 (x64) | x64 | EXE |
| PostgreSQL Global Development Group | PostgreSQL 14 (x64) | x64 | EXE |
| PostgreSQL Global Development Group | PostgreSQL 15 (x64) | x64 | EXE |
| PostgreSQL Global Development Group | PostgreSQL 16 (x64) | x64 | EXE |
| PostgreSQL Global Development Group | PostgreSQL 17 (x64) | x64 | EXE |
| Postman | Postman (x64) | x64 | EXE |
| PowerSoft | AnyBurn (x64) | x64 | EXE |
| Proton AG | Proton VPN (x64) | x64 | EXE |
| Purslane Tech Pte. Ltd. | RustDesk (x64) | x64 | MSI |
| Python Software Foundation | Python (x64) | x64 | EXE |
| QGIS | QGIS (x64) | x64 | MSI |
| QGIS | QGIS LTR (x64) | x64 | MSI |
| Qoppa Software | PDF Studio Viewer (x64) | x64 | EXE |
| Rainmeter | Rainmeter (x64) | x64 | EXE |
| Raspberry Pi Ltd | Raspberry Pi Imager (x64) | x64 | EXE |
| riok GmbH | Kreya (x64) | x64 | MSI |
| Rocket.Chat | Rocket.Chat Desktop (x64) | x64 | MSI |
| Rony Shapiro | Password Safe (x64) | x64 | MSI |
| Salesforce | Salesforce CLI (x64) | x64 | EXE |
| Salesforce (Tableau) | Tableau Desktop (x64) | x64 | EXE |
| Salesforce (Tableau) | Tableau Prep Builder (x64) | x64 | EXE |
| Salesforce (Tableau) | Tableau Reader (x64) | x64 | EXE |
| Scooter Software | Beyond Compare 5 (x64) | x64 | EXE |
| ShareX Team | ShareX (x64) | x64 | EXE |
| Signal Ridge Labs | Spectra PDF (x64) | x64 | EXE |
| Simon Tatham | PuTTY (x64) | x64 | MSI |
| Skymatic | Cryptomator (x64) | x64 | MSI |
| Slack Technologies | Slack (x64) | x64 | MSIX |
| Slido | Slido for Windows (x64) | x64 | MSI |
| SmartBear Software | SoapUI Open Source | x64 | EXE |
| SMath | SMath Studio (x64) | x64 | MSI |
| Sober Lemur S.r.l. | PDFsam Basic (x64) | x64 | MSI |
| Softerra | Softerra LDAP Browser (x64) | x64 | MSI |
| SoftFever | OrcaSlicer (x64) | x64 | EXE |
| Stefan Kueng | grepWin (x64) | x64 | MSI |
| Stellarium | Stellarium (x64) | x64 | EXE |
| SUSE | Rancher Desktop (x64) | x64 | MSI |
| Synology | Synology Drive Client (x64) | x64 | MSI |
| Sysprogs | SmarTTY | x86 | MSI |
| Tabular Editor ApS | Tabular Editor 2 (x86) | x86 | MSI |
| Tailscale | Tailscale (x64) | x64 | MSI |
| TeamSpeak Systems GmbH | TeamSpeak 3 Client (x64) | x64 | EXE |
| TeamViewer | TeamViewer (x64) | x64 | MSI |
| TeamViewer | TeamViewer Host (x64) | x64 | EXE |
| TGRMN Software | Bulk Rename Utility (x64) | x64 | EXE |
| The Document Foundation | LibreOffice (x64) | x64 | MSI |
| The GIMP Team | GIMP (x64) | x64 | EXE |
| The Git Development Community | Git for Windows (x64) | x64 | EXE |
| The R Foundation | R for Windows (x64) | x64 | EXE |
| The R Foundation | Rtools (x64) | x64 | EXE |
| The VirtualGL Project | TurboVNC (x64) | x64 | EXE |
| Thingamahoochie Software | WinMerge (x64) | x64 | EXE |
| TortoiseGit | TortoiseGit (x64) | x64 | MSI |
| TortoiseHg | TortoiseHg (x64) | x64 | MSI |
| TortoiseSVN | TortoiseSVN (x64) | x64 | MSI |
| TPGi | Colour Contrast Analyser (x64) | x64 | MSI |
| Typora | Typora (x64) | x64 | EXE |
| UltiMaker | UltiMaker Cura (x64) | x64 | MSI |
| Unity Technologies | Unity Hub (x64) | x64 | EXE |
| UrBackup | UrBackup Client (x64) | x64 | MSI |
| uvnc bvba | UltraVNC (x64) | x64 | EXE |
| VideoLAN | VLC Media Player (x64) | x64 | MSI |
| Vim | Vim (x64) | x64 | EXE |
| Voidtools | Everything (x64) | x64 | MSI |
| VSCodium | VSCodium (x64) | x64 | MSI |
| Webprofusion | Certify The Web (x64) | x64 | EXE |
| WIBU-SYSTEMS | CodeMeter Runtime Kit (x64) | x64 | EXE |
| win.rar GmbH | WinRAR (x64) | x64 | EXE |
| WinDirStat | WinDirStat (x64) | x64 | MSI |
| WireGuard | WireGuard (x64) | x64 | MSI |
| Wireshark Foundation | Wireshark (x64) | x64 | EXE |
| XnSoft | XnView MP (x64) | x64 | EXE |
| Yubico | Yubico Authenticator (x64) | x64 | MSI |
| Yubico | Yubico PIV Tool (x64) | x64 | MSI |
| Yubico | YubiKey Manager CLI (x64) | x64 | MSI |
| Zeal | Zeal (x64) | x64 | MSI |
| Zoom Video Communications | Zoom Workplace (x64) | x64 | MSI |
| Zulip | Zulip Desktop (x64) | x64 | MSI |
