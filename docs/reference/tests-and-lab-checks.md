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

Now `MecmTarget.RunContentStepsAsync` (one PowerShell start for both steps) returns the sentence of the failed step, `RunPostPackageStepsAsync` passes it on, and `PackagingOutcomeComposer.Compose` fails the run (exit code 1). The inventory record of the ConfigMgr destination is Partial and carries the sentence as its detail. The message is one sentence: "ConfigMgr application created, but a step after it failed: Content distribution to 'All DPs' failed: <first line of the provider text>". If the Intune publish also failed, the message names both. A distribution that fails skips the test deployment, as before. An Intune-only run has no ConfigMgr step and is not changed. The full provider text stays in the log.

| Test class | What it proves |
|---|---|
| `ConfigMgrContentStepOutcomeTests` | The two steps run in one script (see "ConfigMgr publish: fewer PowerShell starts"): a failed distribution ends the script before the test deployment; the exit code names the step that failed; without a test collection only the distribution is in the script. The inventory records the ConfigMgr destination as Partial when the application exists and a content step failed, and the summary of the destinations treats Partial like a failure (its earlier rules are kept). A failed content step fails a ConfigMgr-only run and keeps the reason; no failure (or a blank one) keeps the success; a run that published to Intune is still failed by the content step; a failed Intune publish and a failed content step are both reported; an Intune-only run is not changed; the sentence names the step, the target and the first line of the provider text, cuts a very long text, and gives a message for an empty reason. |
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
| `WrappedMsiAndInstallerCopyTests.AnExeWithNoMsiInside_KeepsNoProductCodeAndSaysWhy` | Product race, probable. A listing of a small archive can end before 7-Zip is assigned to its job object, Windows then refuses the assignment, and the listing failed with "could not be listed". | `SevenZipTool` goes on when the process has already ended: it has nothing left to stop, and 7-Zip starts no child process. `SevenZipPolicyTests` has a test for the case (the job assignment waits for 7-Zip to end and then fails); the test of the refusal decides by a seam that the process is still running, so it does not depend on timing. |
| `PostInstallKillProcessesTests.TheWrapperReturnsTheInstallerExitCodeAndStopsTheLaunchedChild` | Product race in the install wrapper (a fast installer exit is read as 0). Not part of this change; it has its own change. | None here. |

**Lab check.** None. These are timing faults of tests and of one process start; the lab adds nothing. The proof is that the full run no longer fails at random, which needs several runs.

## EXE install wrapper: the exit code of an installer that ends at once

Some definitions list processes to stop after the install (`PostInstallKillProcesses`; today Picpick, HashTools and Windows Admin Center). For them the install wrapper does not use `Start-Process -Wait`, because `-Wait` also waits for every process that the installer started, and an installer that starts its application would never return under a system-context deployment. The wrapper waits for the installer process alone and then stops the named processes.

It started the installer with `Start-Process -PassThru` and read the handle afterwards. A process from `Start-Process -PassThru` without `-Wait` holds no handle, so when the installer ended before the script read the handle, Windows PowerShell gave a null `ExitCode` and the wrapper exited 0. An installer that failed at once (for example exit code 1603) was reported as a success (found when `PostInstallKillProcessesTests` failed at random in a full run: expected 5, actual 0).

The wrapper now starts the installer with `[System.Diagnostics.Process]::Start`, which holds the handle from the start, and it reads `ExitCode` after `WaitForExit()`. The arguments go into `ProcessStartInfo.Arguments` as one string, which is what `Start-Process -ArgumentList '<string>'` passed. If the installer cannot start, the null-process guard exits 1.

| Test class | What it proves |
|---|---|
| `PostInstallKillProcessesTests` | The exact text of the new start; no arguments line for empty arguments; an apostrophe in the arguments is escaped. An installer that exits 5 at once and a wrapper that looks at it 1.5 seconds later (a pause put right after the start line) still gives 5; this test fails with the old wrapper. The earlier test of the stand-in installer that starts a child and exits 5 (the child is stopped, the code is 5, the file holds 5) passes with the new start. |

**Lab check.** The old and the new text of the wrapper were run with Windows PowerShell 5.1 on the ConfigMgr lab server, with a stand-in installer (a copy of `cmd.exe` that exits 5 at once):

1. Old wrapper, 120 runs, 8 at a time: exit code 5 in all 120. The race did not show on its own.
2. New wrapper, the same 120 runs: exit code 5 in all 120.
3. Old wrapper with a pause of 1.5 seconds right after the start line (a host that looks at the process late), 16 runs: exit code 0 in all 16. The installer's code was lost.
4. New wrapper with the same pause, 16 runs: exit code 5 in all 16.

So the loss needs a host that looks at the process after the installer has ended. It was seen once in CI (`PostInstallKillProcessesTests`, expected 5, actual 0), and the pause reproduces it every time. The lab machines were started for the check and stopped afterwards. No snapshot was used. The probe folder was deleted.

Result (2026-11-10): as described.

## WSUS publish result: a first install is not possible

A WSUS update applies only where the detection finds the application with an older version. WSUS does not install the application on a computer that does not have it. The result of a WSUS publish said "Published" or "Already on the server" and did not say that (review finding C3).

The result now says it in three places: the WSUS result message and the log line (`published <id>; applies only where the detection finds an older version of the application, so WSUS cannot give a first install of it`), the status of a WSUS-only publish row (`Published (updates only)`, `Already on the server (updates only)`, with `, warning` after it when a follow-up step failed), and the detail of the WSUS result in One Click and its report.

| Test class | What it proves |
|---|---|
| `WsusPublishServiceTests` | The message of a new update carries the note. |
| `PublishRowOutcomeTests` | The status of a WSUS-only row carries "(updates only)" for a new update, a reused update, and each with a warning. The detail of a success without a warning carries the note. |
| `PublishRunServiceTests` | The command-line publish row for a new update and for a repeat publish carries the new status. |
| `OneClickPlannerTests` | The WSUS result of a published or reused update carries the note as its detail. The cell stays "published <id>". |

**Lab check.** None. The change is wording. The lab has no WSUS server.

## Intune publish: a file detection on size or date

A file detection on the size or on a date of the file was published to Intune as "file exists". The rule detected a different state than the definition and the ConfigMgr application did (review finding B1). No shipped definition uses these properties, but an imported or hand-made definition can.

The Intune rule now keeps the property:

- `Size`: the definition carries bytes and the Graph rule compares the size in megabytes **rounded down** (`sizeInMB`, as the Microsoft Graph reference states). A size that is not a whole number of megabytes (and a size of 0 or a text that is not a number) is refused. Only the operators `GreaterEquals` and `LessThan` give the same answer as a comparison in bytes against a whole number of megabytes; `IsEquals`, `NotEquals`, `GreaterThan` and `LessEquals` are refused with that reason.
- `DateModified` and `DateCreated`: the rule is `modifiedDate` or `createdDate`, and the value is sent in UTC in whole seconds. A value without a UTC offset, one that is not a date, or one with fractions of a second is refused, because the offset is not known, or the whole-second value would move the boundary.
- `Existence` (or no property) still gives "file exists". Any other property is refused and does not become "file exists".
- The operator is mapped as before. A string operator (`BeginsWith` and others) on a size or date is refused.

