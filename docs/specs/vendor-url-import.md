# Import of vendor definitions from a URL

Status: specification, not yet built. Date: 2026-11-12.

## Summary

A software vendor can publish a **feed**: a JSON document that holds application definitions in the definition format of App Packager Desktop. An administrator enters the HTTPS address of the feed. The program fetches the document, validates it, shows everything that the import would do, and stores the definitions only after the administrator confirms. The definitions are stored apart from the shipped and the user definitions, and each one is labelled with its vendor and its source address.

Three rules shape the design:

1. **Nothing happens silently.** There is no import without a full preview and a confirmation, and no update without a diff and a confirmation.
2. **The document is data, not trust.** A fetched document is "not verified" unless the administrator pinned its SHA-256 or pinned the code-signing publisher of the vendor. A definition that carries a script is blocked unless the administrator confirms it in particular.
3. **A secret never leaves the administrator's computer.** An authenticated feed uses a credential that is kept in the Windows credential store, is sent only to the host that the administrator entered, and is never written to a file, a log, a report, a package or an endpoint.

An authenticated feed is an addendum to this specification (see "Authenticated feeds"). It does not change rules 1 to 3.

## What exists today

The program has no import of definitions from a URL. These features are near it and stay as they are:

| Feature | What it does | Difference |
|---|---|---|
| Add from web | A wizard: the administrator enters a version source and a download address, and the program writes one new definition. | The result is a definition that the program wrote from the administrator's input, not a document that a vendor wrote. |
| Add from file | Reads an installer file on the computer and builds one definition. | Same. |
| Bundle import (Workbench) | Imports a `bundle.zip` of one application with its profiles from a file. | A file, not an address. It is checked against hashes that are inside the bundle, so it proves integrity and not origin. |
| User definitions folder | A `*.json` file in this folder is a definition. A user definition with the `Id` of a shipped definition replaces it. | There is no label for the origin, and no check of the content beyond the loader. |

The loader reads the shipped folder and the user folder. This specification adds a third folder (see "Where the definitions are stored").

## Terms

| Term | Meaning |
|---|---|
| Feed | The JSON document at the address. It holds the vendor name and a list of definitions. |
| Vendor definition | One definition of a feed, in the existing definition format. |
| Source record | The record that the program writes beside the stored definitions of a feed: address, time, digest, trust state, signer. |
| Trust state | `Pinned digest`, `Signed by pinned publisher`, or `Not verified`. |
| Credential entry | The stored secret and the host that it is bound to (authenticated feeds only). |

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
- `FeedId` is 3 to 64 characters of `a-z`, `0-9` and `-`. It names the folder of the stored definitions. It is chosen by the document, and the administrator can change it in the preview, because two vendors can use the same id.
- `Definitions` holds the definitions. A document with a single definition object in place of the envelope is also accepted; the vendor name is then asked for in the preview.

## Limits of the fetch

| Rule | Value |
|---|---|
| Scheme | HTTPS only. `http:`, `file:`, `ftp:` and every other scheme is refused. A redirect to HTTP is refused. |
| Redirects | At most 5. Each target is checked again (scheme, host, address in a private range is allowed because feeds can be inside a company, but the preview names the host). |
| Size | 2 MiB for the document, read as a stream and cut off at the limit. |
| Time | 15 seconds to connect and 60 seconds in all. |
| Content | UTF-8 text. The content type must be `application/json` or `text/plain`; a page of HTML is refused with its type named. |
| Certificate | The certificate chain and the host name are checked against the Windows store. There is no option to ignore an error. |
| Request | A generic `User-Agent` (product and version), no cookies, no referrer. The proxy settings of Windows apply. |
| Document | At most 200 definitions, at most 64 levels of depth, at most 4 MiB after parse. |

A failure of any rule stops the fetch. Nothing is written.

## Validation

The document is parsed with the same loader as a definition file and then checked strictly:

