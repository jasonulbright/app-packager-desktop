# Supersedence: version rule and supersedence mode

Status: specification, not yet built. Date: 2026-11-11.

## Summary

A new version of an application can replace an older application on ConfigMgr. For the old application to uninstall cleanly, its detection must stop finding the application once it is removed. A "this version or newer" detection clause (`GreaterEquals`) still finds the new version after the old application is uninstalled. The uninstall then registers as failed.

This specification adds two settings:

- **Version rule**: `Exact` or `Minimum`. It sets the operator of the version clause in the detection that is published.
- **Supersedence mode**: `Off`, `Replace`, or `Replace and uninstall`. It sets how the new application relates to the old application.

`Replace and uninstall` needs `Exact`. The program blocks `Minimum` with `Replace and uninstall` and tells the administrator why.

## Terms

| Term | Meaning |
|---|---|
| New application | The application that this publish creates. |
| Old application | An application of an earlier version of the same title that is already on the site. |
| Base title | The title of the application without its version. The architecture suffix stays in it. |
| Detection clause | One rule of the detection method of a deployment type. |
| Version clause | A detection clause of type File or Registry value that compares a version. |
| Chain | The old applications with the same marker on the site, from the oldest to the newest. |

## Problem in detail

1. The administrator publishes version 1. The detection says "version is 1 or newer".
2. The administrator publishes version 2 with supersedence and "uninstall". ConfigMgr uninstalls version 1 on the devices.
3. Version 2 is on the device. The detection of version 1 still says "found" (2 is newer than 1). The client reports that the uninstall did not work.

If the detection of version 1 says "version is 1" (`Equals`), the detection of version 1 says "not found" after the uninstall, and the client reports success.

Some administrators do not want `Exact`. A self-updating application changes its own version, and an `Exact` clause then says "not installed". So both rules stay available.

## Version rule

| Value | Operator written at publish | Use |
|---|---|---|
| `Minimum` | `GreaterEquals` | This version or newer. Shipped default. |
| `Exact` | `IsEquals` (the word of the ConfigMgr cmdlet) | This version only. |

Rules:

1. The definition does not change. About 240 of the 319 shipped definition files use `GreaterEquals` in a detection clause (`docs/reference/definition-format.md` gives the counts by operator). They keep it. Only the operator that the publish step writes changes.
2. The rule changes a clause only when all of these are true: the clause is a version clause, its operator in the definition is `GreaterEquals`, and its expected value comes from the packaged version. A clause with another operator is not changed. An existence clause is not changed.
3. A definition for an MSI installer has no special case. Its detection is a registry version clause (`Uninstall\{ProductCode}`, `DisplayVersion`, `GreaterEquals`), and rule 2 changes it like any other version clause.
4. A clause that rule 2 skips stays as it is. This is possible when the expected value is a token that is not the packaged version, or when a compound detection has a skipped part. The chain check (see below) warns when a new application keeps a `GreaterEquals` version clause while the mode is `Replace and uninstall`.
5. WSUS detection is not changed.

### Exact guard

`GreaterEquals` hides a small difference between the version in the manifest and the version that the product reports on the device. `IsEquals` does not hide it. Example: the setup file reports `3.7.7`, the installed file reports `3.7.7.1234`, and the staged value is `3.7.7`. With `Exact`, every install would report as failed. The installer file does not show the installed value, and the staged value comes from the same source as the installer analysis, so a comparison with the installer analysis proves nothing.

So the program applies `Exact` to a clause only when it holds a value that was read from the installed product:

1. For a clause on the Windows Installer product (an MSI), the value is the `ProductVersion` of the package.
2. For any other clause, the value is the one that the detection tester read from a machine after a test install of this version: the test step of Add App, or the lab check. The program records it for the title and the version.
3. The program compares the recorded value with the expected value. It compares the text after it replaces the separators `+` and `-` with `.`. It does not ignore zero parts, because it is not known whether the ConfigMgr client treats `3.7.7` and `3.7.7.0` as equal with `IsEquals`. The lab test checks that.
4. If the values are equal, `Exact` is applied.
5. If the values are not equal, or the program holds no such value, `Exact` is blocked for that clause. The preflight panel shows the two values, or says that no installed value exists. The administrator can accept the block for that title. The clause is then written as `Minimum`, the title is marked "Exact not applied", and `Replace and uninstall` is blocked for that title.
6. An accepted block is stored per title and per version. A new version needs its own value or its own acceptance, because the installed value of a new version can differ.
7. One Click publishes unattended and runs no test install. For a row that reaches the guard block, One Click reports the row as blocked with the guard text, and creates nothing. It does not write `Minimum` by itself and it does not reuse an acceptance of an earlier version. An administrator who wants the row to run opens the title, runs the test install or accepts the block for that version, and starts One Click again.
## Supersedence mode

