# Changelog

All notable changes to Packr are documented here.

## [Unreleased]

### Offline Icon Pack Install
- **`IconPackService.InstallFromFileAsync`** — installs the pack from a local or
  UNC `icon-pack.zip`, for hosts whose proxy blocks the release download. The zip
  is copied into scratch before extraction so the extraction source never carries
  a zone identifier from the original location.
- **Beside-zip checksums** — a `checksums.txt` next to the zip is verified when
  present; a mismatch installs nothing, and no file at all installs with
  " Checksum not verified (no checksums.txt beside the zip)." appended to the
  outcome message.
- **`CompleteInstall`** — extraction, manifest read, name mapping, and the copy
  into `Definitions\Icons` are one shared path behind both the download and the
  from-file entry points; the mapping logic is not duplicated.
- **Settings page** — "Install from file..." button beside "Download icon pack",
  `OpenFileDialog` filtered to `*.zip`, same busy gate, status refresh, and
  snackbar handling as the download button.

### On-Existing Behavior
- **`OnExistingBehavior`** — new engine enum (`Skip`, `Overwrite`, `Fail`)
  baked into the generated MECM script at generation time.
  `MecmTarget.BuildPackageScript` and `MecmTarget.PackageAsync` take it as an
  optional parameter defaulting to `Skip`, which is the previous behavior
  exactly.
- **Skip** — the idempotent same-version path, plus the machine-readable line
  `[APP_PACKAGER_CONFLICT] existing-same-version app='<name>' version='<v>'`
  emitted after the deployment-type validation line and ahead of the idempotent
  icon apply. Same format as the PowerShell engine, so one parser serves both.
  The partial-prior-run guard on missing deployment types is unchanged.
- **Overwrite** — the same-version branch now sets `$replaceExisting` and falls
  into the version-change replacement path instead of exiting: one
  `Get-CMDeploymentType` inventory, one staging-name create, remove-old, rename
  sequence for both branches. The application object and its deployments
  survive. App-level fields and the icon re-apply on the way out.
- **Fail** — throws in-script naming the application and its version; nothing
  is written.
- **`Set-CMApplication` on an existing application** — re-applies Publisher
  alongside SoftwareVersion and Description.
- **`OnExistingConflict`** — marker constant with `Format` and `TryParse`, the
  seam the GUI reads the conflict off the output stream with.

### Package Run Conflicts
- **`PackagerExecutionService`** — `PackageAsync` and
  `PackageStagedManifestAsync` carry an `OnExistingBehavior` through to
  `MecmTarget`, and watch every reported line for the conflict marker.
  `PackagerExecutionResult.Conflict` carries the application name and version
  when one was seen.
- **`PackagesViewModel`** — a marker hit opens a themed Overwrite / Skip /
  Cancel remaining dialog with a "Do this for all remaining conflicts in this
  run" checkbox. Overwrite re-invokes that app's package phase with the
  behavior set; Skip marks the app Skipped and moves on; Cancel remaining stops
  the loop. The apply-to-all answer lives for the run only and is not
  persisted.

### Icon Pack
- **`IconPackService`** — resolves the latest `jasonulbright/app-packager-icons`
  release through the GitHub API over `CurlProcess`, so a resolved token raises
  the anonymous 60/hour budget. `icon-pack.zip` is verified against
  `checksums.txt` (sha256, bare file name, `*`-marker or two-space form) before
  anything is extracted; a mismatch installs nothing. Downloads land in a
  per-run scratch folder that is removed on every exit path.
- **Definition name mapping** — pack files are named by PowerShell packager
  name. Each manifest entry resolves to a definition base name by exact match
  first, then a normalized key (lower case, alphanumerics only, one trailing
  `x64`/`x86`/`both` removed, `teams-new` aliased to `teams`). A normalized key
  shared by several definitions copies one PNG per definition, which is how an
  arch-collapsed pack entry reaches both arch variants. An entry matching no
  definition is skipped. Live run against pack v1.1.0: 284 entries, 281
  matched, 2 fanned to a second variant (`dotnet8`, `msvcruntimes`), 3 skipped
  (`specexec-mitigations`, `vscode-system`, `vscode-user`).
- **Install target** — `Definitions\Icons\<definition base name>.png`, the path
  `IconExtractionService.StageExternalIcon` reads for `IconSource` `External`.
  `manifest.json` is kept beside the icons and carries the status row's pack
  version and icon count.
- **Failure surface** — every path returns `IconPackInstallResult` with a plain
  message: lookup failure, a release with no pack assets, a checksum mismatch, a
  manifest that will not parse. A `MinAppVersion` newer than the running
  assembly warns in the result message and still installs.
- **Settings page** — a Packager Icon Pack row showing installed pack version
  and icon count with a Download icon pack button, disabled while a download
  runs and refreshing the status line on completion.

### Application Icons
- **IconNative** — PE `RT_GROUP_ICON` directory parse selecting the largest
  frame of the module's first icon group, tie-broken on colour depth, emitted
  as a single-image `.ico`. A zero width/height byte reads as 256. The
  directory is read through `LoadLibraryEx`/`FindResource` rather than
  `ExtractAssociatedIcon`, which caps at 32 pixels and reports whatever size
  was requested.