1. Every definition passes the definition loader and the schema checks of the definition format.
2. A property that the format does not know is refused and named. (The loader of a user file ignores such a property; an import is stricter, because the vendor writes the document.)
3. `Id` has the characters and the length that the format allows, and is unique in the document.
4. Every address in a definition (download address, version source, checksum file, icon) is `https:`. A UNC path, a local path, a `file:` address and an address with user information (`user:password@host`) are refused.
5. A property that expands an environment variable or runs a command is refused, except the tokens that the format defines.
6. The hosts of all addresses of a definition are listed for the preview.

The result of validation is a list of findings (blocking, warning, information). A blocking finding keeps the definition out of the import. The administrator can import the other definitions of the document.

## Preview

Before anything is written, the program shows one window with:

1. The source: the address, the final address after redirects, the time, the size, the SHA-256 of the document, the vendor name, and the trust state (see "Trust").
2. For each definition: `Id`, name, vendor, installer type, the hosts of every address, whether the payload has a SHA-256 source (`PayloadSha256`, `PayloadHashQuery`, `PayloadSha256Url`) and whether the definition requires one (`RequirePayloadHash`, `RequireValidSignature`), the detection type, the install and uninstall commands, and whether it carries a script (see "Scripts").
3. The conflicts: a definition whose `Id` is in the shipped definitions or in the user definitions. The default is to skip it. The administrator can choose to replace a shipped definition for each `Id`, with a confirmation that names what is replaced (see "Where the definitions are stored").
4. The findings of validation.
5. A button to view the whole text of a definition, and the buttons **Import** and **Cancel**.

The preview needs no network after the fetch. **Cancel** writes nothing.

## Trust

A fetched document proves nothing about its origin by itself. The program offers two controls and a state for no control.

| Trust state | How the administrator gets it | What the program checks |
|---|---|---|
| `Pinned digest` | Enters the expected SHA-256 of the document, which the vendor publishes on another channel. | The SHA-256 of the received bytes equals the pinned value. |
| `Signed by pinned publisher` | Enters the expected publisher of the vendor (the subject and the thumbprint of the code-signing certificate). | A detached signature of the document (a `.p7s` file next to the document, fetched from the same host) is valid for the exact bytes, the certificate chains to a trusted root with a valid time stamp or an unexpired certificate, and the signer is the pinned one. |
| `Not verified` | Nothing. | Nothing. |

Rules:

1. **Wording.** The state `Not verified` uses the same wording as the payload check of the stage: **NOT verified**. The preview, the source record, the grid label and the log say "Definition document NOT verified". The program never says "trusted", "safe" or "verified" for a document without a pin, and never for an installer that a definition names (the payload rules in [Payload verification](../reference/payload-verification.md) apply to the installer, whatever the trust of the document).
2. An import in the state `Not verified` needs a confirmation that names the state. The confirmation is asked again for each import and each update; it is never remembered.
3. A pin that does not match stops the import. It does not fall back to `Not verified`.
4. The pins are stored in the feed settings (not secret). A change of the pin of a stored feed is a new import.
5. The trust of the document does not extend to scripts (see "Scripts") or to the installers.

The exact signature format (CMS detached signature with `SignedCms`) and the fetch of the signature file are confirmed when the signature step is built.

## Scripts

A definition **carries a script** when it holds PowerShell text or another executable text: a script detection, a script requirement or condition, an install or uninstall script or step, a post-install step, or a wrapper. The list of such properties is made from the definition format when the check is built, and a test fails when the format gets a script property that the list does not name.

1. A definition that carries a script is **blocked** from the import. The preview shows the block and the reason.
2. The administrator can unblock one definition after reading it: the window shows the full text of every script of that definition, the SHA-256 of each script, and requires a confirmation that names the definition. The unblock is for this document and this digest only.
3. The SHA-256 of each unblocked script is stored in the source record. An update that changes the text of such a script blocks the definition again.
4. A script that was unblocked is a script like any other for the signing policy of the program (sign, require signature). The import does not sign or trust it.
5. This holds in every trust state, also `Pinned digest` and `Signed by pinned publisher`. A pin proves where the document came from; it does not say that its scripts are safe.

