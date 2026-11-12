# Payload verification

This page is generated from the shipped definitions. A test fails when it differs from them. It lists which definitions verify the file that they download, and how.

## What the stage checks

- **Always:** the file type of the download. An EXE must start with `MZ`, an MSI or MSP with the OLE header, a ZIP with `PK`, an MSIX must be a valid package. This finds an HTML page that a vendor endpoint returned in place of the installer. It does not find a replaced or tampered file.
- **When a SHA-256 is known:** the stage compares the file with it and refuses a mismatch. A SHA-256 is known when the definition pins it (`PayloadSha256`), the version feed publishes it (`VersionScraper.PayloadHashQuery`) or a vendor checksum file lists it (`PayloadSha256Url`).
- **When the definition sets `RequirePayloadHash`:** a missing SHA-256 refuses the stage.
- **When the definition sets `RequireValidSignature`:** the download must carry a valid Authenticode signature. An installer from a local source folder is always checked.
- **When no SHA-256 is known:** the stage run log says `Payload SHA256 NOT verified`, and the file passes after the file-type check.

## Counts

| Item | Definitions |
|---|---|
| Shipped definitions | 323 |
| With a pinned SHA-256 (`PayloadSha256`) | 0 |
| With a SHA-256 from the version feed (`PayloadHashQuery`) | 6 |
| With a SHA-256 from a checksum file (`PayloadSha256Url`) | 12 |
| With any SHA-256 source | 18 |
| That require a SHA-256 (`RequirePayloadHash`) | 3 |
| That require a valid signature (`RequireValidSignature`) | 4 |
| With no SHA-256 source and `RequireValidSignature` not set | 301 |

The counts describe the settings of the definitions. An installer from a local source folder is always checked for a valid signature; the last count does not include that check.

## Definitions with a SHA-256 source

| Definition | Pinned | Version feed | Checksum file | Required |
|---|---|---|---|---|
| `7zip.json` |  | Yes |  | Yes |
| `anypointstudio.json` |  | Yes |  | Yes |
| `citrixworkspace-ltsr-x86.json` |  | Yes |  | Yes |
| `go.json` |  | Yes |  |  |
| `ms-openjdk17-exe-user.json` |  |  | Yes |  |
| `ms-openjdk17-exe.json` |  |  | Yes |  |
| `ms-openjdk17-msi-user.json` |  |  | Yes |  |
| `ms-openjdk17-msi.json` |  |  | Yes |  |
| `ms-openjdk21-exe-user.json` |  |  | Yes |  |
| `ms-openjdk21-exe.json` |  |  | Yes |  |
| `ms-openjdk21-msi-user.json` |  |  | Yes |  |
| `ms-openjdk21-msi.json` |  |  | Yes |  |
| `ms-openjdk25-exe-user.json` |  |  | Yes |  |
| `ms-openjdk25-exe.json` |  |  | Yes |  |
| `ms-openjdk25-msi-user.json` |  |  | Yes |  |
| `ms-openjdk25-msi.json` |  |  | Yes |  |
| `remotedesktopmanager.json` |  | Yes |  |  |
| `teracopy.json` |  | Yes |  |  |

## Definitions that require a valid signature

- `cutepdfwriter.json`
- `tableaudesktop.json`
- `tableauprep.json`
- `tableaureader.json`