- **Transient-load retry** — three PE load attempts at 0/400/1200 ms.
  `LoadLibraryEx` fails with no error while an on-access scan still holds a
  freshly written file, silently degrading the first probe of a just-downloaded
  payload to the 32px fallback.
- **MsiIconReader** — MSI `Icon` table extraction preferring the
  `ARPPRODUCTICON` stream, falling back to the table's first row, and re-reading
  an Icon-table row that holds a PE rather than an `.ico` through the PE path.
  Both COM objects are released explicitly; a live `Database` handle holds a
  write lock for the process lifetime.
- **`IconExtractionService.MaterializeIcon`** — 32px size floor, 512px MECM
  ceiling, and a 256 KB image cap enforced by PNG re-encode at 512/256/128/64
  until the file fits. A PNG-compressed 256px frame is copied out byte for byte
  because `System.Drawing.Icon` cannot decode that shape.
- **`AppDefinition.IconSource`** — `Installer`, `External`, or `None`; absent,
  blank, or unrecognized reads as `None`. `Installer` extracts from the staged
  payload the manifest names, `External` copies
  `Definitions\Icons\<definition name>.ico|png`. Staging writes `app-icon.ico`
  or `app-icon.png` into the content folder, records it as `StageManifest.Icon`,
  and the manifest's `FileHashes` cover it. Icon staging never throws.
- **MecmTarget** — the package script applies
  `Set-CMApplication -IconLocationFile` after every deployment type is in place.
  A missing or refused icon warns and continues.
- **IntunePublishService** — `win32LobApp.largeIcon` is set from the staged icon
  as a `mimeContent` (`image/x-icon`, `image/png`, or `image/jpeg` plus the
  base64 bytes) on the app body. An unresolvable icon leaves the property unset.
- **Definition tags** — `IconSource` set on 277 of 294 definitions
  (215 `Installer`, 34 `External`, 28 `None`). The remaining 17 carry no
  verdict and stay untagged.

### App Definitions
- **dbeaver** — Disable AI now sets DBeaver's documented
  `DBEAVER_AI_DISABLED=true` environment variable (Machine scope for a
  system install, User scope for a user install) instead of appending
  `-Dai.disabled=true` to `dbeaver.ini`. The variable survives upgrades
  and is naturally idempotent; per vendor documentation the permanent
  disable cannot be re-enabled from Preferences.

### Engine
- **Per-parameter value maps** — `InstallerParameter` takes a `ValueMap` and a
  `ValueMaps` table of named maps, so one setting substitutes different strings
  in different places: `{value}` and an unnamed `{setting:Key}` use `ValueMap`,
  while `{setting:Key:MapName}` selects a named map. A value the map does not
  list substitutes itself. One definition now covers a machine-wide and a
  per-user install without being duplicated.
- **`PostInstallScript`** — a definition can carry PowerShell that
  `WrapperGeneratorService.GenerateExeWrappers` appends to `install.ps1` behind
  `if ($proc.ExitCode -ne 0) { exit $proc.ExitCode }`, ending the wrapper at
  `exit 0`. `WhenSetting`/`WhenValue` gate it on an `InstallerSettings` value,
  and its `{setting:...}` tokens resolve at stage time. A gate naming no
  parameter suppresses the script.

### App Definitions
- **dbeaver** — `InstallScope` (System/User, default System) and `DisableAI`
  (toggle, default off). Scope switches install args between `/allusers /S`
  and `/S /currentuser`, uninstall args between `/allusers /S` and
  `/currentuser /S`, and the `Uninstall.exe` path and file detection path
  between `C:\Program Files\DBeaver` and `%LOCALAPPDATA%\DBeaver`. `DisableAI`
  appends `-Dai.disabled=true` after the `-vmargs` marker in `dbeaver.ini`,
  inserting the marker when absent and skipping a line already present; the
  installer replaces `dbeaver.ini` on every install, so the append re-runs
  each time.
- **InstallerSettings drive the staged command line** — staging resolves a
  definition's `InstallerSettings` into the effective install arguments and
  uses them for wrapper generation and the stage manifest, instead of
  reading `InstallArgs` verbatim. `StageAsync` takes an optional map of
  chosen values; a key that is absent falls back to the parameter's
  `DefaultValue`, so a definition resolves the same with or without a user
  pass. Definitions that spelled the same switches out twice —
  `mariadb-server`, `windowsadmincenter`, `sqlserver2022express` — now
  carry only the switches no parameter owns.
- **`{setting:Key}` substitution** — uninstall arguments, the uninstall
  command, and detection templates (registry key, file path and name,
  script body, and compound clauses) resolve `{setting:Key}` against the
  effective parameter value. `sqlserver2022express` uses it so a changed
  instance name moves the uninstall switch and the detection key with it.
  A token naming no parameter is left in place rather than emptied.
- **Payload SHA256 verification** — staging verifies the downloaded payload
  against a vendor-published digest before anything else reads it, next to
  the installer magic check. The expected value comes either from a pinned
  `PayloadSha256` on the definition, or from `VersionScraper.PayloadHashQuery`,
  which captures the hash out of the same feed the version came from so the
  two describe one build. The capture is a named `hash` regex group for
  text feeds and a JSON query path for JSON feeds. A cached payload is
  verified on every run, so a bad earlier download cannot stage later. A
  mismatch, a malformed expected digest, and a missing payload all fail the
  stage; no expected value is not a verification and passes. Restored on
  `teracopy`, `remotedesktopmanager`, and `go`, each confirmed against a
  fully downloaded payload.