## Where the definitions are stored

A third definition folder, apart from the shipped and the user folders:

```
<data root>\ImportedDefinitions\<FeedId>\
    source.json        (the source record)
    <Id>.json          (one file for each imported definition)
```

- The loader reads the folder after the shipped folder and before the user folder. A user definition with the same `Id` therefore wins over an imported one, and an imported definition with the `Id` of a shipped definition replaces it only when the administrator confirmed that `Id` in the preview.
- A file is written to a temporary name and moved into place. An import is all-or-nothing for each definition; a failure leaves the earlier state.
- `source.json` holds: the final address (with the query removed), the vendor, the `FeedId`, the time of the fetch, the SHA-256 and the size of the document, the trust state, the pinned digest or the pinned publisher, the signer that was found, the `Id` list, the replaced shipped `Id` list, and the digests of the unblocked scripts. It holds no secret.
- Each stored definition gets two extra read-only properties: `ImportedFrom` (the `FeedId`) and `ImportedVendor`. The grid shows the origin in a column ("Imported: Contoso Software"), and the details show the source address and the trust state. A definition from a feed is never shown as a shipped definition.
- **Remove feed** deletes the folder and the settings of the feed after a confirmation that names the count of definitions. Stages that already exist stay.

## Update of a feed

1. An update is **manual**: the button **Check for update** on a feed. There is no timer, no check at start, and One Click does not fetch feeds.
2. The update fetches to a temporary place with the same limits and the same trust rules, and shows a **diff** against the stored definitions: added and removed definitions, changed properties of each definition, and in particular a changed address host, a changed hash source, a changed requirement for a hash or a signature, and any new or changed script.
3. Nothing is written until the administrator confirms the diff. The confirmation names the trust state; an update in the state `Not verified` is confirmed again each time.
4. A feed whose pin no longer matches (the vendor changed the document or the signer) stops with the message and shows the new digest, so that the administrator can pin the new one after checking it on the vendor's other channel.
5. A removed definition is not deleted without the confirmation. A definition that the update removes stays in use until the administrator confirms.

## Offline, failure and cancel

| Case | Behavior |
|---|---|
| No network, DNS or connection failure | A clear message. Nothing changes. The stored definitions stay usable. |
| Timeout or size over the limit | The fetch stops at the limit. The message names the limit. |
| HTTP error (4xx, 5xx) | The message names the status and the host, not the query. |
| 429 or 503 | The program waits as the answer asks (up to 60 seconds) and retries up to 3 times; then the message. |
| Redirect to another scheme or too many redirects | Refused. |
| Certificate error | Refused. The message names the host and the error. |
| Not JSON, wrong format, wrong `FeedFormat` | Refused with the position of the first error. |
| Pin mismatch, bad signature, other signer | Refused. The message names what was expected and what was found. |
| The administrator cancels (window or fetch) | The fetch is cancelled at once. Nothing is written. |
| A write fails in the middle | The earlier state stays. The message says which definition failed. |
| The disk is full | The same; no partial file remains. |

## Secrets in addresses

- An address with user information, or with a query parameter that looks like a secret (`token`, `key`, `apikey`, `access_token`, `password`, `secret`, `sig`, `signature`, `sas`, and the common cloud-signature names) is refused for an anonymous import, with the message to use an authenticated feed.
- No log line, error, report, source record or export holds a query string. A line names the scheme, the host and the path.

## Authenticated feeds (addendum)

Some vendors give a feed only to customers. The program supports six methods. Every method has the same rules.

### Methods