| Value | Result on ConfigMgr |
|---|---|
| `Off` | No relation to an old application. Shipped default. |
| `Replace` | The new deployment type supersedes the deployment type of the old application. The old application is not uninstalled. |
| `Replace and uninstall` | The same, and ConfigMgr uninstalls the old application. Needs `Exact`. |

### Where supersedence applies

Supersedence needs an old application that is a separate application. It does not apply when:

1. The resolved title of the new application is the same as the title of the application that is already on the site (for example the title mode is `NoVersion`, or the definition has no version in its title). There is one application, and Overwrite replaces its deployment types. The application cannot supersede itself. The program decides this from the resolved title and not from the title mode, and the lookup never returns an application with the same title as the new one. The preflight panel says so when the mode is not `Off`.
2. The publish is a repeat of the same version (the replace path).
3. The deployment type is MSIX (the program does not publish a native MSIX deployment type).

### Lookup of the old application

The lookup is new. The existing-application check matches one exact title and cannot find an older version under a different title.

A title with the version removed does not identify a product line. Vendors use one product name for several lines and put the line only in the version (for example two major lines of one runtime). A lookup by title could take the application of another line as the old application, and ConfigMgr would then uninstall it. So the lookup uses an identity record and not the title.

**Identity record.** When the program publishes an application to ConfigMgr, it writes a marker that names the identity of the title. The marker has three parts:

- The title identity: the definition identifier for a catalog title. For a title without a definition, a generated identifier that the program stores in the per-title override and that the administrator links once (see below). The base title is never used as an identity.
- The profile identifier. Each profile of a definition is its own application line. The default profile has its own identifier.
- The architecture.

The marker goes into a field of the application that the program owns and that the publish does not otherwise use (the keywords of the application are preferred). The code step confirms the field on the lab site. The replace path keeps the marker.

1. The program reads the identity of the new application: the three parts above.
2. It lists the applications on the site that carry the same marker, and whose title is not the title of the new application. Only an application with all three parts equal is a candidate, so Pilot and Production profiles of one definition do not supersede each other, and an x64 application is matched with x64 applications only.
3. It keeps the applications whose `SoftwareVersion` is lower than the packaged version. The compare uses the version compare of the program. If a version cannot be parsed, the program skips that application and says so.
4. It skips retired applications.
5. It takes the newest of the rest. If there is none, the supersedence step does not run, and the result says "No older version found".

An application without the marker (for example one that an earlier release published, or one that an administrator made by hand) is never chosen by the lookup. The chain warning lists applications whose title has the same form but which have no marker, so the administrator sees why no old application was found. The administrator can set the old application for one title once, from the list on the publish page ("Use this as the old application"). The program shows the title and version first and asks for confirmation. It then writes the marker on that application.

For a title without a definition, the lookup never chooses an application by itself. The first publish has no marker, so it offers the candidates, and the administrator links one with "Use this as the old application" or chooses none. The generated identifier is stored in the entry of the line name and is written into the marker at each later publish, so later versions of that line are found without a new question. A new version is published under the same line name on purpose. The program never copies a line name to another title by itself, even when the base title is the same.

The per-title override is in the user settings. A second administrator, or another PC, has no identifier for the line, and the first publish from there gets a new one. The chain is then broken in a safe way: the result says "No older version found", and nothing is superseded. The administrator links the old application again with "Use this as the old application".
6. The program pairs the deployment types of the new application with those of the old application:
   - When the new and the old application each have one deployment type, the program pairs them directly. The marker already limits the match to one architecture.
   - When an application has more than one deployment type, the program pairs by the suffix of the deployment type name that staging writes (`<AppName> - <NameSuffix>`). The code step confirms on the lab site where the architecture is stored, and that the keywords field takes the marker.
   - A deployment type with no pair gets a warning, and no relation is made for it.
   - The program never links x86 to x64 or to arm64.

### The step

1. After the new deployment type and its content are complete, the program runs `Add-CMDeploymentTypeSupersedence` for each pair. It passes the uninstall option for `Replace and uninstall`. The exact parameter names are confirmed on the lab site before the code is written.
2. The step is safe to run again. It reads the existing relations first and adds only the ones that are missing.
3. The program does not delete or retire the old application.

### Failure and retry

A failed supersedence step does not use `Partial`. `Partial` means that the object is there and clients cannot install it. Here the application is complete.

1. The outcome is a separate state, "Published, supersedence not applied", with the reason. The new application stays.
2. One Click shows the row as published with a warning. It keeps the version pending in its inventory for a supersedence-only pass: on the next run, the program does not publish the version again. It runs the lookup and the supersedence step for that application, and nothing else. The step reads the existing relations first, so a second run adds only the missing ones.
3. The publish page has an "Apply supersedence" action for one application. It runs the lookup and the step without a new publish.

