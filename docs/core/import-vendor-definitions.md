# Import vendor definitions from a URL

A software vendor can publish a **feed**: a JSON document with application definitions in the definition format of this program. **Import From URL** (a button under the grid of the App Packager page) fetches such a document, shows everything that the import would do, and stores the definitions only after you confirm.

Specification: [Import of vendor definitions from a URL](../specs/vendor-url-import.md). This page describes what is built: the anonymous import with a pinned digest. Signed documents, the unblock of a script or a command, the update of a feed and authenticated feeds are later steps.

## Steps

1. Select **Import From URL**. The window opens.
2. Enter the HTTPS address of the feed. An address that is not HTTPS is refused. Optionally enter the SHA-256 of the document, which the vendor publishes on another channel (a pin).
3. Select **Fetch**. **Cancel Fetch** stops it. Nothing is written by a fetch.
4. Read the preview: the source (address without its query, size, SHA-256, vendor, trust state), then for each definition its Id, name, installer type, the hosts of its addresses, whether the payload has a SHA-256 source, the status, and, below the grid, the whole definition and the findings.
5. Check the vendor name and the **Feed Id** (3 to 64 characters of `a-z`, `0-9` and `-`). The Feed Id names the folder of the definitions. Two vendors can use the same one, so you can change it.
6. If the document has no pin, its state is **Definition document NOT verified**. Select the confirmation of that state. It is asked again for each import.
7. Select **Import**.

## What is blocked or skipped

| Case | What happens |
|---|---|
| The definition carries code (a script, an uninstall command, an expanded install command, an installer setting fragment, a bootstrapper, a path to a local file) | Blocked in every trust state. There is no unblock yet. |
| The Id is the Id of a shipped definition | Skipped. You can choose **Replace shipped** for one Id; you are asked to confirm, and the imported definition has its own profiles (`imported:<Id>`). |
| The Id is the Id of one of your own definitions, or of another imported feed | Skipped. Your own definition always wins. |
| Another Id, but the same stage folder (Application and Architecture) as another definition | Skipped, because the two would share one stage. |
| The pin does not match | The import stops. It does not fall back to "not verified". |
| A property that the format does not know, a duplicate property name, an address that is not HTTPS | The document or the definition is refused, with the reason. |

## Where the definitions go

`Data\ImportedDefinitions\<Feed Id>\<Id>.json` and `source.json` (the address without its query, the vendor, the time, the SHA-256, the trust state and the list of Ids). They are apart from the shipped and your own definitions. The grid shows the column **Source**, "Imported: vendor (trust state)", while a definition from a feed is in the grid.

## Remove a feed

The list **Imported feeds** in the same window has **Remove Feed**. It deletes the folder and the definitions of the feed after a confirmation that names the count. Stages that already exist stay.
