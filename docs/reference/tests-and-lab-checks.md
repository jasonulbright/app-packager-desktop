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

When the site refuses a deployment-type step, the publish script cleans up what this run built, with the function `Remove-ApkgIncompleteWork`:

- An application that the run created is removed.
- For an application that was already there (an Overwrite), only the deployment types that this run created are removed. The script records each name when it adds the type, and the function never asks the site for other types, so an operator's type, or one that an earlier run left, is out of reach even if it is named like a staging type.
- Once an old deployment type has been removed, nothing is removed: the new types are then the replacement, and the application needs them.

The original error is thrown again after the cleanup.

| Test class | What it proves |
|---|---|
| `ConfigMgrFailedDeploymentTypeTests` | The steps from the first deployment type to the rename run in a try block that calls the cleanup and throws the original error. The script records the created application, each added type and each removed old type. The cleanup function runs against stand-ins for the cmdlets: it removes a created application, reports a refused removal without failing, removes only the type of this run, never asks the site for other types, and removes nothing after an old type was removed. |
| `SigningOffByteIdentityTests` | The recorded hashes of three generated scripts were changed on purpose, because the scripts now contain the cleanup function and the try and catch blocks. |

**Lab check.** The site is the only judge of what it refuses, so this was run on the ConfigMgr lab server with scripts that the code generated, using a maximum run time of 1000 minutes (the site accepts 720 at most):

1. Before the change: the step was refused and the run ended, but the application stayed with 0 deployment types. A second run with good values was refused with "missing deployment type(s) ... fix or remove the partial app".
2. After the change: the same refusal printed "Removed the incomplete application" and the original error. The application was gone. A second run with good values created the application with 1 deployment type.
3. After the change, an Overwrite with the refused value: the application stayed with its 1 deployment type, and no other type was left. In this case the site refused the new type before it existed, so the lab did not show the removal of a created type. The stand-in tests cover that case.
4. The probe application and its source folder were deleted.

Result (2026-11-09): as described. The lab machines were started for the check and stopped afterwards. No snapshot was used.

## ConfigMgr publish: a skipped version is named

With `OnExisting=Skip` (the default of One Click and the command line), a run that finds the title on the site leaves the application alone and succeeds. A title without a version matches every version, so a new version is skipped too. Until now the log did not say so.

The log now has one line from `MecmTarget.ExistingApplicationSkipMessage`. For another version it reads: ConfigMgr holds the application at version 1.2.3; version 1.2.4 was NOT published by this Skip run; choose Overwrite to replace the deployment types. For the same version it says that nothing was changed. The result is the same as before (Success and AlreadyExists; exit code 0 for a ConfigMgr-only run; the outcome is "already present", and One Click records it as skipped); only the log line is new. On the Packages page, the first pass runs with Skip and then offers Overwrite, so the line can be followed by an overwrite when the user chooses it.

| Test class | What it proves |
|---|---|
| `ConfigMgrSkipMessageTests` | The full text of the message for a different version, the same version, an unread site version and an empty staged version. The decision after the probe (`DecideAfterProbe`, called by `PackageAsync`): no title lets the run go on; Skip ends it with success and logs the site version first and the staged version second; Fail ends it with the refusal and logs no Skip line. |

**Lab check.** The message is built from the version that the site returns, so the probe script was run on the ConfigMgr lab server against a real application at version 1.2.3 with an incoming version of 1.2.4. Expected and seen: the conflict line `existing app='...' version='1.2.3' incoming='1.2.4'`, which is the input of the message. The probe application was deleted afterwards.

Result (2026-11-09): as described. The lab machines were started for the check and stopped afterwards. No snapshot was used.

## ConfigMgr publish: a failed content step fails the run

After a ConfigMgr application is created, a run can distribute its content to a DP group and deploy it to a test collection. If one of these steps fails, the application exists, but clients cannot install it. Until now the failure went to the log and the run still ended with success (review finding D-6).

Now `DistributeAsync` and `DeployToTestCollectionAsync` return a result with the reason, `RunPostPackageStepsAsync` passes it on, and `PackagingOutcomeComposer.Compose` fails the run (exit code 1). The inventory record of the ConfigMgr destination is Partial and carries the sentence as its detail. The message is one sentence: "ConfigMgr application created, but a step after it failed: Content distribution to 'All DPs' failed: <first line of the provider text>". If the Intune publish also failed, the message names both. A distribution that fails skips the test deployment, as before. An Intune-only run has no ConfigMgr step and is not changed. The full provider text stays in the log.

