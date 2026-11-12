# Import of vendor definitions from a URL

Status: specification, not yet built. Date: 2026-11-12.

## Summary

A software vendor can publish a **feed**: a JSON document that holds application definitions in the definition format of App Packager Desktop. An administrator enters the HTTPS address of the feed. The program fetches the document, validates it, shows everything that the import would do, and stores the definitions only after the administrator confirms. The definitions are stored apart from the shipped and the user definitions, and each one is labelled with its vendor and its source address.

Three rules shape the design:

1. **Nothing happens silently.** There is no import without a full preview and a confirmation, and no update without a diff and a confirmation.
2. **The document is data, not trust.** A fetched document is "not verified" unless the administrator pinned its SHA-256 or pinned the code-signing publisher of the vendor. A definition that carries code is blocked.
3. **A secret never leaves the administrator's computer.** An authenticated feed uses a credential that is kept in the Windows Credential Manager, is sent only to the host that the administrator entered, and is never written to a file, a log, a report, a package or an endpoint.

An authenticated feed is an addendum to this specification (see "Authenticated feeds"). It does not change rules 1 to 3.

## What exists today

The program has no import of definitions from a URL. This was checked in the code (the loaders, the download service, the scrapers, the update service, the icon pack and the bundle import). These features are near it and stay as they are:

| Feature | What it does | Difference |
|---|---|---|
| Add from web | A wizard: the administrator enters a version source and a download address, and the program writes one new definition. | The result is a definition that the program wrote from the administrator's input, not a document that a vendor wrote. |
| Add from file | Reads an installer file on the computer and builds one definition. | Same. |
| Bundle import (Workbench) | Imports a `bundle.zip` of one application with its profiles from a file. | A file, not an address. It is checked against hashes that are inside the bundle, so it proves integrity and not origin. |
| Icon pack download | Downloads icons into the `Icons` folder of the user definitions. | Icons only; no definition. |
| User definitions folder | A `*.json` file in this folder is a definition. A user definition with the `Id` of a shipped definition replaces it. | There is no label for the origin, and the loader does not validate the values of the fields. |

The loaders read the shipped folder and the user folder. This specification adds a third folder (see "Where the definitions are stored"). **The validation in this specification is new code.** The existing loader does not check the values of fields (`docs/reference/definition-format.md`), and the format defines no grammar for `Id`.

## Terms

| Term | Meaning |
|---|---|
| Feed | The JSON document at the address. It holds the vendor name and a list of definitions. |
| Vendor definition | One definition of a feed, in the existing definition format. |
| Source record | The record that the program writes beside the stored definitions of a feed: address, time, digest, trust state, signer. |
| Trust state | `Pinned digest`, `Signed by pinned publisher`, or `Not verified`. |
| Credential entry | The stored secret and the host that it is bound to (authenticated feeds only). |
| Code in a definition | Text that runs: a script, a command, a step, a wrapper or a bootstrapper (see "Code in a definition"). |

## Feed document

```json
{
  "FeedFormat": 1,
  "Vendor": "Contoso Software",
  "FeedId": "contoso-software",
  "Updated": "2026-11-12T08:00:00Z",
  "Definitions": [ { "Id": "contoso-agent", "...": "..." } ]
}
```

- `FeedFormat` is 1. Another value is refused with the message that the program is too old or too new for the document.
- `FeedId` is 3 to 64 characters of `a-z`, `0-9` and `-`, and is not a Windows device name (`con`, `nul`, `aux`, `prn`, `com1` and the others). It names the folder of the stored definitions. The administrator confirms or changes it in the preview, because two vendors can use the same id. A document without `FeedId` (or a document that is one definition object without the envelope) gets no id from the document: the preview asks for the vendor name and the `FeedId`.
- `Definitions` holds the definitions.
- **An update matches on the stored feed** (the feed that the administrator selected), never on the `FeedId` in the new document.

## Limits of the fetch