## Blocked combination

`Replace and uninstall` with `Minimum` is blocked before any work starts. The preflight panel shows the text:

> Replace and uninstall needs the version rule Exact. With Minimum, the detection of the old application still finds the new version after the uninstall, and the uninstall is reported as failed. Set the version rule to Exact, or set the supersedence mode to Replace or Off.

The administrator cannot accept this block. The administrator changes one of the two settings. The block is checked after the settings are resolved (see the next section).

## Where the settings live

1. **Site default**: Options. The supersedence mode is per ConfigMgr site. The version rule is per target: one value for the ConfigMgr site and one for Intune. Shipped values are `Minimum` and `Off`, so nothing changes for a current user.
2. **Per-title override**: on the publish page. It can set the version rule and the supersedence mode for one title. It is stored in the user settings, not in the definition, because definitions are shipped content. A title that updates itself uses it to keep `Minimum`.
   - The key is the definition identifier for a catalog title. For a title that has no definition (bring your own installer, Add from installer, a dropped installer, a ConfigMgr import), the key is a **line name** that the administrator types on the publish page. The line name is free text, for example "Vendor Runtime 9". Two product lines that share a base title get two line names. A title without a line name has no per-title override and uses the site default, and it gets no supersedence. The base title is never a key.
   - One override covers all architectures and profiles of the title.
3. **Command line and One Click**: `--version-rule exact|minimum` and `--supersedence off|replace|replace-uninstall`. They override the other levels. One Click uses the site default and the per-title override.

Each setting is resolved by itself, in this order: command line, per-title override, site default. The block is checked after both settings are resolved. A row that is blocked after resolution is reported by One Click as blocked, with the text above, and nothing is created for it. Example: the site default is `Replace and uninstall`, and the per-title override of a self-updating title is `Minimum`. That title is blocked in the preflight panel until the administrator also sets its mode to `Replace` or `Off` in the same override.

## Chain check

Before the publish, the program reads the detection of the old application from the site. It gives a warning when:

- The mode is `Replace and uninstall`, the version rule is `Exact`, and the old application has a version clause with `GreaterEquals`. The uninstall of that old application will still be reported as failed.
- The old application has an `Exact` clause and the new application has `Minimum` (or the reverse). The chain is inconsistent.
- The new application keeps a `GreaterEquals` version clause while the mode is `Replace and uninstall` (rule 4 of the version rule).

The warning shows the title, the application, and the clause. It does not block.

## Rewrite step

The program can rewrite the version clause of an old application to `Exact`. It is optional.

1. The program counts the old applications and clauses to change, and shows the count and the names first.
2. The step is blocked for an old application that has a required (mandatory) deployment. A device that has a newer version, because it updated itself or got it in another way, would read "not installed" and ConfigMgr would install the old version again. The panel lists those deployments. The administrator removes the required deployment, or leaves that application out.
3. For an application with only available deployments or none, the administrator confirms. The confirmation text says that ConfigMgr evaluates the detection again on the devices, and that a device with a newer version reads "not installed".
4. The program rewrites only version clauses with `GreaterEquals` whose expected value is the version of that old application.
5. The log records the application, the clause, the old operator, and the new operator. A "Restore" action reads the log and writes the old operator again. The publish script purges the revision history, so the log is the only way back.

The cmdlet that changes a detection clause of an existing deployment type is confirmed on the lab site before the code is written.

## Intune

Intune relations between applications (`mobileAppRelationship`, update and replace) are not in the v1.0 Graph reference as far as this specification could verify. The beta endpoint may have them. This is **unverified**, and the program does not write Intune supersedence until a tenant test exists. The supersedence mode is shown for ConfigMgr only. The Options page says that Intune supersedence is not available.

The Intune version rule has its own site default (shipped: `Minimum`). It changes the operator of the Intune detection rule (`greaterThanOrEqual` or `equal`). The Exact guard applies to it in the same way.

## WSUS

WSUS keeps its own supersedence and its own "decline superseded" setting. This specification does not change it.

## Interface

1. Options: a "Supersedence" group with the version rule (ConfigMgr and Intune) and the supersedence mode, per site.
2. Publish page: one line that shows the effective values and where they come from (site default, per-title override, command line). A link opens the per-title override. The "Apply supersedence" action is on this page.
3. Preflight panel (per target): the blocked combination, the Exact guard blocks, the chain warnings, and the count for the rewrite step.
4. Result: the result of the publish names the supersedence step, the old application, and the outcome.

## Errors and edge cases