- **Script and Compound detection in definitions** — a definition can now
  declare `Detection.Type` of `Script`, carrying a PowerShell body whose
  `{version}` and `{ProductCode}` placeholders are substituted at stage
  time, or `Compound`, carrying a `Connector` of `And`/`Or` over a `Clauses`
  array of registry and file sub-detections. Clauses resolve individually,
  so each gets the same substitution a standalone detection would.
  `GroupSizes` expresses the grouped shape `(run1 AND ...) OR (run2 AND ...)`
  as exactly two contiguous runs. Compound detections map to CM detection
  clause groups: only the second run is passed to `-GroupDetectionClauses`,
  because the cmdlet builds the expression left-associatively and
  parenthesizes the first run on its own. An AND-joined compound emits no
  connector, AND being the cmdlet default. Nesting a compound inside a
  compound is refused rather than silently flattened.
  Restored the detections that had been flattened to a single clause:
  `nomachine` and `nvda` (Compound Or across both registry views),
  `codemeterruntime` (Compound Or across both Program Files paths), and
  `anyburn`, `bulkrenameutility`, `ocenaudio`, `openshot`, `picpick`,
  `pidgin`, `teracopy` (ARP-scan detection scripts covering both views).
- **GitHub API authentication** — every request to `api.github.com`, from
  scrapers and from the downloader alike, carries an
  `Authorization: Bearer` header when a token is available. The token is
  resolved once per process from `PACKR_GITHUB_TOKEN`, then `GITHUB_TOKEN`,
  then `gh auth token`; with no token the requests stay anonymous. The
  header travels over a curl config on stdin, so the token is not part of
  the process arguments. Only the API host is tokened — release assets live
  on hosts that reject the header. This lifts a full-catalog sweep off the
  anonymous 60/hour budget, which was failing GitHub-sourced definitions on
  rate limiting alone.
- **`JsonQuery` honors `NoUserAgent`** — the flag was read by `HttpRegex`
  only, so a JSON feed that answers an error page to a browser User-Agent
  could not be scraped whatever the definition asked for.
- **HeadProbe scraper** — a new scraper for vendors that publish no version
  index and state no version on the download page. It walks a versioned
  download URL upward from a floor version, preferring the next patch, then
  the first patch of the next minor, then of the next major, and settles on
  the last version the host serves. `MaxVersionHops` bounds the request
  count. A floor the host no longer serves fails the scrape rather than
  reporting a version that is not published.
- **Max-by-version selection** — `HttpRegex` takes `SelectHighestVersion`,
  which collects every pattern match and returns the highest by version
  comparison instead of the first one on the page; `{version}` in
  `DownloadUrlQuery` is substituted with the selected version so the
  download URL comes from the same release. `JsonQuery` gains a
  `[max:field]` array accessor that selects the element whose field holds
  the highest version. Comparison handles 2-4+ dotted parts, alphabetic
  part suffixes (`6.90a`), build metadata (`21.0.12.1+1` outranks
  `21.0.12+10`), prerelease tails, and comma- or dash-separated parts.
- **Chained (multi-hop) scraping** — the new `ChainedHttpRegex` scraper
  walks an ordered list of hops. Each hop fetches a URL and applies a
  regex; the capture substitutes into the next hop's URL as `{capture}`
  (or `{captureN}` for an earlier hop), and the named groups `version`
  and `url` collect the results. Relative hop and download URLs resolve
  against the page they came from.
- **Dell catalog scraper** — the new `DellCatalog` scraper downloads
  `CatalogPC.cab`, expands the nested cab to its UTF-16 XML, and selects
  the Dell Command | Update Windows Universal Application WIN64 component
  by highest `vendorVersion`, then newest release date, then widest
  supported-model list.
- **MSIX provisioned-package support** — staging an `InstallerType: MSIX`
  definition reads the package identity (Name, Version, Publisher,
  architecture) from `AppxManifest.xml` inside the container, verifies the
  signature in `AppxSignature.p7x` and records the signer thumbprint, and
  surfaces all of it in the stage manifest under `MsixPackage`. Staging
  refuses an unsigned package, a signature that does not verify, and an
  architecture that disagrees with the definition.
- **MSIX wrappers** — `install.ps1` provisions device-wide via
  `Add-AppxProvisionedPackage -Online -SkipLicense` after checking the
  package against the thumbprint pinned at stage time; `uninstall.ps1`
  reads the identity back out of the package on the client and removes
  both the provisioning entry and every per-user registration. A per-user
  (`Add-AppxPackage`) variant is generated on request.
- **Bootstrapper `/layout` staging** — a definition can carry
  `BootstrapperLayout`, which makes staging download the vendor
  bootstrapper into a `bootstrapper` subfolder (the bundle refuses a
  layout target beneath the folder it runs from) and run it with
  `/quiet /layout <contentDir>`, so the package content is a complete
  offline install set instead of a bootstrapper that needs internet from
  every client. A layout root longer than the configured limit (120 chars)
  is refused up front, naming the length, rather than reaching the
  bundle's opaque exit 2002. Detection is derived from a designated MSI
  inside the layout — exact relative path or a pattern that must match
  exactly one file — whose ProductVersion is the kit version and whose
  ProductCode is the ARP key, so no probe install is needed. An existing
  complete layout is reused. Generated wrappers run the layout's own
  bootstrapper copy with `/quiet /installpath /features` to install and
  `/quiet /uninstall` to remove.