| Test class | What it proves |
|---|---|
| `ConfigMgrContentStepOutcomeTests` | The two steps run in their order through `MecmTarget.RunContentStepsAsync`: a failed distribution skips the test deployment and returns its sentence; a failed test deployment after a good distribution returns its sentence; two good steps return nothing; without a test deployment only the distribution runs. The inventory records the ConfigMgr destination as Partial when the application exists and a content step failed, and the summary of the destinations treats Partial like a failure (its earlier rules are kept). A failed content step fails a ConfigMgr-only run and keeps the reason; no failure (or a blank one) keeps the success; a run that published to Intune is still failed by the content step; a failed Intune publish and a failed content step are both reported; an Intune-only run is not changed; the sentence names the step, the target and the first line of the provider text, cuts a very long text, and gives a message for an empty reason. |
| `ApplicationInventoryServiceTests` and `OneClickRowRunnerTests` | A Partial ConfigMgr outcome keeps the version, the object id and the CI_ID of the application as the last success, and the attempt outcome says Partial with the reason. A One Click run whose content step failed records Partial with the CI_ID and shows the row as failed. |

**Lab check.** The provider text is the input of the sentence, so the distribution script was run on the ConfigMgr lab server with an application that has no content. The site refused it with "There is no object that can distribute content." The earlier check of the distribution script (page section on content distribution after a replace) showed the text for a DP group that does not exist: "Distribution point group 'No Such Group' was not found." Both are single lines that the sentence takes as the reason. The probe application was deleted afterwards.

Result (2026-11-09): as described. The lab machines were started for the check and stopped afterwards. No snapshot was used.

## ConfigMgr publish: values that the site refuses are refused first

Three checks of the ConfigMgr publish accepted values that the site then refused, or refused a value that the format page allows (review findings A6 and C4).

- **Maximum run time.** The cmdlet takes 15 to 720 minutes (read from the cmdlet on the lab server: `ValidateRange(15, 720)` for `MaximumRuntimeMins`). `DeploymentTypeTiming.Resolve` allowed 1 to 1440 and only logged a value above 720. It now refuses a maximum outside 15 to 720 before the site is touched, and the estimate must be 1 to 720. The Workbench shows a Review finding (`TIMING-CONFIGMGR-RANGE`) for such a profile. It blocks nothing, because an Intune-only publish does not send the value.
- **Operators `Between`, `OneOf`, `NoneOf`.** The definition carries one expected value, and these operators need two values or a list. They were in the accepted list and failed at the site. They are removed from the list, so a definition that uses them is refused with the message that names the value and the accepted ones. No shipped definition uses them.
- **Registry value `Existence`.** The format page allows `PropertyType: Existence` for `RegistryKeyValue`, and the builder refused it. The cmdlet has an existence form that takes the key, the value name, a property type and `-Existence`, with no operator and no expected value. The builder now sends that form (`-PropertyType String -Existence`, with `-Is64Bit` when set).

| Test class | What it proves |
|---|---|
| `StageFinalizationPublishTests` | A maximum of 14, 721 and 1441 minutes is refused with the range 15 to 720; 15 and 720 are accepted; the earlier refusals (estimate 0, estimate above maximum) are kept. |
| `ConfigMgrPrecheckTests` | The three list operators are refused for a registry value and for a file, with a message that does not offer them; the operators that take one value still produce the same clause; a registry value existence produces the exact clause of the existence form, without an expected value, and keeps the 64-bit switch; the property type Existence is read without regard to case or surrounding spaces, for a registry value and for a file. |
| `WorkbenchFindingsTests` | A maximum of 14, 721 and 1440 gives the Review finding, 15 and 720 give none, and the finding blocks no target. |

**Lab check.** The generated script for a `RegistryKeyValue` definition with `PropertyType: Existence` was run on the ConfigMgr lab server. Before the change the builder refused this definition. After the change the site accepted the clause: the application and its deployment type were created, and the detection of the deployment type names the value `InstallDir` and the 64-bit setting. The rule type in the stored detection was not decoded; the site accepted the form and reported no error. The probe application and its source folder were deleted afterwards.

Result (2026-11-09): as described. The lab machines were started for the check and stopped afterwards. No snapshot was used.

## ConfigMgr publish: a replace writes the Software Center name of the manifest

A replace (`OnExisting=Overwrite`) replaced the deployment types and wrote the version and the publisher, and nothing else (review finding A7). The application kept its old Software Center name, although a new application gets the name from the manifest.