| Case | Behavior |
|---|---|
| No old application on the site | The step does not run. The result says "No older version found". |
| More than one old application with the same marker | The program supersedes the newest one. The chain warning lists the others. |
| The old application has no deployment type | The step does not run for it. The result says so. |
| The newest older application is retired | It is skipped. The program takes the next lower one. |
| A version cannot be parsed | That application is skipped. The result says so. |
| The supersedence cmdlet fails | "Published, supersedence not applied", with the reason. The new application stays. |
| `Replace and uninstall` and the old deployment type has no uninstall command | The program warns before the publish. ConfigMgr cannot uninstall without a command. |
| Detection is not a version compare (for example an existence clause) | The version rule has no effect. `Replace and uninstall` is allowed. |
| Title mode `NoVersion` with Overwrite | Supersedence does not apply. The preflight panel says so. |

## Tests

Unit tests (run in CI):

1. The emitted detection text has `IsEquals` for `Exact` and `GreaterEquals` for `Minimum`, for File and Registry version clauses, and only for those, including the registry clauses of MSI definitions.
2. Clauses with other operators and existence clauses are not changed. A clause with a token expected value that is not the packaged version is not changed, and the chain check warns.
3. The Exact guard: equal values, different values (including `3.7.7` and `3.7.7.1234`), no installed value (blocked), the MSI `ProductVersion` case, and the accepted block that writes `Minimum`. An acceptance is stored per title and per version, and it does not carry to the next version. A One Click row at the block is reported as blocked and creates nothing.
4. The blocked combination is refused, and the text is the one in this specification. It is checked after resolution, for the command line, the per-title override, and the site default, for each setting by itself.
5. The key of the per-title override for a catalog title and for a title without a definition.
6. The old-application lookup (identity marker required, architecture kept in the match, lower version only, newest first, retired skipped, unparseable version skipped, an application with the same title as the new one is never returned, a title without a version gives no supersedence). Two definitions whose titles reduce to the same form are not linked. Two profiles of one definition are not linked. Two titles without a definition that have the same base title but different line names are not linked. A title without a line name gets no supersedence. A line name is not copied to another title. An application without a marker is never chosen, and the chain warning lists it. "Use this as the old application" writes the marker only after confirmation. The publish writes the marker, and the replace path keeps it.
7. The pairing of deployment types: one-to-one for single-type applications, by name suffix for multi-type applications, and the warning for one with no pair.
8. The script for the supersedence step (parameter text, quoting, uninstall option, existing relations are not added again).
9. A failed supersedence step gives the separate state and keeps the new application. A second run (publish with Skip or Overwrite, and a One Click run) runs only that step. A One Click row in this state is not skipped as published. The "Apply supersedence" action runs it without a publish.
10. The chain check gives the expected warnings.
11. The rewrite step: changes only the clauses that it must, is blocked with a required deployment, writes the log, and "Restore" reads the log.
12. The shipped site default is `Minimum` and `Off`, and a current user's publish output does not change.

Lab tests on the ConfigMgr lab site, with the lab client. Each test checks that the **uninstall of the old application reports success** on the client. The tests use WinRM and the session that is logged on, as it is. They do not sign out, lock, disconnect, restart, or power off any lab machine.

1. `Exact` with `Replace and uninstall`.
2. `Minimum` with `Replace` (no uninstall): the old application stays, and the new one supersedes it.
3. The blocked combination: the program refuses it and creates nothing.
4. The rewrite step on an old application with `GreaterEquals` and only available deployments: the clause is `Exact` afterwards, and the uninstall reports success. A second old application with a required deployment is refused. "Restore" writes the old operator again.
5. The Exact guard: a product whose installed file version has more parts than the packaged version is blocked. A product with an equal installed value gets `Exact`, and the detection tester on the lab client reads the same value. A check of `3.7.7` against `3.7.7.0` with `IsEquals` on the lab client records how the client compares them.

The lab rules apply: only the lab site server, the lab client and the lab domain controller, never more than three machines at once, no snapshot or checkpoint, and the test removes what it installed.

## Delivery

One pull request for this specification. Then the implementation in four pull requests, one at a time. Each has its tests, its test documentation, a version change and a changelog entry.

1. Settings, the version rule, the Exact guard, and the identity marker that every ConfigMgr publish writes (engine, publish, Options, per-title override, command line). The marker comes first so that applications published from now on can be found by the lookup.
2. The old-application lookup, the ConfigMgr supersedence step with its separate outcome and the "Apply supersedence" action, the blocked combination in the preflight panel, and the lab tests 1 to 3.
3. The chain check.
4. The rewrite step with "Restore", and the lab tests 4 and 5.

## Not covered

- Intune supersedence (unverified, see above).
- Change of the WSUS supersedence.
- Automatic removal of old applications from the site.
- Automatic rewrite of a detection without confirmation.
