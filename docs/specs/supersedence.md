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
| Old application | The application of an earlier version of the same title that is already on the site. |
| Detection clause | One rule of the detection method of a deployment type. |
| Version clause | A detection clause of type File or Registry value that compares a version. |
| Chain | The old applications of one title on the site, from the oldest to the newest. |

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
| `Exact` | `Equals` (`IsEquals` in the ConfigMgr cmdlet) | This version only. |

Rules:

1. The definition does not change. About 171 shipped definitions use a version compare in their detection, and they keep it. Only the operator that the publish step writes changes.
2. The rule changes a clause only when all of these are true: the clause is a version clause, its operator in the definition is `GreaterEquals`, and its expected value is the packaged version. A clause with another operator is not changed. An existence clause is not changed.
3. MSI product code detection is already exact. The rule does not change it.
4. The same rule applies to the detection that the Intune publish writes (`greaterThanOrEqual` and `equal`). The supersedence mode does not apply to Intune (see "Intune").
5. WSUS detection is not changed.

## Supersedence mode

| Value | Result on ConfigMgr |
|---|---|
| `Off` | No relation to an old application. Shipped default. |
| `Replace` | The new deployment type supersedes the deployment type of the old application. The old application is not uninstalled. |
| `Replace and uninstall` | The same, and ConfigMgr uninstalls the old application. Needs `Exact`. |

How the program does it:

1. The program finds the old application on the site: the newest application of the same title whose version is lower than the packaged version. The lookup is the same one that the program uses for the existing-application check. If there is no old application, the supersedence step does not run, and the result says so.
2. After the new deployment type and its content are complete, the program runs `Add-CMDeploymentTypeSupersedence` for the new deployment type against each deployment type of the old application. It passes the uninstall option for `Replace and uninstall`. The exact parameter names are confirmed on the lab site before the code is written.
3. The program does not delete or retire the old application.
4. If the supersedence step fails, the new application stays. The result is `Partial` and gives the reason. The program does not delete the new application, because it is complete in every other way.
5. A repeat publish of the same version (the replace path) is not supersedence. The step does not run for it.

## Blocked combination

`Replace and uninstall` with `Minimum` is blocked before any work starts. The preflight panel shows the text:

> Replace and uninstall needs the version rule Exact. With Minimum, the detection of the old application still finds the new version after the uninstall, and the uninstall is reported as failed. Set the version rule to Exact, or set the supersedence mode to Replace or Off.

The administrator cannot accept this block. The administrator changes one of the two settings.

## Where the settings live

1. **Site default**: Options, per ConfigMgr site. Shipped values are `Minimum` and `Off`, so nothing changes for a current user.
2. **Per-title override**: on the publish page, for one title. It is stored in the user settings, keyed by the definition identifier. It is not stored in the definition, because definitions are shipped content. A title that updates itself uses this override to keep `Minimum`.
3. **Command line and One Click**: `--version-rule exact|minimum` and `--supersedence off|replace|replace-uninstall`. They override the site default. One Click uses the site default and the per-title override.

The order of precedence is: command line, per-title override, site default.

## Chain check

Before the publish, the program reads the detection of the old application from the site. It gives a warning when the chain mixes rules:

- The mode is `Replace and uninstall`, the version rule is `Exact`, and the old application has a version clause with `GreaterEquals`. The uninstall of that old application will still be reported as failed.
- The old application has an `Exact` clause and the new application has `Minimum` (or the reverse). The chain is inconsistent.

The warning shows the title, the old application, and the clause. It does not block.

## Rewrite step

The program can rewrite the version clause of an old application to `Exact`. It is optional.

1. The program counts the old applications and clauses to change, and shows the count and the names first.
2. The administrator confirms. Nothing is rewritten without the confirmation.
3. The program rewrites only version clauses with `GreaterEquals` whose expected value is the version of that old application.
4. The log records the application, the clause, the old operator, and the new operator.
5. The step does not run for an application that has devices in a deployment unless the administrator confirms that as well; the confirmation text says that ConfigMgr evaluates the detection again on those devices.

The cmdlet that changes a detection clause of an existing deployment type is confirmed on the lab site before the code is written.

## Intune

Intune relations between applications (`mobileAppRelationship`, update and replace) are not in the v1.0 Graph reference as far as this specification could verify. The beta endpoint may have them. This is **unverified**, and the program does not write Intune supersedence until a tenant test exists. The supersedence mode is shown for ConfigMgr only. The Options page says that Intune supersedence is not available. The version rule applies to the Intune detection (rule 4 above).

## WSUS

WSUS keeps its own supersedence and its own "decline superseded" setting. This specification does not change it.

## Interface

1. Options: a "Supersedence" group with the version rule and the supersedence mode, per site.
2. Publish page: one line that shows the effective values and where they come from (site default, per-title override, command line). A link opens the per-title override.
3. Preflight panel (per target): the blocked combination, the chain warnings, and the count for the rewrite step.
4. Result: the result of the publish names the supersedence step, the old application, and the outcome.

## Errors and edge cases

| Case | Behavior |
|---|---|
| No old application on the site | The step does not run. The result says "No older version found". |
| More than one old application of the title | The program supersedes the newest one. The chain warning lists the others. |
| The old application has no deployment type | The step does not run for it. The result says so. |
| The old application is retired | It is skipped. The result says so. |
| The supersedence cmdlet fails | `Partial`, with the reason. The new application stays. |
| `Replace and uninstall` and the old uninstall command is missing | The program warns before the publish. ConfigMgr cannot uninstall without a command. |
| Detection is not a version compare (for example an existence clause) | The version rule has no effect. `Replace and uninstall` is allowed. |

## Tests

Unit tests (run in CI):

1. The operator is `Equals` for `Exact` and `GreaterEquals` for `Minimum`, for File and Registry version clauses, and only for those.
2. Clauses with other operators, existence clauses, and MSI product code detection are not changed.
3. The blocked combination is refused, and the text is the one in this specification.
4. The precedence of command line, per-title override, and site default.
5. The old-application lookup (lower version only, newest first, retired skipped).
6. The script for the supersedence step (parameter text, quoting, uninstall option).
7. A failed supersedence step gives `Partial` and keeps the new application.
8. The chain check gives the expected warnings.
9. The rewrite step changes only the clauses that it must, and writes the log lines.
10. The shipped site default is `Minimum` and `Off`, and a current user's publish output does not change.

Lab tests on the ConfigMgr lab site, with the lab client. Each test checks that the **uninstall of the old application reports success** on the client:

1. `Exact` with `Replace and uninstall`.
2. `Minimum` with `Replace` (no uninstall): the old application stays, and the new one supersedes it.
3. The blocked combination: the program refuses it and creates nothing.
4. The rewrite step on an old application with `GreaterEquals`: the clause is `Exact` afterwards and the uninstall reports success.

The lab rules apply: only the lab site server, the lab client and the lab domain controller, never more than three machines at once, machines are started for the test and stopped afterwards, no snapshot or checkpoint, and the test removes what it installed.

## Delivery

One pull request for this specification. Then the implementation in four pull requests, one at a time. Each has its tests, its test documentation, a version change and a changelog entry.

1. Settings and the version rule (engine, publish, Options, per-title override, command line).
2. The ConfigMgr supersedence step, the blocked combination in the preflight panel, and the lab tests 1 to 3.
3. The chain check.
4. The rewrite step and the lab test 4.

## Not covered

- Intune supersedence (unverified, see above).
- Change of the WSUS supersedence.
- Automatic removal of old applications from the site.
- Automatic rewrite of a detection without confirmation.