| Rule | Value |
|---|---|
| Scheme | HTTPS only. `http:`, `file:`, `ftp:` and every other scheme is refused. A redirect to HTTP is refused. |
| Redirects | The program follows redirects itself (see "How redirects are followed"): at most 5, each target checked again (scheme, host, port). An address in a private range is allowed, because feeds can be inside a company; the preview names the host. |
| Size | 2 MiB for the document, read as a stream and cut off at the limit. |
| Time | 15 seconds to connect and 60 seconds in all, **including** the waits of a retry. |
| Content | UTF-8 text. The content type must be `application/json` or `text/plain`; a page of HTML is refused with its type named. |
| Certificate | The certificate chain and the host name are checked against the Windows store, and the revocation of the certificate is checked (the default online check of Windows). There is no option to ignore an error. |
| Request | A generic `User-Agent` (product and version), no cookies, no referrer. The proxy settings of Windows apply. |
| Document | At most 200 definitions, at most 64 levels of depth, at most 4 MiB after parse. |

A failure of any rule stops the fetch. Nothing is written.

### How redirects are followed

The feed fetch uses `HttpClient` with `AllowAutoRedirect = false` and a loop in the program. The automatic redirect of .NET removes the `Authorization` header on every redirect and keeps custom headers on a redirect to any host, and a client certificate on the handler is offered to every TLS connection that the handler opens. So the program does it itself. At each hop the loop:

1. checks the scheme (HTTPS), the host and the port of the target against the bound credential entry (if there is one);
2. sends a request to the **bound host** with the credential;
3. sends a request to **any other host** through a second, anonymous handler: no certificate, no default credentials, no cookies, none of the headers of the secret;
4. counts the hops and stops at the limit.

The repository already follows redirects by hand in the Admin Service client and in the vulnerability transport; the feed fetch uses the same pattern.

## Validation

The document is parsed and checked by new code, strictly:

1. **Duplicate property names are refused** (`AllowDuplicateProperties = false`), because the loaders of the program disagree about which of two equal names wins (the first for the Workbench identity, the last for the catalog). The import stores a **re-serialized copy of the validated model**, never the bytes of the vendor.
2. Every definition passes the definition loader and these checks.
3. A property that the format does not know is refused and named. The names `Schema` and `Acquisition` (which the shipped files carry) are allowed. The names `ImportedFrom` and `ImportedVendor` are refused (see "Where the definitions are stored").
4. `Id` matches `^[A-Za-z0-9._-]{1,64}$`, is not made of dots only, is not a Windows device name (`con`, `nul`, `com1` and the others), and does not end in a dot or a space. It is unique in the document. The file name of a stored definition is built from the validated `Id`, never from another field.
5. `Application`, `Vendor` and `CMName` are plain names: no path separator, no `..`, not rooted, no drive colon, no device name, at most 100 characters. `Architecture` is exactly `x64`, `x86` or `ARM64`. The stage builds a folder from `Application` and `Architecture` today without checking that it stays under the download root; the first release of the import also changes those call sites to combine the folder under the download root with a sanitized name, so that a document that passed no check cannot write elsewhere.
6. Every address in a definition is `https:`. The check names every field that holds an address: the download address, the version source, the hop addresses of a chained scraper (`Hops[].Url`), `ServedUrlTemplate`, `BootstrapperUrl`, `PayloadVariants[].DownloadUrl`, the checksum file, `SecurityAdvisoryUrl` and `VendorUrl`. A UNC path, a local path, a `file:` address and an address with user information (`user:password@host`) are refused.
7. The hosts of all addresses of a definition are listed for the preview. An address that a query finds at run time (`DownloadUrlQuery`) cannot be listed; the preview says that such hosts are not known before Check Latest. A hop that puts a value captured from one host into the address of a **different** host, in the host, the path or the query, is **blocked** (see "Imported definitions run with no ambient credentials"). A relative hop is resolved at run time and must stay on the host of the hop before it.
8. `{version}` and other tokens that go into a script are checked for the characters that the program already allows in a version before they are used.