- **Extract-then-setup wrappers** — a definition can carry `ExtractSetup`,
  generating wrappers that extract a self-extracting payload to a
  spaceless drive-root path (setup rejects paths containing spaces), run
  the inner setup executable with the definition's install or uninstall
  args, and remove the extraction tree, reporting setup's exit code rather
  than the cleanup's. Argument tokenization keeps a quoted value with
  spaces as one argument.
- **Script detection rules** — `StageDetection` carries `ScriptLanguage`
  and `ScriptText`, and the MECM script builder passes them to
  `Add-CMScriptDeploymentType` as `-ScriptLanguage`/`-ScriptText` instead
  of a detection clause. MSIX staging generates a detection script that
  queries `Get-AppxProvisionedPackage -Online` by DisplayName and compares
  versions with `>=`. CM's native Appx deployment type is now reserved for
  manifests that explicitly ask for `MSIX` detection.

### Definitions
- **`omnissahorizonclient` resolves its download group** — the quarterly
  group code was pinned in the scraper URL. Two chained hops now read the
  code out of the Customer Connect product listing and the version and
  anonymous download URL out of that group's file details.
- **`mariadb-server` resolves its release series** — the LTS series was
  pinned. Two chained hops now take the highest series the REST index
  reports as Stable with Long Term Support, then the newest Windows point
  release within it.
- **`agentransack` captures its live download href** — the vendor path
  carries a per-release token that no template can express. A chained hop
  now takes the protocol-relative href off the download page and resolves
  it against that page.
- **`rvtools` walks the download host** — the version and URL were pinned
  to 4.8.1; the definition now uses the `HeadProbe` scraper from a 4.8.1
  floor.
- **`pdfstudioviewer` install path corrected** — the release year embedded
  in the install directory, uninstall command, detection path, detection
  file, and process name read 2026 against a published `ProductVersion` of
  2024.0.1, so all five pointed at a directory the install does not create.
- **`salesforcecli` version scrape restored** — the definition asks for
  `NoUserAgent`, which `JsonQuery` did not honor; the feed answers an HTML
  error page to a browser User-Agent, so the scrape returned no version.
- **`liberica-jdk21` picks the newest release** — the JsonQuery `[0]`
  selection returned 21.0.12+10 while 21.0.12.1+1 was newer; both the
  version and download URL now come from `[max:version]`.
- **46 definitions moved to max-by-version selection** — pwsafe, semeru
  (6), zulu (6), synologydriveclient, redshiftodbc, softerraldapbrowser,
  smartty, smathstudio, renderdoc, picpick, pidgin, gpg4win, hwmonitor,
  kdiff3, irfanview, dbvisualizer, geogebra, graphviz, iapdesktop,
  intunedebugtoolkit, teamspeak3client, tightvnc, tortoisehg, typora,
  urbackupclient, vagrant, veracrypt, wireguard, xnviewmp, yubicopivtool,
  kreya, krita, mysqlconnectornet, pdf24creator, pdfgear and nomachine no
  longer depend on the newest release appearing first on the vendor page.
  Every one live-tested; all return the same version they did before, and
  their download URLs answer.
- **`displaylink` works** — a two-hop crawl (download index → release node
  page) yields the version and the `exe_files` installer URL, replacing an
  index pattern that no longer matched and an empty DownloadUrl.
- **`ultravnc` is unpinned** — a three-hop crawl (release index → release
  page → jdownloads send stub) yields the version and the current mirror
  URL instead of the hardcoded `uvnc.eu/download/1800/` path.
- **`codemeterruntime` is unpinned** — a two-hop crawl reads the selected
  Windows 64-bit build from the download listing and follows it to the
  file page's `directDownload` link, so the file id and its `cHash` are no
  longer hardcoded.
- **`dellcommandupdate` works** — the definition uses the new
  `DellCatalog` scraper instead of a support-page regex that produced no
  download URL.
- **`ohmyposh` is functional** — the MSIX definition now stages, wraps, and
  detects end to end; verified live against the vendor's signed x64 package.
- **`windowsadk` / `windowspeaddon` express offline layouts** — both carry a
  `BootstrapperLayout` naming their bootstrapper URL and file, the feature
  set, the install root, and the feature MSI detection is anchored to
  (Deployment Tools and PE wims respectively). Version resolution verified
  live against both vendor links (10.1.26100.2454).
- **`sqlserver2022express` expresses its wrapper** — the definition carries
  the extract args, the extraction folder name (`SQLEXPR_Media`), and the
  inner setup path, and restores the instance name and sysadmin account
  choices as `InstallerSettings` entries defaulting to `SQLEXPRESS` and
  `BUILTIN\Administrators`.
- **177 definitions added (117 → 294)**, ported from app-packager's
  Enterprise App Catalog parity waves (releases 1.4.0.16–1.4.0.24).
  All deserialize cleanly with unique ids; version scrapers live-tested
  where cheap (~160 confirmed against vendors). Known gaps and pinned
  values are catalogued in MAINTENANCE.md. The four definitions that
  needed engine work before they could function (Dell Command Update,
  DisplayLink, Windows ADK/WinPE layout staging, SQL Server Express
  wrapper) all have it now.