The replace block now writes the Software Center name (`-LocalizedApplicationName`) when the manifest has a display name. Without a display name, the name is not touched. The run comment is written as the description when there is one, as before.

An empty comment does not clear an existing description. The review finding asked for that, but the comment is a value of the run, not of the manifest. The command line (without `--comment`) and One Click (with an empty comment field) pass an empty comment when the operator gave none. A replace that cleared the description for them would remove a note that an administrator typed in the console, without any message. Clearing a description on purpose stays a decision for the owner; it needs a way to tell "no comment given" from "comment cleared", which the run settings do not have today.

| Test class | What it proves |
|---|---|
| `ConfigMgrReplaceMetadataTests` | The replace block carries the Software Center name of the manifest, and none when the manifest has no display name or an empty one. It carries the run comment as the description, and no description for an empty or blank comment. A new application and a replace carry the same name and comment. Quotes in the name and the comment are escaped. |
| `SigningOffByteIdentityTests` | The recorded hash of the generated script of a replace was changed on purpose. |

**Lab check.** The site decides what the console shows, so the generated scripts were run on the ConfigMgr lab server. An application with one display language was created with the Software Center name "Old Software Center Name" and the comment "old comment". Then a replace ran with the name "New Software Center Name" and an empty comment.

1. Before the change: after the replace, the name was still "Old Software Center Name", and the description was still "old comment". The script before the change leaves out `-Description` for an empty comment, as the final code does.
2. After the change: the name was "New Software Center Name".
3. The lab run of the change used an intermediate script that wrote the empty comment, so the description was empty in that run. The final code leaves out `-Description` for an empty comment, as the script before the change did (item 1). The tests check the script text. The final script was not run on the lab server.
4. The probe application and its source folder were deleted.

Result (2026-11-10): as described. The lab machines were started for the check and stopped afterwards. No snapshot was used.

## WSUS publish: installer exit codes of the definition

An EXE installer can report exit codes of its own. A definition lists them as `RebootExitCodes` (reboot required) and `SuccessExitCodes` (a complete install). For ConfigMgr and Intune the generated wrappers turn them into 3010 and 0. WSUS runs the installer itself, and a code that is not in its return codes counts as a failed install. The shipped definition `dellcommandupdate.json` declares `WsusSupport: Yes` and lists `RebootExitCodes: [2]`, so a successful install would have been recorded as failed (review finding C1).

The return codes of a WSUS update are built for each publish, so they now carry the codes of the definition: `WsusPublishService.ReturnCodesFor` adds each reboot code as a success with a restart and each success code as a success, after the fixed codes 0, 1707, 3010 and 1641. A code that the fixed table has keeps the meaning of the table, and appears once. An MSI gets the fixed codes only, because the wrappers apply the codes to EXE installers only. The stage manifest carries both lists (`RebootExitCodes`, `SuccessExitCodes`), written when the application is staged.

A manifest that was staged before the manifest carried the lists has none. When the publish reads such a manifest, `WsusPublishService.CarryExitCodes` takes the lists from the definition of the application, so an older stage does not publish with the fixed codes. A manifest that has lists is left as it is. The One Click run, the Packages page, the Workbench and the command line all publish through this one place.

| Test class | What it proves |
|---|---|
| `WsusReturnCodesTests` | Without codes the table is the fixed one; a reboot code becomes a success with a restart and a success code a success; a code that the table has keeps the meaning of the table and appears once; an MSI and empty lists add nothing; an older manifest takes the codes from the definition, a manifest that has codes keeps them, and without a definition nothing changes. |
| `WsusPublishServiceTests` | The plan that goes to the adapter carries the codes of the definition after the fixed ones. |

**An update that is already on the server.** The return codes belong to an update when it is created. A repeat publish of a version that has an update which is not expired sends only the id of that update and reapplies the approval, so the update keeps its old return codes. An update of Dell Command Update that was published before the release of the return codes must be expired in the WSUS Updates window and published again; the repeat publish then creates a new update with the new codes. The section after this one describes how a repeat publish notices such a difference.

**Lab check.** None. The lab has no WSUS server. The tests check the plan that the publisher sends to the adapter; they do not show how a WSUS client reads the return codes. The adapter already creates each return code from the plan (`ReturnCodeValue`, `InstallationResult`, `IsRebootRequired`), and this change adds entries to the same list.

## WSUS publish: a repeat publish of a changed stage