| Test class | What it proves |
|---|---|
| `IntuneRuleMappingTests` | A size of whole megabytes gives `sizeInMB` with the megabyte count; sizes that are not whole megabytes, zero, negative, text, and empty are refused; `LessThan` is accepted; `IsEquals`, `NotEquals`, `GreaterThan` and `LessEquals` are refused because of the rounding down; a date with fractions of a second is refused; a date with an offset gives `modifiedDate` or `createdDate` in UTC; a date without an offset, a text that is not a date, and an empty value are refused; `Existence` in any case gives "file exists"; an unknown property is refused; a string operator on a size is refused; a size clause in an AND compound keeps its rule. |

**Lab check.** None. The lab has no Intune tenant, and the Graph operation names (`sizeInMB`, `modifiedDate`, `createdDate`) and the rounding down of the size are taken from the Graph reference. The text format of a date comparison value is not stated there. The tests check the rule that the program builds, not how the service stores it. This stays unverified against a tenant.

## Intune publish: the comparison type of a registry value

The Intune rule of a registry value chose its comparison type from the format of the expected value: a value that `System.Version` could parse compared as a version, anything else as text. A value of one number (`124`, `7`) and every integer property compared as text, so `10` was lower than `9` (review finding B2).

The rule now takes the type from the property type of the detection:

- An integer property type (`Integer`, `Int`, `Int32`, `Int64`, `DWORD`, `QWORD`) compares as an integer. A value that is not an integer is refused.
- `Version` compares with version semantics, also for a version of one number. A value that is not a dotted version of one to four numbers (for example `1.2.3-beta`) compares as text, as before.
- `String` or no property keeps the guess from the format of the value. The manifest cannot tell a declared `String` from the default that stage writes, so a change here would turn working rules into text comparisons.
- `Existence` gives the rule "value exists". Any other property type is refused.

| Test class | What it proves |
|---|---|
| `IntuneRuleMappingTests` | Each integer type name (in any case) gives the integer comparison with the same value; a value that is not an integer, empty, with a space, or too large is refused; a version of one number or of several gives the version comparison; a version property with a value that is not a dotted version gives text; `String` or no property keeps the guess (`1.2.3.4` is a version, `124` is text); an unknown property type is refused; `Existence` gives the exists rule for the named value; an integer and a version clause in an AND compound keep their rules. |

**Lab check.** None. The lab has no Intune tenant. The names of the comparison types come from the Microsoft Graph reference (`string`, `integer`, `version`). The tests check the rule that the program builds, not how the service compares at the client. This stays unverified against a tenant.

## Intune publish: the minimum Windows release is a setting

Every Intune app was published with the minimum Windows release `Windows10_21H2`, and no setting changed it (review finding B3). The Settings page now has **Minimum Windows release** in the Intune section. The value is stored in the Intune settings, the default is `Windows10_21H2` (a settings file from an earlier release gets it), and every publish to Intune sends it, also the publish of a ConfigMgr conversion. A value that is empty or not of the shape `Windows10_<release>` or `Windows11_<release>` gives the default. A well-formed value that the list on the page does not know goes through to Graph, so that a newer release can be set in the settings file before the list has it.

The list on the page (`Windows10_1607` to `Windows11_25H2`) is written from the names that Intune uses. The Graph reference gives only the example `Windows11_23H2`, so the other names are not checked against a tenant.

| Test class | What it proves |
|---|---|
| `IntuneWindowsReleaseTests` | The default is `Windows10_21H2` in the options class and in the settings; the list contains the default, has no duplicate, and every entry has the shape; an empty, blank or unknown-case value resolves to the canonical name; a malformed value gives the default; a well-formed value that the list does not know passes through; the setting survives a JSON save and load of the settings class, and a file without it gets the default (the Settings page code that loads and saves the box is checked by the build only). |
| `IntunePublishServiceTests` | The app body carries the release of the options (a known release, a lower-case release in its canonical spelling) and the default for an empty or malformed value. The update of an app that is already in Intune carries the release too. |

**Lab check.** None. The lab has no Intune tenant. The tests check the body that the program sends. They do not show that a tenant accepts each name in the list, or whether Graph refuses a release that it does not know (a well-formed value that is not in the list goes through). On a repeat publish such a refusal would come at the last step, after the upload.

A per-definition override of the release is not part of this change.

## Intune publish: requirement rules

The Intune publish did not send the requirement rules of an application. It logged a review finding, and the Win32 app had no gating, so it could install on devices that ConfigMgr skips (review finding B4). The owner decided: translate every rule that Graph can carry, and block the publish when a rule cannot be translated, unless the administrator accepts the drop of that rule.

The three built-in conditions and any template of the same shape become PowerShell requirement rules (`win32LobAppPowerShellScriptRule`, rule type `requirement`, compared as the string `True` or `False`): a WMI value (`cpu-arch`), the operating system language (`os-language`), and a script condition that gives a boolean (`vpn-connected`). The rule list of the app is the detection rules followed by the requirement rules. A rule with no Intune form gives the blocking finding `RequirementNotTranslatable`; the drop is accepted per application and rule with the entry `<identity tag>|<condition id>` (the finding prints it) in the setting `Intune.AcceptedRequirementDrops`, which turns it into the review finding `RequirementDropped` and leaves the rule out. The panel button for this comes with the preflight panel (batch D).

| Test class | What it proves |
|---|---|
| `IntuneRequirementRuleTests` | Each built-in condition gives a requirement script rule with the right script, expected value, account and display name; the x64 and ARM64 values are mapped like the ConfigMgr rule; an unknown condition, an unmapped value, an empty culture list, a condition kind with no script form and a WMI property name that is not a plain name each give a gap and no rule; a quote in a value is escaped; a long name is clipped to 100 characters; the findings are informational for a translated rule, blocking for a gap, review for an accepted drop; an accepted drop of another rule or another application, or of a bare condition id, does not count; the key is the identity tag of the application, which does not change with the version; a culture code that does not exist gives a gap and not an exception; the saved conditions of a run replace the manifest rules; a script condition is signed under a signing policy, and a required signature that is not produced stops the publish before the first Graph call; the match is not case-sensitive; one accepted drop does not hide a second gap. |
| `IntunePublishServiceTests` | The request body carries the requirement rules after the detection rules; a gap stops the publish before the first Graph call; an accepted drop leaves that rule out and keeps the others; the requirements that the run applies beside the manifest are sent; an app without requirements sends only the detection rules. |
| `IntuneCompatibilityAndIdentityTests` | The findings of an application with requirements: translated rules are informational and a missing architecture still needs review. |

**Lab check.** None. The lab has no Intune tenant. The tests check the rules that the program sends. They do not show how the Intune agent runs the scripts or compares their output: the use of the string comparison with `True`, the system account, the language read from `InstalledUICulture`, and the display name length are not validated against a tenant. A WMI condition with several rows is read as "some row has the value", which is a choice of this program; ConfigMgr's own behavior for several rows is not documented.

## Intune publish: the restart behavior follows the post-execution behavior

Every Intune app was published with the restart behavior `basedOnReturnCode`, whatever the post-execution behavior of the deployment type said, so `ForceReboot` had no effect (review finding B5). The restart behavior now follows the behavior: `NoAction` gives `suppress`, `ProgramReboot` gives `allow`, `ForceReboot` gives `force`, and `BasedOnExitCode` or no value gives `basedOnReturnCode`. The conversion of a ConfigMgr application used this mapping already, and now both use one function. The two shipped definitions that set `ForceReboot` (the Visual C++ runtimes) now restart devices on Intune.

