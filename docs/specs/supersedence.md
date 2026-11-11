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
| Base title | The title of the application without its version and without its architecture suffix. |
| Detection clause | One rule of the detection method of a deployment type. |
| Version clause | A detection clause of type File or Registry value that compares a version. |
| Chain | The old applications of one base title on the site, from the oldest to the newest. |

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

1. The definition does not change. The shipped definitions use `GreaterEquals` in 221 detection clauses (the count in `docs/reference/definition-format.md`, in 240 of 319 definition files). They keep it. Only the operator that the publish step writes changes.
2. The rule changes a clause only when all of these are true: the clause is a version clause, its operator in the definition is `GreaterEquals`, and its expected value comes from the packaged version. A clause with another operator is not changed. An existence clause is not changed.
3. A definition for an MSI installer has no special case. Its detection is a registry version clause (`Uninstall\{ProductCode}`, `DisplayVersion`, `GreaterEquals`), and rule 2 changes it like any other version clause.
4. A clause that rule 2 skips stays as it is. This is possible when the expected value is a token that is not the packaged version, or when a compound detection has a skipped part. The chain check (see below) warns when a new application keeps a `GreaterEquals` version clause while the mode is `Replace and uninstall`.
5. WSUS detection is not changed.

### Exact guard

`GreaterEquals` hides a small difference between the version in the manifest and the version that the product reports on the device. `IsEquals` does not hide it. Example: the staged value is `3.7.7` and the file reports `3.7.7.1234`. With `Exact`, every install would report as failed.

So the program applies `Exact` to a clause only after it checks the expected value:

1. The program compares the expected value with the version that the installer analysis read from the installer or from the main file (the same source that the definition names). The comparison uses the version compare of the program, with trailing zero parts ignored.
2. If the values are equal, `Exact` is applied.
3. If the values are not equal, or the program cannot read the version, the preflight panel shows a block for that clause with the two values. The administrator can accept the block for that title; the clause is then written as `Minimum` and the title is marked "Exact not applied". `Replace and uninstall` is blocked for that title.
4. The lab tests check that the detection tester reads the same value on the lab client.

## Supersedence mode

| Value | Result on ConfigMgr |
|---|---|
| `Off` | No relation to an old application. Shipped default. |
| `Replace` | The new deployment type supersedes the deployment type of the old application. The old application is not uninstalled. |
| `Replace and uninstall` | The same, and ConfigMgr uninstalls the old application. Needs `Exact`. |

### Where supersedence applies

Supersedence needs an old application that is a separate application. It does not apply when:

1. The title mode is `NoVersion` with Overwrite. There is one application, and Overwrite replaces its deployment types. The application cannot supersede itself. The preflight panel says so when the mode is not `Off`.
2. The publish is a repeat of the same version (the replace path).
3. The deployment type is MSIX (the program does not publish a native MSIX deployment type).

### Lookup of the old application

The lookup is new. The existing-application check matches one exact title and cannot find an older version under a different title.

1. The program builds the base title with the same naming rule that staging uses for the application title (title mode, architecture suffix, profile).
2. It lists the applications on the site whose base title is the same.
3. It keeps the applications whose `SoftwareVersion` is lower than the packaged version. The compare uses the version compare of the program. If a version cannot be parsed, the program skips that application and says so.
4. It skips retired applications.
5. It takes the newest of the rest. If there is none, the supersedence step does not run, and the result says "No older version found".
6. The program pairs each deployment type of the new application with the deployment type of the old application of the same architecture. A deployment type with no pair gets a warning, and no relation is made for it. The program never links x86 to x64 or to arm64.

### The step

1. After the new deployment type and its content are complete, the program runs `Add-CMDeploymentTypeSupersedence` for each pair. It passes the uninstall option for `Replace and uninstall`. The exact parameter names are confirmed on the lab site before the code is written.
2. The step is safe to run again. It reads the existing relations first and adds only the ones that are missing.
3. The program does not delete or retire the old application.

### Failure and retry

A failed supersedence step does not use `Partial`. `Partial` means that the object is there and clients cannot install it. Here the application is complete.