A WSUS update cannot change after it is published. A repeat publish of a version whose update is on the server (and not expired) sent only the id of that update and reapplied the approval, and the result said "already on the server". A corrected install command, rule, return code or file never reached the server, and nothing said so (review finding C2).

Each update that App Packager creates now carries a line in its description, `AppPackager plan: <16 hex characters>`. It is a digest of the command line, the installer type and file, the rules (`IsInstalled`, `IsInstallable`), the return codes and the names and SHA-256 hashes of the files (`WsusPlanDigest`). A PowerShell script (`.ps1` or `.psm1`) in the payload is hashed without its signature block, because App Packager signs its scripts again at each staging and a timestamped signature has other bytes each time. A script that is signed again with the same body keeps the digest; an edit of the body changes it. A repeat publish of the same version compares the digest of its stage with the line of the update on the server:

- **Same digest:** as before. The result is "already on the server", and the approval is reapplied.
- **Different digest:** the publish stops before it changes anything, with the failure "Version X is already on the server, and this stage differs from it ... WSUS cannot change a published update ... Expire it in WSUS Updates, then publish again." After the update is expired, a repeat publish creates a new update with a new id. The step fails; it is not a refusal, so One Click does not remember it as "not supported".
- **No line:** the update was published by an earlier release. The result is "already on the server", as before, with a note in the log that its install command, rules, return codes and files were not compared with this stage. The note is a log line and not a warning, because a warning makes a command-line build end with exit code 1 at every run.

| Test class | What it proves |
|---|---|
| `WsusPlanDigestTests` | The digest is 16 hex characters and stable; each input changes it, also when a value moves to another field or a companion file is renamed; a script is hashed without its signature, so a new signature keeps the hash and an edit of the body changes it; the line round trips through a description without disturbing the identity line; a description without a valid line has no digest. |
| `WsusPublishServiceTests` | The plan description ends with the digest line; a repeat publish of the same stage finds the update unchanged and warns about nothing; a changed install command, reboot code, success code or detection fails with the kind `UpdateDiffers`, names the remedy, and sends no publish request; an update without a digest is reported with a log note and no warning; a script signed again with the same body does not make the update differ, and a corrected script body does; a changed installer file fails; a declined update that differs fails and says it is declined; the failure is a failed step and not a refusal. |

**Lab check.** None. The lab has no WSUS server. The tests use the fake adapter that the other publish tests use, so they show what the publisher sends and decides, not how a WSUS server stores the description.

## Tests that failed at random in the full run

In five full runs on 2026-11-10 (CI and the release workflow), a different test failed each time, and each passed on the next run of the same code. A release cannot start while the run on the commit is red, so each failure cost a rerun of about 40 minutes. The causes were read from the code and the CI logs; none of them could be made to fail on purpose, so the fixes have no test that was red first.

| Test | Cause | Fix |
|---|---|---|
| `CollectionComplianceViewModelTests.ADetailThatArrivesAfterTheSelectionChangedIsDiscarded` | Test race. The view model reads a detail on a pool thread, and the fake service looked at its gate only when that read started. The test cleared the gate right after the selection, so the first read could see the new, empty gate and finish at once. | The test waits until the read has started before it changes the gate. The fake service reads the gate before it counts the call. |
| `SiteHygieneLiveTests.TheLiveChoicesRoundTripAndValuesNeverSetStayNull` | Test race. Each property setter starts a save, so the SQL server and the database can be written by two saves; the test read the last save only. | The test reads what is stored in the settings. |
| `AddFileViewModelTests.APackageShowsItsIdentityAndAProminentTrustWarning` | Probable test and environment cause. The test waits for two steps on the thread pool with a limit of 10 seconds, and a full run keeps the pool busy. | The limit of `StaTestHelper.PumpUntil` is 60 seconds; a passing test still returns as soon as its condition holds. |
| `WrappedMsiAndInstallerCopyTests.AnExeWithNoMsiInside_KeepsNoProductCodeAndSaysWhy` | Product race, probable. A listing of a small archive can end before 7-Zip is assigned to its job object, Windows then refuses the assignment, and the listing failed with "could not be listed". | `SevenZipTool` goes on when the process has already ended: it has nothing left to stop, and 7-Zip starts no child process. |
| `PostInstallKillProcessesTests.TheWrapperReturnsTheInstallerExitCodeAndStopsTheLaunchedChild` | Product race in the install wrapper (a fast installer exit is read as 0). Not part of this change; it has its own change. | None here. |

**Lab check.** None. These are timing faults of tests and of one process start; the lab adds nothing. The proof is that the full run no longer fails at random, which needs several runs.