| Test class | What it proves |
|---|---|
| `IntuneCompatibilityAndIdentityTests` | Each behavior gives its restart behavior (also in other letter case, with spaces, with no value, and with an unknown value); the conversion of a ConfigMgr application gives the same result as the publish for every behavior. |

**Lab check.** None. The lab has no Intune tenant. The tests check the value that the program sends.

## Intune publish: an unchanged package is not uploaded again

A repeat publish of an app always created a new content version and uploaded the whole payload, even when the `.intunewin` file was the one that the app already held (review finding B6). The notes of the app now record the digest of the package (`AppPackagerContent:<digest>`, after the identity tag), and a repeat publish whose package has the same digest, for an app that has committed content, skips the upload and updates only the app.

| Test class | What it proves |
|---|---|
| `IntunePublishServiceTests` | An unchanged package makes no content-version request and no blob upload, and sends one update of the app without `committedContentVersion` and with the identity and content lines; the metadata of the app (minimum release, commands) still changes; a different digest uploads, commits and records the new digest; an app with the digest in its notes but no committed content uploads; an app from an earlier release (no digest line) uploads once and records the digest; a new app records the digest in the notes of the create; the lines of the operator are kept and an old digest line is replaced; a package with no digest always uploads and writes no digest line; a failed update of an unchanged package fails the publish. |
| `IntuneCompatibilityAndIdentityTests` | The notes keep the identity tag first and the content line after it, in the style of the operator's line endings; the content line is never an identity tag (it does not change which app is found); an exact line match is needed. |

**Lab check.** None. The lab has no Intune tenant. The tests check the requests that the program sends. They do not show that Graph accepts an update of an app without a new content version, which the program relies on: the update sends the same properties as the update that activates a new content version, except `committedContentVersion`. They also do not show that the list of Win32 apps returns `committedContentVersion` (if it did not, the publish would always upload, and no publish would be wrong). This stays unverified against a tenant.

## Intune publish: retries, token renewal and upload address renewal