| # | Method | What the administrator enters | How it is sent |
|---|---|---|---|
| 1 | Bearer or license key in a header | The header name (default `Authorization` with `Bearer`, or a custom name such as `X-License-Key`) and the key. | In the header of each request to the bound host. |
| 2 | HTTP Basic | A user name and a password. | The `Authorization: Basic` header, only over HTTPS, to the bound host. |
| 3 | Windows integrated authentication | Nothing (the account of the administrator). | Kerberos or NTLM through the Windows stack, only to a host that the administrator listed as an intranet host for this feed. |
| 4 | Client certificate (mutual TLS) | The thumbprint of a certificate in `CurrentUser\My` that has a private key. | The certificate is offered in the TLS handshake with the bound host. The private key stays in the store and is never read or exported. |
| 5 | OAuth 2 or Microsoft Entra ID token | The token address, the client id and a client secret (or the thumbprint of a certificate), and the scope. | The program gets a token with the client credentials flow and sends it as a bearer token. The token is kept in memory only, until it expires. |
| 6 | Signed expiring address | The whole address, which contains the signature. | The address is the secret. It is fetched as it is. |

### Storage of secrets

1. A secret (key, password, client secret, signed address) is stored **only** in the Windows Credential Manager, or protected with DPAPI for the current Windows user, as the Intune client secret already is. It is never written to a settings file, a definition, a source record, a log, an export, a report, a CI artifact or a support bundle.
2. The settings file holds only the id of the credential entry, the method, the host, and non-secret values (header name, user name, token address, client id, certificate thumbprint).
3. A secret is shown as dots in the window, with one button to replace it. It is never shown again after it is saved.
4. **Redaction.** One redactor is applied to every output that the program writes: log lines, error messages, exceptions, reports, source records and exports. It removes the values of the headers that carry a secret, the secret parts of an address, and any registered secret value found in a text. A test puts a canary value as the secret of each method and searches all outputs for it.

### Binding to a host

1. A credential entry is bound to one **scheme, host and port** (HTTPS only). The credential is sent only to a request whose scheme, host and port are exactly those.
2. A redirect to another host (or port, or scheme) is followed without any credential: the headers of the secret are not sent, the client certificate is not offered, and the cookies are not kept. If the other host answers 401 or 403, the fetch fails with the message that the redirect went to a host that has no credential entry. The program never asks the administrator to reuse the credential on the other host.
3. A redirect inside the same host keeps the credential.
4. The address of the document, the address of its signature file and the addresses in the definitions are checked against the bound host. An address on **another host** has no credential. The preview lists each such host. The administrator can add a credential entry for that host (a second entry, with its own method and secret) or confirm that it is fetched without one. The confirmation names the host.
5. HTTP is refused for every method. A downgrade by a redirect is refused.

### Access is not trust

1. A credential only opens the door of the feed. It does not change the trust state of the document and it does not verify the installers.
2. The pin or the publisher check of "Trust" is still wanted. A document that came over an authenticated channel and has no pin is `Not verified`.
3. The rules for scripts and for the SHA-256 or signature of the payload are unchanged.
4. A definition from an authenticated feed whose payload has no SHA-256 source and no signature requirement gets the finding "payload NOT verified", and the administrator confirms it as for any other definition.

### The administrator's computer does the work

The program downloads, stages and packages on the administrator's computer. The credential is used there and only there. The credential is **never** written into a staged manifest, a script, a package, a `.intunewin` file, an application object in ConfigMgr or Intune, or a WSUS update, and an endpoint never receives it. A definition from an authenticated feed does not tell the endpoint where to download from with a secret: the installer is part of the package (the content is copied from the share or uploaded).

### Interface

A page **Feeds** with a list and these actions:

| Action | What it does |
|---|---|
| Add | The address, the vendor, the method, and the secret fields. Saves the entry and the secret, then runs Test. |
| Test | Sends one request to the address and shows the HTTP status, the final host, the size and the trust state. It shows no header and no secret. It reports 401, 403, an expired token and a certificate error each with its own message. |
| Rotate | Replaces the secret. The old secret is overwritten in the store. The next fetch uses the new one. |
| Remove | Deletes the credential entry and the settings. The administrator chooses whether the imported definitions stay or go. |
| Import, Check for update | As in "Preview" and "Update of a feed". |

### License keys