The result of validation is a list of findings (blocking, warning, information). A blocking finding keeps the definition out of the import. The administrator can import the other definitions of the document.

## Code in a definition

A definition **carries code** when it holds text that runs on an endpoint or on the administrator's computer:

| Where | Examples |
|---|---|
| A script | A script detection (`Detection.ScriptText`), a script requirement or condition, a post-install script (`PostInstallScript`), an install or uninstall script, a wrapper. |
| A command | `InstallArgs` with `ExpandInstallArgs`, `UninstallCommand` (also in `PayloadVariants[]`), `InstallerSettings[].CommandFragment`, `InstallerSettings[].UninstallFragment`, `ExtractArgs`. |
| A program that runs on the administrator's computer | `BootstrapperLayout` and a bootstrapper that Stage downloads and runs. |
| A path to a local file | `LocalInstaller`, `LocalSource`, `SideFiles` (the content of the administrator's own files would go into a package). |

The list of properties is made from the definition format when the check is built, and a test fails when the format gets a property that runs code and that the list does not name.

1. A definition that carries code is **blocked** from the import, in the first release of the import and in every trust state, also `Pinned digest` and `Signed by pinned publisher`. A pin proves where the document came from; it does not say that its code is safe. The preview shows the block and the reason. There is no unblock in the first release.
2. A later release adds an **unblock for a script and for a command** (not for the paths to local files and not for a bootstrapper that runs on the administrator's computer, which stay blocked). Many real definitions have an uninstall command, so the unblock of commands is needed for them to import at all. The unblock is for one definition. The window shows the full text of every script of the definition, the SHA-256 of each, and requires a confirmation that names the definition.
3. The stored digest of an unblocked script covers the script text **and every setting that the script uses** (`{setting:Key}`, its default, its value maps and its options). An update that changes any of them blocks the definition again. An update that changes nothing in them keeps the unblock.
4. A script that was unblocked is a script like any other for the signing policy of the program (sign, require signature). The import does not sign or trust it.
5. Commands (the second row) are always shown in full in the preview, because they run as the system account on endpoints.

A payload that Stage runs on the administrator's computer has the same access as the user to the credential store. That is why a bootstrapper is blocked, and why this specification does not rely on the store to stop a program that runs as the user.

## Preview

Before anything is written, the program shows one window with:

1. The source: the address, the final address after redirects, the time, the size, the SHA-256 of the document, the vendor name, and the trust state (see "Trust").
2. For each definition: `Id`, name, vendor, installer type, the hosts of every address, whether the payload has a SHA-256 source (`PayloadSha256`, `PayloadHashQuery`, `PayloadSha256Url`) and whether the definition requires one (`RequirePayloadHash`, `RequireValidSignature`), the detection type, the install and uninstall commands, and the code findings.
3. The conflicts: a definition whose `Id` is in the shipped definitions, in the user definitions, **or in another feed**. The default is to skip it. The administrator can choose to replace a shipped definition for each `Id`, with a confirmation that names what is replaced. A definition of another feed is never replaced; it is skipped, or the administrator changes the `FeedId` of this import in the window, so that it goes into a folder of its own (two feeds can then hold the same `Id`, and the second one is shown as a conflict that stays skipped until the first is removed).
4. The findings of validation.
5. A button to view the whole text of a definition, and the buttons **Import** and **Cancel**.

The preview needs no network after the fetch. **Cancel** writes nothing.

## Trust

A fetched document proves nothing about its origin by itself. The program offers two controls and a state for no control.

| Trust state | How the administrator gets it | What the program checks |
|---|---|---|
| `Pinned digest` | Enters the expected SHA-256 of the document, which the vendor publishes on another channel. | The SHA-256 of the received bytes equals the pinned value. |
| `Signed by pinned publisher` | Enters the expected publisher of the vendor (the subject and the issuer, or the public key, of the code-signing certificate). | A detached signature of the document (a `.p7s` file at the same address with `.p7s` added, on the same host) is valid for the exact bytes, and the signer is the pinned one (see below). |
| `Not verified` | Nothing. | Nothing. |

Rules:

1. **Wording.** The state `Not verified` uses the same wording as the payload check of the stage. The check says `Payload SHA256 NOT verified`; the import says `Definition document NOT verified`. The preview, the source record, the grid label and the log use it. The program never says "trusted", "safe" or "verified" for a document without a pin, and never for an installer that a definition names (the payload rules in [Payload verification](../reference/payload-verification.md) apply to the installer, whatever the trust of the document).
2. An import in the state `Not verified` needs a confirmation that names the state. The confirmation is asked again for each import and each update; it is never remembered.
3. A pin that does not match stops the import. It does not fall back to `Not verified`.
4. The pins are stored in the feed settings (not secret). A pinned digest stops every update, because a new document has a new digest: the update shows the new digest, and the administrator pins it after checking it on the vendor's other channel.
5. The trust of the document does not extend to code (see "Code in a definition") or to the installers.

How the signature is checked:

- The check uses `SignedCms` (a detached CMS signature). `SignedCms.CheckSignature` checks the chain at the current time. When the signature has an RFC 3161 time stamp, the program uses its time as the verification time of the chain **only after it checked the time stamp**: the token must be valid for this signature (`Rfc3161TimestampToken.VerifySignatureForSignerInfo`, so its message imprint matches the signature of the signer), and the certificate of the time stamping authority must chain to a trusted root and carry the time-stamping usage (`1.3.6.1.5.5.7.3.8`). An unchecked time stamp is ignored, because anyone who has the key of an expired certificate can attach any time to a signature.
- The certificate must have the code-signing key usage (`1.3.6.1.5.5.7.3.3`). The chain must reach a trusted root, and the revocation mode is the online check of Windows.
- The pin is the subject and the issuer, or the public key, and not the thumbprint, so that a renewal of the certificate by the vendor does not break the pin.
- A document whose `Updated` is older than the stored one is refused as a replay of an older signed document, unless the administrator confirms it.
- A feed at a **signed expiring address** (method 6) has no signature file (the signature of the address covers its path). Its trust is a pinned digest or `Not verified`.

## Where the definitions are stored

A third definition folder, apart from the shipped and the user folders:

```
<data root>\ImportedDefinitions\<FeedId>\
    <Id>.json          (one file for each imported definition)
    source.json        (the source record; written last)
```

- The loader reads the folder after the shipped folder and before the user folder. A user definition with the same `Id` therefore wins over an imported one. An imported definition with the `Id` of a shipped definition replaces it only when the administrator confirmed that `Id` in the preview. The three loaders that enumerate definitions (the catalog loader, the Workbench discovery and the command-line build) all read the third root, and a new origin, **Imported**, marks these definitions. They never carry the origin **Catalog**, so the program never shows one as a shipped definition.
- A file is written to a temporary name and moved into place. The folder of a feed is built in a staging folder and swapped in, with `source.json` written last, so a failure leaves the earlier state and no folder whose source record disagrees with its files. The swap is two renames, so at start the program recovers: when the folder of a feed is missing and its old copy exists, the old copy is restored.
- `source.json` holds: the final address (with the query removed), the vendor, the `FeedId`, the time of the fetch, the SHA-256 and the size of the document, the trust state, the pinned digest or the pinned publisher, the signer that was found, the `Id` list, the replaced shipped `Id` list, and the digests of the unblocked scripts. It holds no secret. It has no `Id` and must never be read as a definition.
- **The origin comes from the folder and from `source.json`**, not from a property of the definition. A feed cannot set its own label, and a copy of a file into the user folder does not carry the label. The grid shows the origin in a column ("Imported: Contoso Software"), and the details show the source address and the trust state.
- When an imported definition replaces a shipped `Id`, its origin changes from Catalog to Imported. The Workbench state of the shipped application (its profiles, keyed `catalog:<Id>`) stays with the shipped definition and does not apply to the imported one; the preview says so for each replaced `Id`.
- **Remove feed** deletes the folder and the settings of the feed after a confirmation that names the count of definitions. Stages that already exist stay. The page of the feeds has one **Remove** action with a choice: the credential only (the definitions stay, and cannot be updated without the feed settings), or the feed with its definitions.

## Update of a feed

1. An update is **manual**: the button **Check for update** on a feed. There is no timer, no check at start, and One Click does not fetch feeds.
2. The update fetches to a temporary place with the same limits and the same trust rules, and shows a **diff** against the stored definitions: added and removed definitions, changed properties of each definition, and in particular a changed address host, a changed hash source, a changed requirement for a hash or a signature, and any new or changed code.
3. Nothing is written until the administrator confirms the diff. The confirmation names the trust state; an update in the state `Not verified` is confirmed again each time.
4. A feed whose pin no longer matches stops with the message and shows the new digest or signer.
5. A removed definition is not deleted without the confirmation. A definition that the update removes stays in use until the administrator confirms.
6. An unblock of a script (a later release) survives an update only when the digest of the script and its settings is unchanged.

## Offline, failure and cancel

| Case | Behavior |
|---|---|
| No network, DNS or connection failure | A clear message. Nothing changes. The stored definitions stay usable. |
| Timeout or size over the limit | The fetch stops at the limit. The message names the limit. |
| HTTP error (4xx, 5xx) | The message names the status and the host, not the query. |
| 429 or 503 | The program waits as the answer asks and retries up to 3 times, all inside the 60 seconds of the fetch; then the message. |
| Redirect to another scheme or too many redirects | Refused. |
| Certificate error | Refused. The message names the host and the error. |
| Not JSON, wrong format, wrong `FeedFormat`, duplicate property | Refused with the position of the first error. |
| Pin mismatch, bad signature, other signer | Refused. The message names what was expected and what was found. |
| The administrator cancels (window or fetch) | The fetch is cancelled at once. Nothing is written. |
| A write fails in the middle | The earlier state stays. The message says which definition failed. |
| The disk is full | The same; no partial file remains. |

## Secrets in addresses

- An address with user information, or with a query parameter that looks like a secret (`token`, `key`, `apikey`, `access_token`, `password`, `secret`, `sig`, `signature`, `sas`, and the common cloud-signature names) is refused for an anonymous import, with the message to use an authenticated feed.
- No log line, error, report, source record or export holds a query string. A line names the scheme, the host and the path. This needs a change of existing code: the download service logs the whole address with its query today, and the first release of the import changes it (and applies the redactor to the fetch, the preview, the source record and the log).

## Imported definitions run with no ambient credentials

A definition of the program can read an API with a token that the administrator has (the GitHub token from the environment or from `gh`). A vendor definition must not receive it.

1. A request that an imported definition causes (a version source, a hop, a download) carries **no** ambient credential: not the GitHub token, not a Windows credential, not a stored feed credential of another host.
2. A hop that puts a value captured from one host into the address of a different host (in the host, the path or the query) is **blocked**, because it could send what a first hop read to the vendor: with the token of the administrator, or from an intranet page that needs no credential at all. A relative hop (as `{capture}/page.html` in a shipped definition) is resolved at run time and must stay on the host of the hop before it; this is allowed.
3. The administrator can allow the GitHub token for one imported definition after a confirmation that names the definition and the host. The block in rule 2 stays.
4. Every request that an imported definition causes through `curl.exe` runs with `--proto =https --proto-redir =https`, so that a redirect cannot downgrade it to HTTP after the static check of the address.

## Authenticated feeds (addendum)

Some vendors give a feed only to customers. The program supports six methods. Every method has the same rules.

### Methods

| # | Method | What the administrator enters | How it is sent |
|---|---|---|---|
| 1 | Bearer or license key in a header | The header name (default `Authorization` with `Bearer`, or a custom name such as `X-License-Key`) and the key. | In the header of each request to the bound host. |
| 2 | HTTP Basic | A user name and a password. | The `Authorization: Basic` header, only over HTTPS, to the bound host. |
| 3 | Windows integrated authentication | Nothing (the account of the administrator). | Kerberos or NTLM through the Windows stack, with a `CredentialCache` for the exact `https://host:port/` prefix and the Negotiate scheme, only to a host that the administrator listed as an intranet host for this feed. |
| 4 | Client certificate (mutual TLS) | The thumbprint of a certificate in `CurrentUser\My` that has a private key. | The certificate is offered in the TLS handshake with the bound host only (the handler selects the certificate by the target host and returns none for any other host). The private key stays in the store and is never read or exported. |
| 5 | OAuth 2 or Microsoft Entra ID token | The token address, the client id and a client secret (or the thumbprint of a certificate), and the scope. | The program gets a token with the client credentials flow and sends it as a bearer token. The token is kept in memory only, until it expires. The secret goes to the **token host**, which is a second bound host (the entry names both hosts). |
| 6 | Signed expiring address | The whole address, which contains the signature. | The address is the secret. It is fetched as it is. |

### Storage of secrets

1. A secret (key, password, client secret, signed address) is stored in the **Windows Credential Manager**, as a generic credential of the current Windows user, persisted on the local computer only (not the enterprise persistence, which roams). There is one store. A secret larger than the limit of the Credential Manager (2560 bytes) is refused with a message. The Intune client secret of the program, which is a DPAPI value in the settings file, is not part of this and is not changed here.
2. The settings file holds only the id of the credential entry, the method, the host, and non-secret values (header name, user name, token address, client id, certificate thumbprint).
3. A secret is shown as dots in the window, with one button to replace it. It is never shown again after it is saved.
4. **Redaction.** One redactor is applied to every output that the program writes: log lines, error messages, exceptions, reports, source records and exports. It removes the values of the headers that carry a secret, the secret parts of an address, and any registered secret value found in a text. A test puts a canary value as the secret of each method and searches all outputs for it.
5. The Credential Manager does not stop a program that runs as the same user from reading a secret (see "Code in a definition").

### Binding to a host

1. A credential entry is bound to one **scheme, host and port** (HTTPS only), and, for method 5, to the token host as well. The credential is sent only to a request whose scheme, host and port are exactly those.
2. A redirect to another host (or port, or scheme) is followed without any credential: the headers of the secret are not sent, the client certificate is not offered, and the cookies are not kept (see "How redirects are followed"). If the other host answers 401 or 403, the fetch fails with the message that the redirect went to a host that has no credential entry. The program never asks the administrator to reuse the credential on the other host.
3. A redirect inside the same host keeps the credential (the loop of the program sends it again; the automatic redirect of .NET would not).
4. The address of the document and the addresses in the definitions are checked against the bound host. An address on **another host** has no credential. The preview lists each such host. The administrator can add a credential entry for that host (a second entry, with its own method and secret) or confirm that it is fetched without one. The confirmation names the host.
5. HTTP is refused for every method. A downgrade by a redirect is refused.

### Payload downloads and version checks

The payload downloads and the version checks of the program run through `curl.exe`, not through `HttpClient`, and `curl.exe` follows redirects by itself: it removes only `Authorization` and `Cookie` on a redirect to another host, sends a custom header (method 1) to every target, offers a store certificate to any host that asks, and shows a header given on the command line to the other processes of the same user and to administrators. So the binding rules do not hold on that path as it is. A request that needs a credential (a download or a scrape from an authenticated feed) does one of two things:

1. It goes through the **managed fetcher** of the feed (the loop above), with the bound credential.
2. Or `curl.exe` runs **without `-L`**, with the secret passed on standard input (`-K -`), with `--proto =https --proto-redir =https`, and the program follows the redirects itself under the rules above. The program already sends a configuration on standard input for the GitHub API; the two are merged into one configuration, and a secret is never put on the command line.

A request without a credential keeps the behavior of today. The command-line build does not read the credentials of feeds, by design (a build that runs unattended must not use the secret of a user), so a definition that needs a credential for its payload host fails there with the message that the credential entry is missing.

### Access is not trust

1. A credential only opens the door of the feed. It does not change the trust state of the document and it does not verify the installers.
2. The pin or the publisher check of "Trust" is still wanted. A document that came over an authenticated channel and has no pin is `Not verified`.
3. The rules for code and for the SHA-256 or signature of the payload are unchanged.
4. A definition from an authenticated feed whose payload has no SHA-256 source and no signature requirement gets the finding `Payload SHA256 NOT verified`, and the administrator confirms it as for any other definition.

### The administrator's computer does the work

The program downloads, stages and packages on the administrator's computer. The credential is used there and only there. The credential is **never** written into a staged manifest, a script, a package, a `.intunewin` file, an application object in ConfigMgr or Intune, or a WSUS update, and an endpoint never receives it. A definition from an authenticated feed does not tell the endpoint where to download from with a secret: the installer is part of the package (the content is copied from the share or uploaded).

### Interface

A page **Feeds** with a list and these actions:

| Action | What it does |
|---|---|
| Add | The address, the vendor, the method, and the secret fields. Saves the entry and the secret, then runs Test. |
| Test | Sends one request to the address and shows the HTTP status, the final host, the size and the trust state. It shows no header and no secret. It reports 401, 403, an expired token and a certificate error each with its own message. |
| Rotate | Replaces the secret. The old secret is overwritten in the store. The next fetch uses the new one. |
| Remove | Deletes the credential entry and the settings. The administrator chooses whether the imported definitions stay or go (see "Where the definitions are stored"). |
| Import, Check for update | As in "Preview" and "Update of a feed". |

### License keys

A license key in a header is **access control** for the feed. It is not copy protection. Anyone who has the key can fetch the feed, and anyone who has a downloaded definition or an installer can copy it. The program keeps the key out of files and logs and sends it only to the bound host. It does not and cannot stop an administrator from sharing what the program downloaded.

## Tests

Unit tests (CI, with a fake HTTP handler):

1. **Fetch limits:** each rule of the table (scheme, redirect count and scheme, size, time including retries, content type, certificate error) refuses and writes nothing.
2. **Validation:** an unknown property, a duplicate property name (also with different letter case), a duplicate `Id`, an `Id` that is not in the grammar (a path, a device name, a trailing dot), an address with user information, a `file:` or UNC address, a hop with a captured host, and an environment expansion are refused and named; the other definitions of the document import; `Schema` and `Acquisition` are allowed; `ImportedFrom` and `ImportedVendor` are refused; the stored file is the re-serialized model.
3. **Preview:** nothing is written before the confirmation; Cancel writes nothing; the conflict list (shipped, user, another feed) and the default (skip) are right.
4. **Trust:** a pinned digest that matches and that does not; a signature that is valid, invalid, from another signer, without the code-signing usage, with an expired certificate and a time stamp inside its life, and older than the stored `Updated`; the wording `Definition document NOT verified` in the preview, the source record, the log and the grid label; `Not verified` asks for a confirmation at each import and update.
5. **Code:** each property of the list blocks the definition in every trust state, in the first release too; the list is checked against the format; (step 3) the unblock needs the confirmation and covers the settings of the script; (step 4) a change of a setting or of a script blocks again.
6. **Storage:** the staging folder and the swap; `source.json` last; a failed write keeps the earlier state; the precedence of shipped, imported and user definitions; the new origin in all three loaders; a shipped `Id` replaced only with its confirmation; the label from the folder, not from the file; Remove.
7. **Update:** the diff lists added, removed and changed definitions and the host, hash and code changes; nothing is written before the confirmation; a changed pin stops the update; an update matches on the stored feed.
8. **Offline and failures:** every row of the failure table.
9. **Secrets in addresses and ambient credentials:** the refusal for an anonymous import; no query in any log line (also the download service); an imported definition gets no GitHub token.
10. **Authenticated feeds, for each method (1 to 6):** the credential is sent to the bound host; a request to another host carries none; a redirect inside the host keeps it; a redirect to another host drops it (a fake handler records every request and the test asserts that no secret header and no cookie reaches the other host; the client certificate is proved in the lab); a 401 and a 403 give their messages; an expired token (method 5) is renewed once and then fails; a revoked key gives the 401 message and no retry loop; HTTP and a downgrade redirect are refused; a payload host without an entry is listed in the preview; the curl path gets no `-L` and no secret on its command line.
11. **Redaction:** a canary secret for each method is not found in any log, error, report, source record, export, settings file, staged manifest or script (the test reads every file that the run writes).
12. **Credential store:** a secret goes to the store and not to the settings file; Rotate overwrites it; Remove deletes it; a secret over the limit is refused.

A fake handler cannot see a TLS client certificate, so the certificate and integrated-authentication rules are proved in the lab. Lab tests (the lab has a web server and a certificate authority, and the test starts a small token endpoint on the web server for method 5; the rules of the lab apply: no sign-out, no restart, no power-off). There is one lab test for each method that has a transport of its own:

1. Method 1 and 2: an HTTPS feed with a header key and with Basic: the right credential imports, a wrong one gives 401, a redirect to a second lab host shows that the second host sees no credential.
2. Method 3: Windows integrated authentication for a host in the intranet list, and a refusal for a host that is not in the list.
3. Method 4: a feed that needs a client certificate: the import works with the certificate and fails without it, the certificate is not offered to a second host, and the private key is not exportable from the program.
4. Method 5: a token from a lab token endpoint, a token that expires during a run, and a revoked client secret.
5. Method 6: a signed expiring address that works and the same address after it expires.
6. A feed document with a pinned digest from the lab authority.

## Delivery

One pull request for this specification. Then the implementation, one pull request at a time, each with its tests, its test documentation, a version change and a changelog entry:

1. Fetch with the manual redirect loop, the limits, validation (new code), preview, storage (third folder, new origin in the three loaders), the source record, the grid label, anonymous import with a pinned digest, Remove feed, the **block of code in a definition (no unblock)**, no ambient credentials for imported definitions, the removal of the query from logs and the redactor for these outputs. The trust state `Not verified` with its wording.
2. The trust by signed document (the pinned publisher).
3. The unblock of a script, with its confirmation and its settings digest.
4. The update with the diff.
5. Authenticated feeds, the Credential Manager store, the full redactor and the Feeds page, with method 1 and method 2, and the managed or no-redirect curl path for authenticated payloads.
6. Methods 3 (integrated), 4 (client certificate), 5 (token) and 6 (signed address), each in its own change with its lab test.

## Open and unverified

- The exact signature format and the place of the signature file (a detached `.p7s` at the address with `.p7s` added is assumed).
- The list of properties that run code (made when step 1 is built, with a test).
- Whether `FeedId` from the document or a name that the administrator chooses is the better folder name when two vendors collide (the preview lets the administrator change it).
- Whether OAuth 2 should also offer the device code flow (not in step 6).
- The behavior of `SocketsHttpHandler` for headers and certificates on a redirect is taken from the runtime as the review states it; step 1 confirms it with a test against the real handler.

## Not covered

- Import of installers or packages from a URL. The program already downloads installers through the payload rules of a definition.
- Automatic or scheduled update of a feed.
- A feed that the program itself publishes.