1. The outcome is a separate state, "Published, supersedence not applied", with the reason. The new application stays.
2. One Click shows the row as published with a warning. On the next run, with Skip or Overwrite, the program finds the application, sees that the relation is missing, and runs the supersedence step again. The step is the only work that it repeats.
3. The publish page has an "Apply supersedence" action for one application. It runs the lookup and the step without a new publish.

## Blocked combination

`Replace and uninstall` with `Minimum` is blocked before any work starts. The preflight panel shows the text:

> Replace and uninstall needs the version rule Exact. With Minimum, the detection of the old application still finds the new version after the uninstall, and the uninstall is reported as failed. Set the version rule to Exact, or set the supersedence mode to Replace or Off.

The administrator cannot accept this block. The administrator changes one of the two settings. The block is checked after the settings are resolved (see the next section).

## Where the settings live

1. **Site default**: Options. The supersedence mode is per ConfigMgr site. The version rule is per target: one value for the ConfigMgr site and one for Intune. Shipped values are `Minimum` and `Off`, so nothing changes for a current user.
2. **Per-title override**: on the publish page. It can set the version rule and the supersedence mode for one title. It is stored in the user settings, not in the definition, because definitions are shipped content. A title that updates itself uses it to keep `Minimum`.
   - The key is the definition identifier for a catalog title. For a title that has no definition (bring your own installer, Add from installer, a dropped installer, a ConfigMgr import), the key is the base title.
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
| More than one old application of the base title | The program supersedes the newest one. The chain warning lists the others. |
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
3. The Exact guard: equal values (with trailing zeros), different values, and a version that cannot be read.
4. The blocked combination is refused, and the text is the one in this specification. It is checked after resolution, for the command line, the per-title override, and the site default, for each setting by itself.
5. The key of the per-title override for a catalog title and for a title without a definition.
6. The old-application lookup (base title, lower version only, newest first, retired skipped, unparseable version skipped, `NoVersion` with Overwrite gives no lookup).
7. The pairing of deployment types by architecture, and the warning for one with no pair.
8. The script for the supersedence step (parameter text, quoting, uninstall option, existing relations are not added again).
9. A failed supersedence step gives the separate state and keeps the new application. A second run runs only that step. The "Apply supersedence" action runs it without a publish.
10. The chain check gives the expected warnings.
11. The rewrite step: changes only the clauses that it must, is blocked with a required deployment, writes the log, and "Restore" reads the log.
12. The shipped site default is `Minimum` and `Off`, and a current user's publish output does not change.

Lab tests on the ConfigMgr lab site, with the lab client. Each test checks that the **uninstall of the old application reports success** on the client. The tests use WinRM and the session that is logged on, as it is. They do not sign out, lock, disconnect, restart, or power off any lab machine.

1. `Exact` with `Replace and uninstall`.
2. `Minimum` with `Replace` (no uninstall): the old application stays, and the new one supersedes it.
3. The blocked combination: the program refuses it and creates nothing.
4. The rewrite step on an old application with `GreaterEquals` and only available deployments: the clause is `Exact` afterwards, and the uninstall reports success. A second old application with a required deployment is refused. "Restore" writes the old operator again.
5. The Exact guard: a product whose file version has more parts than the packaged version is blocked.

The lab rules apply: only the lab site server, the lab client and the lab domain controller, never more than three machines at once, no snapshot or checkpoint, and the test removes what it installed.

## Delivery

One pull request for this specification. Then the implementation in four pull requests, one at a time. Each has its tests, its test documentation, a version change and a changelog entry.

1. Settings, the version rule, and the Exact guard (engine, publish, Options, per-title override, command line).
2. The old-application lookup, the ConfigMgr supersedence step with its separate outcome and the "Apply supersedence" action, the blocked combination in the preflight panel, and the lab tests 1 to 3.
3. The chain check.
4. The rewrite step with "Restore", and the lab tests 4 and 5.

## Not covered

- Intune supersedence (unverified, see above).
- Change of the WSUS supersedence.
- Automatic removal of old applications from the site.
- Automatic rewrite of a detection without confirmation.