A license key in a header is **access control** for the feed. It is not copy protection. Anyone who has the key can fetch the feed, and anyone who has a downloaded definition or an installer can copy it. The program keeps the key out of files and logs and sends it only to the bound host. It does not and cannot stop an administrator from sharing what the program downloaded.

## Tests

Unit tests (CI, with a fake HTTP handler):

1. **Fetch limits:** each rule of the table (scheme, redirect count and scheme, size, time, content type, certificate error) refuses and writes nothing.
2. **Validation:** an unknown property, a duplicate `Id`, an address with user information, a `file:` or UNC address, and an environment expansion are refused and named; the other definitions of the document import.
3. **Preview:** nothing is written before the confirmation; Cancel writes nothing; the conflict list and the default (skip) are right.
4. **Trust:** a pinned digest that matches and that does not; a signature that is valid, invalid, from another signer, or expired; the wording "NOT verified" in the preview, the source record, the log and the grid label; `Not verified` asks for a confirmation at each import and update.
5. **Scripts:** each script-bearing property blocks the definition; the unblock needs the confirmation; the digest of the script is stored; an update that changes the script blocks again; the list of script properties is checked against the format.
6. **Storage:** the temporary file and the move; a failed write keeps the earlier state; the precedence of shipped, imported and user definitions; a shipped `Id` replaced only with its confirmation; Remove feed.
7. **Update:** the diff lists added, removed and changed definitions and the host, hash and script changes; nothing is written before the confirmation; a changed pin stops the update.
8. **Offline and failures:** every row of the failure table.
9. **Secrets in addresses:** the refusal for an anonymous import; no query in any log line.
10. **Authenticated feeds, for each method (1 to 6):** the credential is sent to the bound host; a request to another host carries none; a redirect to another host drops it (a fake handler records every request and the test asserts that no secret header, no cookie and no client certificate reaches the other host); a 401 and a 403 give their messages; an expired token (method 5) is renewed once and then fails; a revoked key gives the 401 message and no retry loop; HTTP and a downgrade redirect are refused; a payload host without an entry is listed in the preview.
11. **Redaction:** a canary secret for each method is not found in any log, error, report, source record, export, settings file, staged manifest or script (the test reads every file that the run writes).
12. **Credential store:** a secret goes to the store and not to the settings file; Rotate overwrites it; Remove deletes it.

Lab tests (the lab has a web server and a certificate authority; the rules of the lab apply: no sign-out, no restart, no power-off):

1. An HTTPS feed with a certificate from the lab authority: import with a pinned digest.
2. A feed that needs a client certificate (method 4): the import works with the certificate and fails without it, and the private key is not exportable from the program.
3. A feed with Windows integrated authentication (method 3) for a host in the intranet list, and a refusal for a host that is not in the list.
4. A redirect from the feed host to another lab host with a credential: the second host sees no credential.

## Delivery

One pull request for this specification. Then the implementation, one pull request at a time, each with its tests, its test documentation, a version change and a changelog entry:

1. Fetch, limits, validation, preview, storage, the source record, the grid label, anonymous import with a pinned digest, and Remove feed. The trust state `Not verified` with its wording.
2. The trust by signed document (the pinned publisher).
3. The block of scripts and the unblock with the confirmation.
4. The update with the diff.
5. Authenticated feeds, the credential store, the redactor and the Feeds page, with method 1 and method 2.
6. Methods 3 (integrated), 4 (client certificate), 5 (token) and 6 (signed address), each in its own change with its lab test.

## Open and unverified

- The exact signature format and the place of the signature file (detached `.p7s` next to the document is assumed).
- The list of script-bearing properties of the definition format (made when step 3 is built, with a test).
- Whether `FeedId` from the document or a name that the administrator chooses is the better folder name when two vendors collide (the preview lets the administrator change it).
- Whether OAuth 2 should also offer the device code flow (not in step 6).

## Not covered

- Import of installers or packages from a URL. The program already downloads installers through the payload rules of a definition.
- Automatic or scheduled update of a feed.
- A feed that the program itself publishes.
