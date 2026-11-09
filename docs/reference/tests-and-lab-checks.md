# Tests and lab checks

This page lists the tests that guard the release, trust and update paths, and the checks that were done on lab machines. It says what each test proves and how to run it.

## How to run the tests

The unit tests are in `tests\ApplicationPackagerEngine.Tests`. Run one class:

```powershell
dotnet test tests\ApplicationPackagerEngine.Tests\ApplicationPackagerEngine.Tests.csproj --filter "FullyQualifiedName~IntuneWinToolPinTests"
```

The full suite runs in the CI workflow (`ci.yml`). Tests that open windows take the focus of the computer. Run the full suite in CI or on a lab machine, not on a workstation that someone is using.

## Intune content prep tool download

The download of `IntuneWinAppUtil.exe` is pinned to the Microsoft release 1.8.7 and to its SHA-256 (`IntuneWinToolService.PinnedVersion`, `IntuneWinToolService.PinnedSha256`).

| Test class | What it proves |
|---|---|
| `IntuneWinToolPinTests` | The default URL names a tag (`/raw/v1.8.7/`), not a branch. The pinned hash is a SHA-256 digest. A file with another hash is refused before the signature check, and nothing is installed. A file with the right hash must still carry a Microsoft Corporation signature. |

The checks apply to a file that the app downloads. A copy of the tool that is already in the Tools folder or on the PATH is used as it is.

**Lab check.** A unit test cannot show that the real file at the tag matches the pin, so it was checked on the ConfigMgr lab server:

1. Download `https://github.com/microsoft/Microsoft-Win32-Content-Prep-Tool/raw/v1.8.7/IntuneWinAppUtil.exe` with `curl.exe`, as the app does.
2. Expected: 62,520 bytes, SHA-256 `C1BA45B5CB939E84AF064BB7FF4B38FB3DFE33C8DC1078FD9B157672EAE671F6`, Authenticode status `Valid`, signer Microsoft Corporation.
3. Change one byte of a copy. Expected: the hash no longer matches the pin, and the signature status is `HashMismatch`.
4. Delete the download.

Result (2026-11-09): all expected values were seen. The lab machines were started for the check and stopped afterwards. No snapshot was used.

To take a new release of the tool, change `PinnedVersion` and `PinnedSha256` together, repeat this check, and update the URL in [Network](network.md).

## Intune publish and cancellation

`IntunePublishService` passes the cancellation token to every step that waits.

| Test class | What it proves |
|---|---|
| `IntunePublishCancellationTests` | A publish that is cancelled while it waits for the token, for the storage address, for a block upload or for the commit ends within seconds. No request activates the app. The tenant stand-in blocks one step until the token of that request is cancelled. |

**Lab check.** A real cancel test needs an Intune tenant, which the lab does not have. The stand-in tenant covers the same code path, because the service sends every request through one `HttpClient`.