### UI
- **First-run setup window** — a themed modal `SetupWindow` opens after the
  main window loads when no settings file exists or `FirstRunCompleted` is
  unset. Step one picks the environment (MECM only / MECM + Intune / Intune
  only), bound to the existing `DeploymentTarget` values and preselected
  from the stored one; step two shows only the fields that target needs —
  Site Code, Site Server, File Share Root, and Download Root for MECM,
  Tenant ID, Client ID, and Client Secret for Intune, the secret DPAPI-
  protected by `IntuneSecretProtector` with an empty box keeping the saved
  one. Save writes through `SettingsService`, reloads `SettingsViewModel`,
  and refreshes the action bar — no restart. A "Don't show this again"
  checkbox suppresses the window without configuring anything; Skip or
  closing without it leaves it due next launch. `FirstRunDecision` holds
  the decision and touches no WPF types; settings files written before the
  flag existed are stamped `FirstRunCompleted` on load, so upgrading users
  never see it.
- **Deployment-target-aware action bar** —
  `PackagesViewModel.RefreshDeploymentTargetState` applies the target from
  one place: at launch, after a Settings save, and after the setup window
  saves. The mapping itself is the WPF-free `DeploymentTargetActionState`.
  With target `IntuneOnly`, Check MECM is disabled (not hidden) with a
  tooltip naming the missing ConfigMgr site, Package reads Publish with a
  matching tooltip and skips the MECM/Intune dropdown, and the launch-time
  MECM pre-flight version query is skipped before it can open a provider
  connection, logging the skip. `MECM` and `MECMAndIntune` are unchanged.

### Fixed
- **Intune-only runs no longer trip the MECM gates** — `PackageAsync`
  required `SiteCode` regardless of Deployment Target, so target
  `IntuneOnly` could never start a run on a console-less workstation. The
  gate is now `DeploymentTargetGate.Validate`: MECM targets need Site Code
  and File Share Root, Intune targets need Tenant ID, Client ID, and a
  stored client secret, and each blocked run says which.
- `StagingService` resolves origin-relative download hrefs (leading `/`)
  against the scraper URL's origin instead of its directory.

### Project
- **MIT license added** — Packr is free software, free for everyone, permanently.
- **Docs reframed** — README and ROADMAP drop the commercial product framing (paid tiers, subscription validation, product-site branding). All technical content retained; the optional hosted version-data endpoint is now described as a free, opt-in convenience with local scraping as the default and fallback.
- **README rewritten for a general administrator audience** — plain-language "what it does / who it's for / quickstart" ahead of the technical sections; relationship to the PowerShell sibling app-packager stated, where features land first.
- **`tools/build-release.ps1`** — publishes a self-contained single-file win-x64 build and emits `Packr-<version>.zip` plus `checksums.txt` (SHA256, bare filenames) into `publish/`.
- **Version property** added to the UI project so release tooling has a single source for the version string.

### Parity
- Parity work with app-packager begins: features shipped there are being ported here, tracked per feature.

## [2.2.0-alpha1] - 2026-08-31

Feature parity with app-packager v1.4.0.14. Every ported behavior comes
from the live-tested PowerShell implementation; the multi-DT and
requirement-rule script generation was re-validated on the CM lab from
this engine's output.

### Engine
- **Stage manifest schema v3** — recursive FileHashes with write-time
  verification; DeploymentTypes, Requirements, CommandOverrides, and
  drop-to-package provenance fields; v2 manifests still read cleanly.
- **Deployment conditions** — condition template document (CPU
  architecture WQL, OS language built-in with PlatformType narrowing,
  VPN-connected script condition with configurable adapter patterns),
  global conditions get-or-created by name with a read-back guard, and
  requirement rules attached per deployment type. Requirements can also
  apply to a single-DT app via an explicit parameter.
- **Multi-deployment-type applications** — DeploymentTypes manifest
  entries create most-specific-first; version replaces stage the whole
  new set before removing the old; unchanged reruns validate every DT
  and exit idempotently. Duplicate-name/missing-suffix/missing-detection
  specs fail before anything is created.
- **Command overrides** — explicit override beats the manifest record;
  flat overrides are refused on multi-DT manifests.
- **Intune Win32 publishing** — IntuneWinAppUtil download with
  Authenticode/Microsoft-signer verification, .intunewin build and
  metadata/encryption-info reader, and the full Graph v1.0 publish flow
  on native HttpClient: create-or-update by display name, chunked
  block-blob upload, commit with file encryption info, committed
  version patch.
- **Drop-to-package analysis** — byte-signature installer
  identification (no 7z dependency), 19-engine silent-switch database,
  proposed fields marked Confirmed/Predicted with provenance, MSI
  identity authoritative, per-user ARP key routing, stage-manifest and
  definition graduation.
- **Provider host threading** — the generated MECM scripts accept a
  configured site server instead of assuming the local machine.
- Microsoft.Data.Sqlite 10.0.11 (clears the transitive SQLitePCLRaw
  advisory).

### UI
- **Config review reaches staging** — the values edited in the
  pre-stage configuration dialog are now passed to the staging call, so a
  `ReviewBeforeStage` definition stages with the reviewed settings rather
  than the definition's defaults.