The Intune publish sent each request once. A throttled call, a busy service, a lost connection, an expired token or an expired storage address failed the whole publish, and a large upload could be lost at its last block (review finding B7). The publish now retries as described in [Publish to Intune](../core/publish-to-intune.md#retries-new-tokens-and-new-upload-addresses). The rules are in `IntuneRetry`: a refusal for load (429, 503) is repeated for every request except the create of the app, which is repeated only after 429 (a front end can answer 503 after the app was made); the other transient failures (408, 500, 502, 504, a lost connection) only for a request that can be repeated without harm; a create is never repeated after such a failure, because a second app could result.

| Test class | What it proves |
|---|---|
| `IntuneRetryTests` | Which statuses and which failures are transient for a repeatable and for a non-repeatable request; the wait doubles with each retry, adds at most a quarter at random, never passes the maximum and does not overflow; `Retry-After` (seconds or date) replaces the computed wait and is honored up to 5 minutes, also beyond the 60 s maximum of the computed wait; the create of the app is repeated after 429 only; a base delay of zero means no wait; the defaults (5 retries, 2 s, 60 s, 3 renewals). |
| `IntunePublishServiceTests` | A throttled read, a create refused for load, a read that lost its connection, an update after a bad gateway, a blob block after a service error and an error of the token endpoint are sent again and the publish succeeds. A create after an error 500 or a lost connection is sent once. The retries stop at the configured number, and at 0 each request is sent once. Answers 400, 404 and 409 are not repeated. An expired upload address (403) is renewed, the upload continues with the new address, refusals in a row are limited and expiries that are not in a row each get a renewal, an address that is the one in use is not accepted as the renewed one, the renewal request is repeated after a server error, a failed renewal stops the publish, and no renewal happens when 0 are allowed. The create of the app is not repeated after 503 and is repeated after 429; the content version create is repeated after 503. A refused token is renewed once and not in a loop. A cancel during a wait stops the publish. The retry log names no query of the storage address. |

**Lab check.** None. The lab has no Intune tenant, and the tests use scripted answers. They do not show how Graph or the storage service answer in practice: the `renewUpload` call and the states `azureStorageUriRenewalSuccess`, `azureStorageUriRenewalFailed` and `azureStorageUriRenewalTimedOut` follow the Graph reference for a content file, and the use of 403 as the answer for an expired address is the usual answer of Azure storage for an expired shared access signature. These stay unverified against a tenant.

## Intune publish: all four hardware minimums of a converted app

The publish of a converted ConfigMgr application copied the minimum memory and the minimum number of processors and dropped the other two hardware minimums of the Graph resource, the minimum free disk space and the minimum CPU speed (review finding B8). The PowerShell app passes all four. The publish now sends all four, and sends null (the value of an app with no minimum) for one that the plan does not carry, so that a repeat publish clears a minimum that an earlier publish set and that the app no longer has, or that someone set by hand in the Intune admin center. The PowerShell app omits an unset minimum and never clears it. A negative minimum is refused before the first Graph call.

The importer still derives only memory and processors from ConfigMgr rules. A disk space rule of ConfigMgr has a partition option (the system drive, any drive, or one drive letter) that the single Intune value cannot carry, and the logical name and the unit of the CPU speed rule are not verified, so those rules stay "not converted". Only a plan that carries the values sends them.

| Test class | What it proves |
|---|---|
| `IntunePublishServiceTests` | A converted plan with all four minimums sends all four properties; a plan with one sends null for the other three; the update of an existing app sends the nulls; a key that is not a hardware minimum is ignored; a negative minimum stops the publish before any request; a publish of a stage that is not converted sends no hardware minimum. |
| `CmImportPublishTests` | The converted plan still sends the memory and the processor minimums that the importer derives, and null for the disk and CPU speed minimums that it does not derive. |

**Lab check.** None. The lab has no Intune tenant. The tests check the body that the program sends. They do not show that Graph accepts the four properties, with null for an unset one, on create and update.

## ConfigMgr publish: the native MSIX deployment type is removed

The ConfigMgr publish had a second path for a manifest with the detection type `MSIX`: the native Appx deployment type (`Add-CMWindowsAppxDeploymentType`). The lab run of the review showed that this path failed on every call with a UNC content path (the search for the package file used `Get-ChildItem -Filter` on a UNC path) and that a second publish ended with exit code 0 without reading the behavior for an existing application (review finding A2). The PowerShell app publishes an MSIX only as a script deployment type, and the owner decided that App Packager does the same. A staged MSIX already has wrappers and a script or provisioning-key detection, so the script path publishes it with no change.

The native script builder is removed. A manifest with the detection type `MSIX` is refused at the start of the publish, before any share or site work, with the message that the native type is no longer published and that the application is to be staged again. No definition that the program writes and no current stage of an MSIX produces that type for a publish: the dropped-installer route already refuses an MSIX and sends the user to a definition. A hand-written definition of another installer type whose detection rule has the type `MSIX` still stages that manifest and is refused at publish (the message says to change the rule).

| Test class | What it proves |
|---|---|
| `ConfigMgrNativeMsixRemovedTests` | A manifest with the detection type `MSIX` (in any letter case) is refused with a message that names the native type, the script deployment type and Stage; the other detection types are not refused; the package run stops before it connects or touches the share; the native script builder no longer exists. |
| `MecmScriptBuilderTests` | A script-staged MSIX still builds a script deployment type and does not use the native cmdlet (unchanged test). |

**Lab check.** None. The change removes a path. The script path for an MSIX was run in the lab in the review (the MSIX scripts of that run passed).

## A concurrency test of the metrics history failed on a loaded disk

On 2026-11-11 (01:05 UTC on 2026-11-12) the run of CI on a merge commit failed once in `LiveHistoryAndAlertTests.ConcurrentAppendsFromTwoStoresKeepEveryWholeLineAndOneHeader` (expected 401 lines, found 400) and passed on the rerun of the same code. Cause, from reading the code (the run had no log of the store, so the warning was not seen): the test made 400 appends from eight threads. Each append holds the lock file of the history file for the time of a flush to disk, and a store that waits for the lock gives up after 5 seconds and does not record that refresh (a documented choice of the store, with a log warning). The lock is not fair: the thread of the other store that is queued on the store's own lock takes the lock file again in the short gap, so on a loaded CI disk one store can wait through a long run of the other store's appends. The code shows that a cut or joined line cannot occur under the lock; the count of 400 fits one lost row. The store now takes an optional lock wait (the default stays 5 seconds), the concurrency test gives both stores a wait of two minutes and collects their warnings (a lost row now names its cause), and it makes 60 appends from four threads. Two new tests prove the wait itself with the lock file held by the test: an append waits and then writes its row, and an append that cannot get the lock file in time records nothing and logs the warning.

**Lab check.** None. It is a timing fault of a test.

## ConfigMgr publish: an icon that was not applied is a warning, and dead code is removed

The ConfigMgr publish script prints `Application icon not applied` or `Application icon not found` when the icon of the application cannot be set, and carries on, because a missing icon does not stop the packaging. The run then read as a plain success and nothing in its result said that the icon was missing (review finding D, "icon failure ... read as success"). The result of the publish now carries these lines as warnings (`PackagingResult.Warnings`), and the history of the run records them as the detail of the ConfigMgr step when the step has no error and no failed content step. The status of the run does not change: the application exists and clients can install it.

The unused method `MecmTarget.DeployAsync` (a deployment to a collection that no part of the program called; the test deployment has its own script) is removed (review finding D, "dead code").

| Test class | What it proves |
|---|---|
| `ConfigMgrPublishWarningTests` | An icon that was not applied gives the warning with the reason; an icon that was not found gives the warning with its path; an icon that was set, an empty output and an output without a warning give none; the same warning is reported once; the detail of the step is the error, else the failed content step, else the warnings joined with `; ` (and null when there is none); the dead method no longer exists. |

**Lab check.** None. The change reads lines that the existing script already prints, and removes a method that nothing called.

## Publish of a dropped installer: the title settings apply

A definition run takes its application title from the mode of the stage (a profile), the saved mode of the application, and the global settings **Include version in application name** and **Remove version from application name**. The publish of a dropped installer or a bring-your-own installer (`PackageStagedManifestAsync`) did not read these settings: the ConfigMgr application and the Intune display name always carried the name of the manifest, whatever the settings said (review finding D, "a dropped installer ignores the title settings").

Both paths now call one function (`PackagerExecutionService.RunApplicationTitle`). The staged path passes the title to the ConfigMgr publish, logs it when it differs from the name of the manifest, and gives the Intune publish the same title as its display name, as a definition run does. A dropped installer has no saved mode of its own, so the mode of the manifest and then the global settings decide. The content folders and the detection stay versioned. A dropped installer that was published earlier under the name of its stage is published as a new ConfigMgr application (and, for a plain dropped installer, a new Intune app, because its Intune identity is made from the name) when a title setting changes its name. The earlier ones are not renamed or removed.

The same finding says that the staged-manifest path "skips the requirement list". That is covered by the Intune requirement change (finding B4): the requirement rules of the manifest are now translated for Intune on every path, and the ConfigMgr publish reads the requirement rules of the manifest on every path. A saved per-application condition list belongs to a definition (it is keyed by the definition identifier), so a dropped installer has none.

| Test class | What it proves |
|---|---|
| `RunApplicationTitleTests` | With no setting the title is the name of the manifest; the global remove setting strips the version; the include setting adds it and wins over the remove setting; the mode of the manifest outranks the global setting; the saved mode of an application outranks the global setting but not the manifest. |

**Lab check.** None. The title function and its inputs are unit-tested. The staged path runs the same ConfigMgr script as a definition run, with a title argument that the definition run already passes.

## Profile fields that were stored and ignored: return codes, reboot policy, working directory

The Workbench stored `Install.ReturnCodes`, `Install.RebootPolicy` and `Install.WorkingDirectory` (and the same three for the uninstall step) in the manifest, and no publish read them (review finding D, "stored-but-ignored profile fields"). The install fields now take effect when the stage is finalized and published. The profile is checked at that time, so a mistake in the return code list or the reboot policy stops the stage with a message that names the entry.

| Field | ConfigMgr | Intune | WSUS |
|---|---|---|---|
| Install return codes | The wrapper keeps its codes (the cmdlet has no return code parameter). | Added to the default table; a code of the list replaces the default of the same code. | Success and reboot codes join the exit code lists of the update. |
| Install reboot policy | `-RebootBehavior` | `deviceRestartBehavior` | No change. |
| Install working directory | `-InstallWorkingDirectory` | No such setting. | No change. |
| Uninstall working directory | `-UninstallWorkingDirectory` | No such setting. | No change. |
| Uninstall return codes and reboot policy | Recorded only (Workbench finding `UNINSTALL-FIELDS-NOT-APPLIED`). | Recorded only. | Recorded only. |

| Test class | What it proves |
|---|---|
| `ProfileDeploymentSettingsTests` | The list parser (bare number, types, separators, mistakes named); the Intune table (defaults kept, a matching code replaced, others added); the reboot policy mapping; a working directory with a line break is refused; `Apply` joins the codes to the WSUS lists and sets the behavior; the ConfigMgr script carries both working directories with a quote escaped, and none when unset. |
| `StageFinalizationTests` | A profile with the three install fields changes the manifest; a bad return code list stops the stage. |

**Lab check.** A ConfigMgr publish on CM01 of a test application whose profile sets both working directories and the reboot policy; read the deployment type back with `Get-CMDeploymentType` (`InstallWorkingDirectory`, `UninstallWorkingDirectory`, `PostInstallBehavior`); remove the test application. No sign-out and no restart.

## No .intunewin file unless an Intune publish builds it

The presence of `IntuneWinAppUtil.exe` on a computer decided whether the program built a `.intunewin` bundle. A stage built one next to the staged content, and a ConfigMgr-only run built one and copied it to the content share. The program now builds the bundle only inside an Intune publish. A stage and a ConfigMgr-only run build nothing and copy nothing besides the content.

| Test class | What it proves |
|---|---|
| `NoImplicitIntuneWinTests` | The three automatic build methods do not exist; no service other than the Intune deployment service asks whether the tool is installed; the Intune publish resolves the tool from the tool folder or the PATH before it downloads (a copy on the PATH is used). |

**Lab check.** A ConfigMgr-only run with the tool present leaves no `.intunewin` file in the stage folder and none on the content share.

## A run with no comment keeps the description of an existing application

A ConfigMgr replace already wrote the description only when the run had a comment. The Intune update wrote the name of the application into the description whenever the comment was empty, so a description that an administrator typed in the console was lost on the next publish (review finding A7). An Intune update now keeps the existing description when the run has no comment. A new app, or an existing app with no description, gets the name.

| Test class | What it proves |
|---|---|
| `IntunePublishServiceTests` | An empty or blank comment keeps the description of the existing app; a comment replaces it; an existing app with no description gets the name. |

**Lab check.** None for ConfigMgr (the replace path is unchanged). The Intune result is not verified against a tenant.

## ConfigMgr publish: fewer PowerShell starts

A ConfigMgr publish started a Windows PowerShell 5.1 process for the existing-application probe (only for the Skip and Fail answers), one for the package, one for the content distribution and one for the test deployment. Each start loads the ConfigMgr module, which takes several seconds. The distribution and the test deployment now run in one script (one start, one module load): the distribution body first, then the deployment body. A distribution that fails exits with code 11, and the deployment does not run. A deployment that fails exits with code 12. The reason is written to standard error as one line, and the sentence of the failed step is the same as before.

The probe must run before the content is copied to the share, and the package must run after the copy and its checks, so those two stay separate starts. The generated scripts for the WSUS-only runs and the revision cleanup were checked and need no change: the compatibility scan of the WSUS update reads the staged scripts, and the revision cleanup runs only when an application has more than one revision.

| Test class | What it proves |
|---|---|
| `ConfigMgrContentStepOutcomeTests` | One script holds both steps and loads the module once; the distribution comes first and its failure ends the script; no test collection means no deployment block; each step keeps the body of its own script; the exit code names the step that failed. |

**Lab check.** A ConfigMgr publish on CM01 with auto-distribute and a test collection: the log shows both steps and one module load; a missing DP group fails the distribution and creates no deployment. No sign-out and no restart. Not run in this session: the lab needs credentials that this session does not hold.

## Before a publish: what it does and does not do, and the accept button

The review of the outside publish asked for a preflight statement per target. The Packages page now writes one sentence per target of the run to the log (`Before publish: ...`) when the run starts, for the ConfigMgr, Intune and WSUS buttons, and the Intune review window repeats the sentences above its findings when it opens (it opens only when a finding needs a decision). The sentences say only what the code does: ConfigMgr does not supersede an older application, and deploys (Available) only to the test collection of Settings; Intune does not create or change assignments (an update reaches the devices that the app is already assigned to); WSUS makes no first install, and approves only for the approval group of Settings.

A requirement that has no Intune rule blocks the Intune publish. The finding now carries its accept key, and the Intune review window shows **Accept dropped requirements and continue** when a finding can be accepted. The button adds the keys to `Intune.AcceptedRequirementDrops`, writes a log line for each, and continues. The next review shows the finding as `RequirementDropped` (Review).

| Test class | What it proves |
|---|---|
| `PublishPreflightTests` | Each deployment target gets the sentences of its own targets; the sentences name what the publish does not do. |
| `IntuneRequirementRuleTests` | The blocking finding carries the key that the button stores; the stored key turns it into the accepted finding, which cannot be accepted again. |

**Lab check.** None. The window is WPF and is not exercised by a test; the dialog code adds the keys that the tests show to be accepted by the finding builder. The Intune result is not verified against a tenant.

## Supersedence step 1: the version rule, the Exact guard and the identity marker

The first of four steps of the specification `docs/specs/supersedence.md`. It adds the settings (Options, the settings file and the command line options `--version-rule` and `--supersedence`), the version rule that changes the operator of the version clause at publish, the Exact guard, the blocked combination, and the identity marker that every ConfigMgr publish writes. It writes no supersedence relation; the lookup, the supersedence step, the chain check and the rewrite step are steps 2 to 4.

Resolution: each setting is resolved by itself in the order command line, per-title override, site default, and the blocked combination is checked after both are resolved. A refused row creates nothing. The shipped values are `Minimum` and `Off`, so the published detection of a current user does not change; only the identity marker is new.

Not yet built in this step (recorded for the later steps): the per-title override and the recorded installed values are edited in the settings file, and nothing records an installed value from the test step of Add App yet, so a title that is not an MSI needs an accepted block or `Minimum`; the line name of a title without a definition; the action that accepts a block; the marker for an application that already has keywords. The program does not rewrite a keyword of an administrator, because how the provider stores several keywords through `Set-CMApplication -Keyword` is not confirmed on a lab site. A row that the version rule refuses is recorded as Refused.

| Test class | What it proves |
|---|---|
| `SupersedenceVersionRuleTests` | `Exact` writes `IsEquals` to a File or Registry version clause (the MSI clause too) and nothing else; `Minimum` changes nothing; other operators, other expected values, existence clauses and script clauses stay; a compound detection and the deployment type detections are covered; the definition is not changed. The guard: equal, different (`3.7.7` and `3.7.7.1234`), no value, the MSI ProductVersion, separator normalization without ignoring zero parts, an accepted block per version. Resolution order and the blocked combination after resolution, the shipped defaults, a bad word refused. The plan refuses a blocked row, applies Exact to the target that asked for it, logs an accepted block. The marker: its parts, no marker without an identifier (the run gives the identifier of a grid stage of the default profile), the script writes it after the application exists, reads the keywords first and writes only when the application has none of its own, a failed marker is a warning. With the shipped defaults the script of the copy of the manifest equals the script of the original. An accepted block of the Exact guard blocks Replace and uninstall for the title. A null or differently cased override dictionary does not stop a publish. |
| `BuildArgumentsTests` | The two options parse, are absent by default, and an unknown word is refused with the allowed words. |
| `PowerShellLiteralTests` | The package script with a marker that holds a quote parses without error. |

**Lab check (2026-11-12, CM01, ConfigMgr 5.00.9146; the run of the generated marker script against two test applications, removed after).** Confirmed: New-CMApplication and Set-CMApplication take -Keyword as a string array; the keywords read back as DisplayInfo[].Tags through SccmSerializer; an application with a keyword of an administrator gets no marker and keeps its keyword (the script warns); after the keywords were cleared (Set-CMApplication -Keyword @()) the marker was written and SDMPackageXML contains >marker<; every save of an application makes a new revision with a new CI_ID, so the program finds an application by name. Not confirmed: the marker through a real publish of a catalog title; the compare of 3.7.7 with 3.7.7.0 under IsEquals on the client; lab test 5.

## Supersedence step 2: the lookup, the pairing, the step and the separate outcome

The second of four steps of `docs/specs/supersedence.md`. After a ConfigMgr application is created, and when the supersedence mode is not `Off` and the application carries the identity marker, the program asks the site for the applications with the same marker, chooses the old one, pairs the deployment types and sets one relation for each pair. The marker is matched in the stored application XML (`>marker<`), so one script reads the candidates and their deployment types; the choice is made in plain code.

The step runs after the distribution and the test deployment, and not when one of them failed. Rules that the code applies: the old application is the newest one with the marker whose version is lower than the packaged version and whose title differs from the new title; a retired application is skipped; a version that cannot be read is skipped and named; the others with the marker are named in the log and are not superseded. One deployment type on each side pair directly; with more, the pair is made by the name suffix of staging, and a type without a partner supersedes nothing and gets a warning. An old application without a deployment type supersedes nothing, and the result says so. The relations of a deployment type are read first and a relation that exists is not written again; after a failed write they are read again. The cmdlet gets names and not identifiers, because each save makes a new revision with a new CI_ID. A line that already holds a higher or equal version under another title is named in the log. A step that throws never fails the run; an unreadable CI_ID is reported as not applied. One Click records the separate state too.

A failed step gives the outcome `PublishedSupersedenceNotApplied` (not Partial): the application is complete, the run does not fail, the inventory keeps the version as published, and the detail reads "Published, supersedence not applied: <reason>". A failed content step outranks it.

Not built in this step (recorded for the later steps): the "Apply supersedence" action and the supersedence-only pass of One Click; the replace path that keeps the relations of the deployment types that it removes; the list "Use this as the old application"; the preflight panel text for the blocked combination (the combination is refused before anything is created, and the refusal text is in the log and the result).

| Test class | What it proves |
|---|---|
| `SupersedenceStepTests` | The choice (newest older, same title never, equal or newer never, retired skipped, unreadable version skipped and named, unreadable new version). The pairing (one and one, by suffix, unmatched warnings, no cross-link of architectures, no deployment type). The parser of the site output with a separator in the title. The lookup script (marker matched in the XML with the escapes, the new application always listed, the identifiers are numbers). The step script (names, uninstall option as `-IsUninstall:$true` or `$false`, the relations are read before the write, no text of the provider decides, every pair is tried, exit 1 on failure; a name with a double quote is only ever inside a single-quoted string). The failed step is a separate outcome and not Partial; a failed content step outranks it; the run summary keeps the state. A mode of Off or a missing marker asks nothing of the site. |
| `PowerShellLiteralTests` | The lookup script and the step script parse without error and take a quote in a name. |

**Lab check (open).** On CM01 with two published versions of one test line: `Set-CMApplicationSupersedence -Id <new> -CurrentDeploymentTypeId <dt> -SupersededApplicationId <old> -OldDeploymentTypeId <dt> -IsUninstall $true` creates the relation and a second call reports that it exists (confirm the text that the match `already` relies on); the console shows the relation; the lookup finds the old application through the marker in `SDMPackageXML`; the time of the lookup on the lab site. Lab tests 1 to 3 and 6 of the specification belong to this step. Not run in this session: the lab needs credentials that this session does not hold.

## PowerShell 7.7 stage failure, and four new .NET runtime definitions

**The defect.** The definition `PowerShell 7 (x64)` read the latest release of the GitHub API (`tag_name`) and built `PowerShell-<version>-win-x64.msi` from it. PowerShell 7.7.0, the latest release since 2026-11-10, ships no MSI for Windows: its Windows assets are three zip files (`win-x64`, `win-x86`, `win-arm64`, plus two fxdependent zip files) and one universal `PowerShell-7.7.0.msixbundle`. The address answered 404 and the stage failed after two download attempts (reproduced with `AppPackagerBuild --application catalog:0000089d8d25e2a0 --stage`).

**The fix.** The definition keeps the MSI (its detection by product code, its ARP entry and its uninstall do not change). The version scraper is now `HttpRegex` over the releases list with the pattern `"name":\s*"PowerShell-(\d+\.\d+\.\d+)-win-x64\.msi"` (the name of an asset, so that a release body that lists a file or a `.msix` file cannot give a version) and `SelectHighestVersion`: it takes the highest release that carries the x64 MSI. A release without an MSI (7.7.0 today), a prerelease (`7.7.0-rc.1` does not match) and a back-published patch of an older line that is listed above a newer one cannot give a version. The staged version is 7.6.10. No 7.7 release (preview 1 to 5, the release candidate and 7.7.0) carries an MSI, so this packager follows the 7.6 line, which is the LTS line, until a release of a newer line carries an MSI again; `PowerShell 7 LTS (x64)` still follows 7.4. The msixbundle is a different packaging and is not used by this definition. Whether to add an MSIX packager for 7.7 is a decision for the owner.

**Four new definitions**, written from the existing ones: `.NET 9 Desktop Runtime (x86)`, `.NET 11 Desktop Runtime (x64)`, `ASP.NET 9 Server Hosting Bundle (x64)` and `ASP.NET 11 Server Hosting Bundle (x64)`. `.NET 9 Desktop Runtime (x64)` and `.NET 10 Desktop Runtime (x64)` already existed; there is no x86 definition of .NET 10, by decision. .NET 11.0.0 is a stable release (`releases-index.json` lists `11.0` with `latest-runtime` 11.0.0, released 2026-11-10, support phase active), so the definitions use the stable channel and no preview channel.

| Test class | What it proves |
|---|---|
| `PowerShell77AndDotnetRuntimeDefinitionTests` | With the real asset names of 2026-11-12 the version is 7.6.10 (not 7.7.0, not the release candidate); a synthetic back-published 7.5 patch that is listed first does not win; an msix file, a body that names a file, and a prerelease give no version; a list with only a prerelease, an msixbundle and zip files gives no version; the download address of the chosen version names an asset that exists in the list; the definition still stages an MSI with its detection and ARP. The four new definitions: the application name, the architecture, the installer type and framework, the silent switches, the channel of the version query, the download address, the CPE, the detection path of the x86 and x64 desktop runtime, the ARP entry and the uninstall command of the hosting bundles. The shipped Ids are unique, and there is exactly one definition of each of the six runtime names (and none of .NET 10 x86). |
| `ShippedDefinitionCountsDocTests` | The counts in the documentation follow the 323 files. |

**Real stage (2026-11-12, `AppPackagerBuild --stage` of a self-contained build, run on CM01; the files were removed).** An earlier run of the same command on the dev PC broke the rule that no app of this family runs there; it was repeated on the lab and the dev PC files were deleted. PowerShell 7 (x64) 7.6.10 (ProductVersion 7.6.10.0); .NET 9 Desktop Runtime (x86) 9.0.23; .NET 11 Desktop Runtime (x64) 11.0.0; ASP.NET 9 Server Hosting Bundle 9.0.23 (ProductVersion 9.0.23.26523; .NET 9 reached the end of support on 2026-11-10 and 9.0.23 is its last release); ASP.NET 11 Server Hosting Bundle 11.0.0. All five stages exited 0. The ConfigMgr publish and the Intune publish of these five were not run in this session; the Intune result is not verified against a tenant.

**Lab check (2026-11-12, CM01).** Confirmed with two test applications that carried the marker: the lookup script lists both with their deployment types (the new one by name); Set-CMApplicationSupersedence -Name ... -CurrentDeploymentTypeName ... -SupersededApplicationName ... -OldDeploymentTypeName ... -IsUninstall:$false creates the relation (-IsUninstall is a Boolean); a second run finds it by reading Get-CMDeploymentTypeSupersedence (it returns the old deployment type objects) and writes nothing; the relation survives a rewrite of the old detection. Not confirmed: the uninstall of the old application on the client (lab tests 1 and 2); an Overwrite publish of an application that supersedes another one (lab test 6); the time of the lookup on a site with many applications; the text of a refused relation.

## Supersedence step 3: the chain check

The third of four steps of `docs/specs/supersedence.md`. Before a ConfigMgr publish with the supersedence mode `Replace` or `Replace and uninstall`, the program finds the older application of the line (the same lookup as the step), reads the stored XML of its deployment types, and compares the operators of its version clauses with the new detection. The warnings go to the log and never block: Replace and uninstall with Exact against an older GreaterEquals clause; an Exact clause against Minimum, or the reverse; a new GreaterEquals version clause under Replace and uninstall. The stored XML keeps the detection method as XML text inside an argument, so the reader parses that text as a document of its own.

Not built: the warning that the old deployment type has no uninstall command (it needs the stored form of the uninstall command, which the lab confirms).

| Test class | What it proves |
|---|---|
| `SupersedenceChainCheckTests` | The operator of a Version expression is read from the detection method text inside a deployment type XML; an unreadable document or a non-version clause gives none; the site output is parsed per deployment type; the script asks one application by name and escapes it; each warning (uninstall will fail, Exact against Minimum, Minimum against Exact, the new application keeps GreaterEquals only under Replace and uninstall); an existence clause is no version clause; a compound detection and the deployment type detections are walked. |
| `PowerShellLiteralTests` | The detection script parses with a double quote, a quote and a subexpression in the name. |

**Lab check (2026-11-12, CM01).** Confirmed: the stored XML of a deployment type holds the detection twice, as rule elements (`Expression`, `Operator`, `SettingReference` with `DataType="Version"`) and as XML text inside the argument `MethodBody`; the operator words are `GreaterEquals` and `Equals` (`IsEquals` is stored as `Equals`). The reader takes the elements and reads the text only when there are none, so a clause is not counted twice (a defect that the lab run found; `SupersedenceRealDetectionXmlTests` keeps the XML that the site wrote).

## Supersedence step 4: the rewrite of the version clause of the old applications, and Restore

The last of four steps of `docs/specs/supersedence.md`. The rewrite is optional and runs from the command line: `--rewrite-old-versions plan` reads the site and prints the plan; `apply` and `restore` need `--yes`. The old applications of a line are the applications with the identity marker of the title, the profile and the architecture, except the newest version.

Plan, as the specification says: an old application with a **required deployment** is blocked and its collections are named; a clause is rewritten only when its operator is `GreaterEquals`, its expected value is the version of that old application, the detection is one File or Registry value version clause, and the Exact guard passes for that old application and version (a recorded installed value of that version equals the expected value; the MSI case has no staged MSI of an old version, so only the recorded values count). Every other clause is listed with the reason. The confirmation text says that a device with a newer version then reads "not installed".

Apply writes the clause with `Set-CMScriptDeploymentType -RemoveDetectionClause <logical name> -AddDetectionClause <the same clause with IsEquals>`; the log (`supersedence-rewrite.jsonl` in the data folder) records the application, the deployment type, the old and the new operator and the logical name. The publish script purges the revision history, so the log is the only way back, and it is written ahead: one entry is on disk before each change, a restore entry cancels the entry of a change that failed, and a log that cannot be written stops the apply. Restore reads the log, takes for each deployment type whose last entry is a rewrite the first rewrite entry (the operator before the first rewrite), and writes it back by the same method; it writes a restore entry, so a second Restore does nothing, and it names an entry whose application is not in the line any more. The names in the script output are base64, so a separator in a title cannot turn a rewrite into a failure. The newest version is current under every title that holds it, the detections are kept by application and deployment type, the registry hive is checked in the plan, and a deployment query without its end marker is refused (the plan never reads silence as "no required deployment").

| Test class | What it proves |
|---|---|
| `SupersedenceRewriteTests` | The one-clause detection is read from the XML that the site wrote (File and Registry, the real documents); a document with no enhanced detection, with two clauses or that cannot be read is not rewritable. The plan: a clause that passes is rewritten; a required deployment blocks the application and names the collection; another operator, another expected value, a failing guard and an unreadable detection leave the clause with the reason. The guard needs a recorded value of the version of the old application. The rewrite script (the same clause with the other operator, removed by its logical name, built off the CM drive; the registry clause keeps hive, key, value and view; only the two operators). The deployment script and the required ones. The log round trips and skips a bad line; the script output becomes entries; Restore takes the first operator, only for the line, and not twice; a rewrite after a restore is restored to the original operator. |
| `BuildArgumentsTests` | The option parses with plan, apply and restore, takes `--yes`, and refuses an unknown word, a combination with stage or package, and `--yes` alone. |

**Lab check (2026-11-12, CM01).** Confirmed with a test application that had one File clause: `Set-CMScriptDeploymentType -RemoveDetectionClause <logical name> -AddDetectionClause <clause>` replaced the clause (the stored operator changed from `GreaterEquals` to `Equals`, the logical name changed) and a supersedence relation to the application stayed; the generated rewrite script printed the `Rewritten|...` line; `Get-CMApplicationDeployment` returns `OfferTypeID` 0 for a required deployment and the deployment script printed it; an application with no deployment prints nothing and gives no error. The test applications and the deployment were removed. Not confirmed: the Restore script on the site (it uses the same builder with the other operator), the effect of the rewrite on the client (lab test 4: the uninstall of the old application reports success; an old application whose installed file reports more version parts is not rewritten), and the rewrite of a Registry clause on the site (the stored XML of a registry clause was read from the site and is a test fixture, but the rewrite was not run on it). `Get-CMApplicationDeployment` has `-DisableWildcardHandling` (checked on the site).

**Lab check (open).** On CM01: publish a test application, read the keywords with the console to confirm that the marker is stored in the keywords and that an administrator keyword stays; confirm that `[...SccmSerializer]::DeserializeFromString($app.SDMPackageXML, $true).DisplayInfo.Tags` returns the keywords; publish with `Exact` and an MSI, and read the clause operator back; record how the client compares `3.7.7` with `3.7.7.0` under `IsEquals`. Not run in this session: the lab needs credentials that this session does not hold. No sign-out and no restart.

## Import of vendor definitions from a URL, step 1 (engine)

`docs/specs/vendor-url-import.md`, delivery step 1, in part. The engine is built and tested; the program does not import a feed yet, because the loaders do not read the third folder and there is no window or command. Nothing in the program calls the engine, so a current user sees no change.

What the engine does, as the specification says:

- **Fetch.** HTTPS only (the certificate revocation is checked online; an address whose query has a secret-looking name, such as `token`, `key`, `sig` or `sas`, is refused for an anonymous import; a `http:`, `file:`, `ftp:` address, an address with user information, and a redirect to HTTP are refused before a request); the program follows redirects itself, at most 5, each target checked again; 2 MiB read as a stream and cut off, also by a declared length; content type `application/json` or `text/plain`; UTF-8 text; 429 and 503 are retried up to 3 times with the wait of the answer, inside the 60 seconds of the fetch; the certificate and the host name are checked by Windows (an error is refused with the host named); a generic `User-Agent`, no cookies, no referrer; no message and no record holds the query.
- **Validation.** No duplicate property name (also with another letter case); a property that the format does not know is refused and named (`Schema` and `Acquisition` are allowed; `ImportedFrom` and `ImportedVendor` are refused); `Id`, `Application`, `Vendor`, `CMName` and `Architecture` follow the grammar; every address field is `https:` (a relative hop after the first one is allowed); the hosts are listed; at most 200 definitions and 64 levels. What is stored is the model written again.
- **Code.** A definition that carries code is blocked in every trust state: scripts (a script detection, a post-install script), commands (an uninstall command, an expanded install command, the fragments of installer settings, the extract arguments), a bootstrapper, a path to a local file, the target secret, the Office Deployment Tool configuration (`Odt`: its values go into an XML file without escaping and run as the system account) and the zip payload (`ZipPayload`: it deletes an install folder and a registry key that the document names). `DefinitionCode` classifies every top-level property of the format, and a test fails when the format gets a property that it does not classify.
- **Trust and preview.** With a pinned SHA-256 the state is `Pinned digest`, and a pin that does not match stops the import (no fall-back). Without a pin the state is `Not verified`, the wording is `Definition document NOT verified`, and an import needs its confirmation each time. The conflicts (shipped, user, another feed) are listed; the default is to skip; a shipped `Id` is replaced only when it is confirmed; a user definition is never replaced. Nothing is written by the preview.
- **Malformed documents.** A value of the wrong kind, an explicit null, a list element that is not an object, or a list element that is null gives a finding (a refused document or a blocked definition) and never an exception. A feed that is stored under the FeedId that the administrator confirms is never replaced by an import (an update is a later step). An invalid FeedId in the document is a warning: the administrator chooses another. The source record holds the wording `Definition document NOT verified`, and a listed Id that is not in the grammar is never read as a path.
- **Storage.** `<data root>\ImportedDefinitions\<FeedId>\<Id>.json` and `source.json`, built in a staging folder and swapped in, with the record written last; a failed write keeps the earlier state; `Recover` restores the old copy of a swap that stopped between the two renames; the feeds are read in the order of their import, and the record is never read as a definition; Remove feed deletes the folder.

| Test class | What it proves |
|---|---|
| `VendorImportTests` | Every row above, with a fake HTTP handler (no network) and a temporary folder: the refused addresses and redirects, the limits, the retry and the total time, cancel, the digest and the generic request; the validation of identifiers, names, addresses, duplicates, unknown properties, depth and count; the code block for each kind of code and the classification of the format; the preview, the pin, the confirmation, the conflicts and the replace rules; the storage swap, the failed write, the recovery, the order and Remove. |

**Not yet tested.** The behavior of `SocketsHttpHandler` for headers and certificates on a redirect (the specification asks for a test against the real handler), the signed document (step 2) and the authenticated feeds (steps 5 and 6).

## Supersedence: the recorder of installed values (command line)

`--record-installed-value <value> --version <version>` and `--accept-exact-block --version <version>` write one value of `Supersedence.InstalledValues` or `Supersedence.AcceptedExactBlocks` for the title and the version, and end. They stage, package and send nothing. The key is the one the Exact guard and the rewrite read, so a recorded value takes effect. A value and a version are checked as version texts (a digit first, then letters, digits and `. + _ -`, at most 64 characters), so no free text enters the settings.

| Test class | What it proves |
|---|---|
| `InstalledValueRecorderTests` | The grammar of a value; the key equals the key of the guard; a second value replaces the first; an acceptance is stored once and per version; a bad version, value or title is refused and nothing is stored. |
| `BuildArgumentsTests` | The two options parse, need `--version`, run alone and refuse a value that is no version; the usage names them. |

**Not tested here.** The run of the command against a settings file (policy 2a: no run of the program on the development PC). Lab check: record a value, read the settings file, then `--rewrite-old-versions plan` on the lab site shows the clause as rewritable.

## Supersedence: the "Apply supersedence" action

The button on the Packages page runs `PackagerExecutionService.ApplySupersedenceAsync` for each checked application: it reads the stage manifest of the sealed or staged build, resolves the title as the publish resolves it, plans the supersedence (the same planner as a publish), and runs the step of `MecmTarget`. It records a success for the ConfigMgr destination in the inventory.

| Test | What it proves |
|---|---|
| `LocalFileGridStageTests.ApplySupersedence_RefusesWithoutAStage_ThenWithoutASiteOrTheMode_AndContactsNoSite` | A row without a stage and a row without a site code or with the mode off are refused with their own sentence before any site call. |

**Lab check (not run here).** On CM01: publish version 1 and version 2 of a title with the mode `Replace`, remove the relation by hand, check the row, press **Apply supersedence**: the relation exists again and the inventory row reads Succeeded. Press it again: no relation is added twice.

## Supersedence: the rewrite window (old-version clause, with Restore)

The button **Rewrite old versions** on the Packages page opens the change plan window for the checked application (one at a time). The plan reads the line from the site with `OldVersionRewriteRunner.PlanAsync` (the same plan as `--rewrite-old-versions plan`). The window lists each clause that would change, shows the blocked and left clauses with their reasons in the notes, and asks for the confirmation text of the specification before Confirm. Confirm writes the log ahead and rewrites (the same runner as `--rewrite-old-versions apply`). Undo is Restore (the same runner as `restore`). When nothing is left to rewrite and the log holds a pending rewrite, the window offers that Restore.

| Test class | What it proves |
|---|---|
| `RewriteOldVersionsPlansTests` | No window when no clause can be rewritten; one row per rewritable clause with the destructive confirmation, the notes and Undo; the result rows (written, failed with the site text, not reached); the restore row names the operator that comes back. |

**Lab check (not run here).** On CM01 with two published versions of a title and a recorded installed value of the old version: press the button, read the plan, confirm, check that the old application's clause reads IsEquals on the site and that `supersedence-rewrite.jsonl` holds the entry; press Undo and check that GreaterEquals is back. An old application with a required deployment shows as blocked and is not changed.

## WQL Editor layout

`WqlEditorLayoutTests` shows the Collection and Compliance page with the WQL Editor tab selected, in the light and dark theme at 1210 x 900 and 794 x 720, and measures: the gap from a label to its box (4 px), the gaps between the blocks (multiples of 4 px, at least 8 px), the same top and height of the rule box and the rule name box, the size of the query box (at least 120 px high and 360 px wide), the tops of one button row, the gap between buttons (8 px), the gap from the query box to the first button row (8 px), and no button cut off or outside the page. The test found one defect: the combo box was 37 px high and the text box 36 px; both are 40 px now.

**Not done here.** The guide image `docs/images/tools-collections-wql.png` is not retaken. It needs a lab capture on CLIENT01 (policy 2a: no window on the development PC).

## PowerShell scripts are plain ASCII

`PowerShellScriptsAreAsciiTests` reads the bytes of every `.ps1`, `.psm1` and `.psd1` file of the repository (not `bin`, `obj`, `.git`, `.claude`) and fails on a UTF-8 byte-order mark or any byte above 127, naming the file and the line. Windows PowerShell 5.1 reads a script without a byte-order mark in the ANSI code page, so one dash or curly quote changes the text. The rule does not apply to C# or XAML. The scan found one file (`tools/RemoteTestHarness/RemoteTestHarness.ps1`, 47 box-drawing characters), which is fixed.

The rule covers the data files that a script or a test reads, too (`EveryDataFileThatAScriptOrATestReadsIsPlainAscii`): the definition files, the JSON fixtures that the PowerShell fixture scripts read (plan digest, site hygiene, WSUS rules and adapter), the workflow `release.yml`, `README.md`, `THIRD-PARTY-NOTICES.md`, the installer script, `DesktopRuntime.props`, the project files and the three documents that the drift tests parse. The scan removed the byte-order mark of 31 JSON files and wrote the 15 Unicode bytes of `canonicalization-edge-cases.json` as `\u` escapes (the parsed text is the same, so the digests do not change). **Decision, not changed:** the JSON fixtures of the vulnerability feeds (`Fixtures/Vulnerability`) are read by C# only and keep the text of the vendors; the third-party license files keep the names of their authors; binary fixtures are not text.
