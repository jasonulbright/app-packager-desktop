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
| `IntuneWinToolPinTests` | The default URL names a tag (`/raw/v1.8.7/`), not a branch. The pinned hash is a SHA-256 digest. A file with another hash is refused before the signature check, and nothing is installed. A file with the right hash must still pass the signature check; the test uses an unsigned file. The check for a valid signature from another publisher is not covered by a test. |

The checks apply to a file that the app downloads. A copy of the tool that is already in the Tools folder or on the PATH is used as it is.

**Lab check.** A unit test cannot show that the real file at the tag matches the pin, so it was checked on the ConfigMgr lab server:

1. Download `https://github.com/microsoft/Microsoft-Win32-Content-Prep-Tool/raw/v1.8.7/IntuneWinAppUtil.exe` with `curl.exe`, as the app does.
2. Expected: 62,520 bytes, SHA-256 `C1BA45B5CB939E84AF064BB7FF4B38FB3DFE33C8DC1078FD9B157672EAE671F6`, Authenticode status `Valid`, signer Microsoft Corporation.
3. Change one byte of a copy. Expected: the hash no longer matches the pin, and the signature status is `HashMismatch`.
4. Delete the download.

Result (2026-11-09): all expected values were seen. The lab machines were started for the check and stopped afterwards. No snapshot was used.

The download URL is built from `PinnedVersion`. To take a new release of the tool, change `PinnedVersion` and `PinnedSha256` together, repeat this check with the URL, size and SHA-256 of the new release (not the 1.8.7 values above), and update the URL in [Network](network.md).

## Intune publish and cancellation

`IntunePublishService` passes the cancellation token to every step that waits.

| Test class | What it proves |
|---|---|
| `IntunePublishCancellationTests` | A publish that is cancelled while it waits for the token, for the storage address, for a block upload or for the commit ends within seconds. No request activates the app. The tenant stand-in blocks one step until the token of that request is cancelled. A second test cancels a publish during the wait between two reads (an interval of five minutes) and shows that it ends at once and sends no further request. |

**Lab check.** A real cancel test needs an Intune tenant, which the lab does not have. The stand-in tenant covers the same code path, because the service sends every request through one `HttpClient`.

## Third-party notices and the NSIS license

The installer is built with NSIS. The license text of NSIS is in `licenses\nsis-COPYING.txt`, and `THIRD-PARTY-NOTICES.md` names it.

| Test class | What it proves |
|---|---|
| `ThirdPartyNoticesTests` | Each file that the notices name is in the `licenses` folder, and each file in the folder is named in the notices. The NSIS license file is there, and the notices name its three licenses. The release stage copies the `licenses` folder, and the installer uses LZMA compression. |

**Lab check.** None. The tests read files in the repository, and no lab machine is needed.

## Update install: signature check just before the start

`UpdateService.ConfirmBeforeLaunchAsync` runs when the user chooses Install, after the download and the first checks and just before the installer starts. It confirms that the file has not changed (hash and size), reads the Authenticode signature again, applies the update signature policy, and requires the same signer subject as the first check. If any step fails, the installer does not start and the message ends with "The installer will not start."

| Test class | What it proves |
|---|---|
| `UpdateTests` (the `ConfirmBeforeLaunch` cases) | The check passes when nothing changed. It refuses a signature that is no longer valid, has no timestamp, or cannot be read. It refuses a signer that passes the policy but differs from the signer of the first check. It still catches a file that was swapped after the first check. |

**Lab check.** A unit test uses a stand-in for the signature reader. The real reader was checked on the ConfigMgr lab server with a real signed installer:

1. The genuine installer: status `Valid`, with a timestamp. Expected: the check passes.
2. A copy with one changed byte: status `HashMismatch`. Expected: the check refuses.
3. A file signed by another publisher: status `Valid` with another signer. Expected: the signer check refuses.

Result (2026-11-09): all three results matched. The lab machines were started for the check and stopped afterwards. No snapshot was used.

## ConfigMgr content distribution after a replace

After a run with `OnExisting=Overwrite`, the deployment types of the application are replaced and the content is distributed to the DP group again.

The review finding A1 said that the replaced content never reaches the distribution points. The lab did not show that. On ConfigMgr 5.00.9146 the new deployment type got a new content ID, and the site sent the new content to the DP at once; the file on the DP held the new text. The defect that the lab did show is in the second distribution request: the site refuses it with "No content destination was found ... the content has already been distributed". The old check looked for other words, so the run reported a failed distribution and skipped the test-collection deployment. The same answer also comes for a DP group that does not exist.

| Test class | What it proves |
|---|---|
| `PostPackageStepsTests` | The distribution script reads the site answers for content that is already there as done. It looks up the DP group first, and refuses the answer with "was not found" when the group does not exist. The lookup comes after the refusal, so a first distribution does not pay for it. |

**Lab check.** A probe application with two content folders (version one and version two) was distributed to the DP group. Its deployment type was then replaced the way the Overwrite path does it (new type, remove old type), and the generated distribution script was run.

1. Before the fix: the second request failed with the text above.
2. The new content was on the DP: its file held "version two", and the old content ID still existed until the site cleans it.
3. After the fix: the script printed "Content already targeted" for the existing group, and refused a group named `No Such Group` with "Distribution point group 'No Such Group' was not found."
4. The probe application and its source folder were deleted.

Result (2026-11-09): as described. The lab machines were started for the check and stopped afterwards. No snapshot was used.

## ConfigMgr publish: a refused step leaves no half-built application

When the site refuses a deployment-type step, the publish script removes what the run built. An application that the run created is removed. For an application that was already there (an Overwrite), the staging deployment types of the run are removed and the application stays as it was.

| Test class | What it proves |
|---|---|
| `ConfigMgrFailedDeploymentTypeTests` | The steps from the first deployment type to the rename run in a try block. The catch block goes back to the site drive, removes the application only when the run created it (the flag is set in the branch that creates it and nowhere else), removes the staging deployment types otherwise, and throws the original error again. |
| `SigningOffByteIdentityTests` | The recorded hashes of three generated scripts were changed on purpose, because the scripts now contain the try and catch blocks. |

**Lab check.** The site is the only judge of what it refuses, so this was run on the ConfigMgr lab server with scripts that the code generated, using a maximum run time of 1000 minutes (the site accepts 720 at most):

1. Before the change: the step was refused and the run ended, but the application stayed with 0 deployment types. A second run with good values was refused with "missing deployment type(s) ... fix or remove the partial app".
2. After the change: the same refusal printed "Removed the incomplete application" and the original error. The application was gone. A second run with good values created the application with 1 deployment type.
3. After the change, an Overwrite with the refused value: the application stayed with its 1 deployment type, and no staging deployment type was left.
4. The probe application and its source folder were deleted.

Result (2026-11-09): as described. The lab machines were started for the check and stopped afterwards. No snapshot was used.