- **Settings**: Deployment Target (MECM only / MECM + Intune / Intune
  only), Intune tenant/client/secret (DPAPI-protected at rest), Site
  Server host.
- **Deployment Conditions page**: per-app Architecture / OS languages /
  Network / Variant split / Commands grid plus editable condition
  names; selections persist and flow into packaging (variant splits
  mirror the PowerShell packagers' gating patterns).
- **Command override editor** with shipped-default watermarks and
  one-click revert; grid button reads Default/Modified.
- **Drop-to-package**: drag an .msi/.exe onto Packages, review every
  proposed field with Confirmed/Predicted badges, confirm-gated Stage,
  save-as-definition; staged drops package through the normal pipeline.
- **Intune-only runs** skip MECM entirely (no site, no share); per-run
  Intunewin/Intune-publish notes in the grid and live log.

### Tests
- Engine suite 41 → 182 (manifest v3, condition templates, spec
  resolver, script builder, Intune reader/rules/publish flow, signature
  scanner, silent switches, drop analysis).

## [2.1.0-alpha8] - 2026-04-05

### Engine - Uninstall Wrapper Fixes
- **NSIS uninstall matching** — wrapper now matches ARP entries by application display name (from definition) instead of download filename. Fixes Bitwarden, DBeaver, Opera, TeamViewer Host, Notepad++, WinRAR.
- **MSI uninstall via ProductCode** — wrapper reads ProductCode from stage-manifest.json first, falls back to MSI file path. Fixes Firefox/Thunderbird exit 1605 (MSI wrapper uninstall).
- `GenerateExeWrappers` takes new `applicationName` parameter for ARP matching.

### Definition Fixes (7 apps)
- git.json: InstallerFramework NativeEXE -> InnoSetup
- positron.json: InstallerFramework NativeEXE -> InnoSetup
- winscp.json: InstallerFramework NSIS -> InnoSetup
- vim.json: UninstallArgs `/S` -> `/VERYSILENT /SUPPRESSMSGBOXES /NORESTART`
- wireshark.json: InstallerFramework InnoSetup -> NSIS
- audacity.json: UninstallArgs added `/SP-`

### Lab Infrastructure
- CLIENT01 rebuilt: 150GB OS disk (was 60GB), fresh Windows 11 Enterprise
- `Deploy-HomeLab.ps1`: CLIENT01 OSDiskSize configurable in config.psd1, disk expansion step added
- `Deploy-HomeLab.ps1`: re-adds CLIENT01 to lab definition when removed (supports VM-only rebuild)
- Default Switch NIC required for CLIENT01 internet access (not auto-added by AutomatedLab deferred deploy)

## [2.1.0-alpha7] - 2026-04-04

### Engine — Installer Metadata Extraction (Foundation)
- **ArchiveExtractionService** — 7z.exe wrapper: extract, list, extract single file, identify installer framework from archive contents
- **InstallerIdentificationService** — auto-detect NSIS/InnoSetup/WixBurn/MSIX/ZIP from EXE binary via 7z inspection; extract detection-relevant metadata per framework
- **Per-framework manifest readers**: NativeMSI (COM), NSIS (product-info.json + primary EXE), InnoSetup ({app}/ + _is1 key), WiX Burn (inner MSI extraction), MSIX (AppxManifest.xml), ZIP (primary EXE FileVersion)
- **Automated detection derivation** — `BuildDetection` falls back to `InstallerMetadata` when definition's `{ProductCode}` can't be resolved. Eliminates manual detection rule authoring for most installer types.
- **InstallerFramework field** on `AppDefinition` — all 116 definitions populated (NativeMSI, NSIS, InnoSetup, WixBurn, InstallShield, Squirrel, MSIX, ZIP, NativeEXE, ODT)
- **InstallerParameter model** — schema-driven per-app settings with ControlType (Toggle/Dropdown/Text/Number), CommandFragment, Group, Tooltip. Framework for plugin-style installer parameter UI.

### Engine — Deployment Automation
- **MSIX deployment type** in MecmTarget — `Add-CMWindowsAppxDeploymentType` for Teams/Slack. CM handles detection natively from MSIX manifest.
- **Per-user deployment support** — `InstallContext` field (Machine/User) on AppDefinition, flows to `InstallationBehaviorType InstallForUser` on deployment type. LogonRequirementType correctly omitted for user installs per CM docs.
- **Auto-distribute** — `MecmTarget.DistributeAsync()` with `-DistributionPointGroupName`
- **Auto-deploy** — `MecmTarget.DeployAsync()` with configurable purpose, maintenance window override
- **Content share path validation** — pre-check UNC accessibility before packaging
- **SanitizeFolderName** — strips trailing dots, commas, special chars from vendor folder names (prevents CM content path mismatches)

### Engine — Services
- **VersionHistoryService** — SQLite database tracking every operation (CheckLatest, Stage, Package, Distribute, Deploy). Per-app history, latest-per-app view, time range queries.
- **BareStaging** — `IDeploymentTarget` that copies content to network share without creating CM Application. For PSADT prep, Intune prep, manual workflows.
- **PsadtWrapper** — generates Deploy-Application.ps1 with vendor/app metadata, process close list, PSADT best practices. Creates separate [Interactive] CM Application, not second DT.
- **BuildDetection version sanitization** — `+` and `-` converted to `.` for Version-type RegistryKeyValue detection values
- **BuildDetection {version} substitution** — in RegistryKeyValue and RegistryKey key templates

### UI
- **Add New App wizard** — 5-step page (Basic Info → Version Source → Download → Install/Uninstall → Review & Save) with CardExpander sections, test scrape button, ComboBox selectors, tooltips on every field
- **ContentDialogHost** added to MainWindow — enables ContentDialog overlays throughout the app
- **Comment field optional** — Package without comment shows ContentDialog (Continue/Cancel) instead of blocking error
- **NumberBox** for runtime settings — replaces TextBox with spin buttons, min/max, tooltips
- **StepVisibilityConverter** — drives wizard step switching

### Regression Testing — 90 of 116 Signed Off
- Full pipeline validated on CM01 + CLIENT01: Check Latest → Check MECM → Stage → Package → Distribute → Deploy → Install → Detect
- 11 deferred (install/distribution issues), 13 excluded (architecture changes), 2 vendor-gated
- Detection fixes: 42 definitions corrected across 4 validation rounds
- See REGRESSION-STATUS.md for per-app validation table

## [2.1.0-alpha6] - 2026-04-02

### Engine — Detection Fixes
- Disk logging: per-session log files in `%LOCALAPPDATA%\SRLPackr\Logs\`
- Status labels realigned to workflow (Latest retrieved, Staged, Packaged, etc.)
- StatusToBrushConverter default changed from Transparent to Gray

## [2.1.0-alpha5] - 2026-04-02

### App Definitions — 117 of 117 validated
- **Tableau Desktop, Prep Builder, Reader (3)**: HttpRegex version from release notes, `tssoftwareregistered`/`tssoftware` CDN downloads, `replace:.:-` for URL hyphens, `DisplayVersionOverride` for dotted display
- Tableau WAF bypass: direct CDN download URLs work, no Akamai blocking

### Roadmap
- Added v2.2 WebView2 Manual Download Panel spec for vendor-gated apps (Citrix LTSR, VMware Workstation)
- Embedded `CoreWebView2.DownloadStarting` interception for TOS/auth-gated downloads
- `ManualDownload`, `ManualDownloadUrl`, `ManualDownloadHint` definition fields planned

## [2.1.0-alpha4] - 2026-04-02

### Engine — New Scrapers & Features
- **CdnCabScraper** — Office CDN version discovery via CAB/VersionDescriptor.xml extraction. Supports MEC, Current, and SemiAnnual channels with `I640Version`/`I320Version` attributes.
- **ODT staging pipeline** — full M365 staging: download ODT setup.exe, generate download/install/uninstall XML configs, run `setup.exe /download` for offline Office source, generate ODT-specific wrappers.
- **OdtConfig** on `AppDefinition` — ProductIds, UninstallProductId, Channel, ExcludeApps fields for M365 ODT configuration.
- **MsixManifest** pattern in `FileVersionScraper` — extracts `Package.Identity.Version` from AppxManifest.xml inside MSIX packages (Teams).
- **LAST_MODIFIED** pattern in `HttpRegexScraper` — HEAD request extracts Last-Modified header, converts to date-based version `YYYY.M.D` (Sysinternals).
- **ZIP extraction in staging** — downloaded ZIP files are automatically extracted to find the MSI/EXE installer inside. Original ZIP cleaned up after extraction (TeamViewer, paint.net).
- **MecmTarget recursive directory copy** — `Directory.GetFiles` with `SearchOption.AllDirectories` preserves subdirectory structure when copying to network share (M365 Office/Data/).

### App Definitions — 114 of 117 validated end-to-end
- **M365 Apps/Project/Visio x64+x86 (6)**: CdnCab scraper + ODT staging pipeline, full end-to-end
- **MSVC Runtimes x64+x86 (2)**: FileVersion scraper, separate apps, file-based detection on msvcp140.dll
- **CutePDF Writer (1)**: FileVersion scraper
- **Microsoft Teams (1)**: MsixManifest version extraction from MSIX
- **Sysinternals Suite (1)**: LAST_MODIFIED header → date version
- **TeamViewer (1)**: HttpRegex page scrape + ZIP→MSI extraction
- **paint.net (1)**: JsonQuery GitHub API + ZIP→MSI extraction

### Bug Fixes
- MecmTarget only copied top-level files, missing M365 Office/Data/ subdirectories
- TeamViewer EXE installer has no version info — reverted to ZIP/MSI variant with proper ARP detection

## [2.1.0-alpha3] - 2026-04-01

### Engine — New Scrapers & Features
- **JsonQueryScraper** — path-based query language for JSON APIs (`[?key=value].field`, array index, negation, AND conditions). Replaces need for dedicated scrapers per API format.
- **FileVersionScraper** — downloads installer to temp cache, reads EXE FileVersionInfo or MSI ProductVersion. Stage reuses cached download to avoid re-downloading.
- **DownloadUrlQuery** — extracts download URL from the same API/page response in a single request. Supported on both `JsonQueryScraper` and `HttpRegexScraper`. Relative URLs auto-prefixed with scraper base path.
- **NoUserAgent** flag on `VersionScraperConfig` — omits User-Agent header for vendors that block browser UA from server IPs (Adobe).
- **DisplayVersionOverride** on `AppDefinition` — decouples UI display version from URL transform version. Supports `{version}` placeholder for raw version.
- **regex_replace** transform — regex-based version string manipulation (Adoptium LTS suffix, Corretto 5→4 part version).
- **RegistryKey** detection type — key-existence detection with `{version}` substitution in key path (ASP.NET Hosting Bundle).
- **`{version}` substitution in File detection paths** — version-specific install directories (dotnet runtimes).
- **Raw version fallback for File detection** — uses raw (dotted) version for `ExpectedValue` when PropertyType is Version, preventing invalid version format errors.

### Engine — Performance & Reliability
- **Stage version reuse** — Stage passes `knownVersion` from Check Latest to skip redundant API calls (GitHub rate limiting). Re-scrapes only when `VersionTransform` exists and raw version is needed.
- **REDIR: mode optimization** — `--max-filesize 1` replaces `--output NUL` for fast redirect URL resolution without downloading the response body. Accepts exit code 63 as success.
- **InstallerFileName `{display_version}` resolution** — both `{version}` and `{display_version}` placeholders supported in InstallerFileName override.

### UI
- **Status column labels** — workflow-aligned labels: Latest retrieved, Not in MECM, Update available, Up to date, Current newer, Staged, Packaged, Error variants.
- **Status column visibility** — `StatusToBrushConverter` updated: all statuses render visible text. Default fallback changed from `Transparent` to `Gray`.
- **Checkbox behavior** — Check Latest unchecks on error. Check MECM unchecks if version equals or newer. Stage unchecks on error. Package unchecks on both error and success.

### App Definitions — 101 of 117 validated end-to-end
- **JsonQuery batch (13)**: dotnet 8/9/10, ASP.NET 8, Edge, WebView2, Node.js, PostgreSQL 13–17, Python
- **GitHub API batch (19)**: Audacity, Bitwarden, Brave, DBeaver, draw.io, Git, Greenshot, mRemoteNG, Notepad++, Obsidian, Positron, PowerShell 7/LTS, PowerToys, RStudio, ShareX, Vim, WinDirStat, WinMerge
- **Adoptium Temurin (14)**: JDK/JRE 8/11/17/21/25 (x64+x86 variants)
- **Amazon Corretto (7)**: JDK 8/11/17/21/25 (x64+x86 variants)
- **FileVersion (8)**: AnyDesk, Malwarebytes, MSOLEDB 19, Postman, SSMS 22, TeamViewer Host, VS 2026 Enterprise/Community
- **REDIRECT (3)**: Foxit PDF Reader, TortoiseGit, Zoom Workplace
- **Citrix (1)**: Workspace CR (XML catalog regex)
- **DYNAMIC (11)**: Adobe Reader DC, pgAdmin 4, Power BI Desktop, PyCharm, R, TortoiseSVN, VMware Tools, WinSCP, Wireshark, XenCenter, XenServer VM Tools
- **7-Zip** migrated from 7-zip.org HTML to GitHub Releases API

### Bug Fixes
- ASP.NET 8 / dotnet detection: `RegistryKey` type and `{version}` in File paths now handled in `BuildDetection`
- Node.js double-v URL: `v{version}` → `v{display_version}` when `strip_prefix:v` transform active
- Corretto download 403: Stage re-scrapes when `VersionTransform` exists to preserve raw version for URL
- Malwarebytes detection: `PropertyType` changed from `String` to `Version` for `GreaterEquals` operator
- CMName mismatches: dotnet 8/9/10, ASP.NET 8 CMNames updated to match engine app names
- Stale runtime DLLs: `hostfxr.dll`/`coreclr.dll` in deploy folder caused framework-dependent app to search locally instead of `Program Files\dotnet`
- mRemoteNG download 404: asset filename includes build number not in tag; switched to `releases/latest/download/`
- Brave InstallerFileName: `{display_version}` placeholder resolution added

### Deploy Script
- Allowlist-based definition filtering — only tested definitions deployed to CM01
- Stale runtime DLL cleanup guard
- `JSON_QUERY` removed from exclusion filter (definitions now use real scraper configs)

## [2.1.0-alpha2] - 2026-03-31

- 24 apps validated end-to-end on CM01 (HttpRegex scraper)
- VersionTransform: insert_dot, replace, strip_prefix
- InstallerFileName override for fwlink/query-string URLs
- REDIR: pattern prefix for redirect URL extraction
- MSI Manufacturer fallback for invalid characters
- Scraper fetching switched from PS5.1 IWR to curl.exe

## [2.1.0-alpha1] - 2026-03-31

- ApplicationPackagerEngine class library (IVersionScraper, HttpRegexScraper, ScraperRegistry)
- Core services: DownloadService, MsiInspectionService, WrapperGeneratorService, ManifestService, StagingService
- MecmTarget: PowerShell 5.1 subprocess for CM cmdlets
- Chrome JSON definition as proof of concept
- UI routing: JSON apps → engine DLL, PS1 apps → powershell.exe

## [2.0.0-alpha] - 2026-03-30

- Initial WPF Fluent UI with .NET 10, WPF-UI 4.2.0, CommunityToolkit.Mvvm
- Packages page with DataGrid, Check Latest/MECM/Stage/Package operations
- Applications page with vendor-grouped expanders and tri-state checkboxes
- Settings page: Appearance, Organization, MECM Site, DT defaults
- MECM connection test, per-app timestamps, priority auto-check
- Window state persistence, keyboard shortcuts, CSV export
