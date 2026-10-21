using System.Text.Json.Nodes;
using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests.Deployments.Rings;

/// <summary>The ring service with the real Deployments script through the stub site.</summary>
public sealed class RingServiceScriptTests
{
    private static DeployableObject App => DeploymentHarness.App;

    [Fact]
    public async Task CreateAllMakesOneDeploymentPerRingRecordsTheRunAndAuditsEachRing()
    {
        using var h = new RingHarness();
        var outcome = await h.CreateRunAsync(RingHarness.ThreeRings(), App);
        Assert.True(outcome.Succeeded, outcome.Message);
        Assert.Equal(3, outcome.RingOutcomes.Count(o => o.Succeeded));
        Assert.Equal(3, h.CreateCalls);
        var run = h.Run(outcome.Run!.RunId);
        Assert.Equal([RingStatuses.Created, RingStatuses.Created, RingStatuses.Created], run.Rings.Select(r => r.Status));
        var stored = h.Site.List("AppAssignments").OfType<JsonObject>().ToList();
        Assert.Equal(run.Rings.Select(r => r.DeploymentId).Order(), stored.Select(s => (string?)s["AssignmentUniqueID"]).Order());
        Assert.Equal(["2026-10-21T09:00:00", "2026-10-23T09:00:00", "2026-10-25T09:00:00"], stored.OrderBy(s => (string?)s["TargetCollectionID"]).Select(s => (string?)s["StartTime"]));
        var creates = h.Records.Where(r => r.Event == AuditEvents.Create).ToList();
        Assert.Equal([1, 2, 3], creates.Select(r => r.RingIndex!.Value));
        Assert.All(creates, r => Assert.Equal(("Lab-Rings", run.RunId, true), (r.PlanName, r.RunId, r.Verified == true)));
        Assert.Equal(["QA", "Pilot", "Prod"], creates.Select(r => r.RingName));
        Assert.NotNull(outcome.UndoChange);
        Assert.Equal(3, h.Progress.Count(p => !p.Final));
        Assert.False(run.HoldLaterRings);
        Assert.Contains("\"RingIndex\":1,", File.ReadAllText(h.Audit.Path));
    }

    [Fact]
    public async Task CreateAllStopsAtAFailedSecondRingAndUndoRunRemovesTheFirst()
    {
        using var h = new RingHarness();
        var plan = RingHarness.ThreeRings();
        var prepared = await h.PrepareAsync(RingHarness.CreateRun(plan, App, await h.ExpandAsync(plan, App)));
        Assert.False(prepared.Blocked, string.Join(" | ", prepared.Checks.Where(c => c.Result != "Pass").Select(c => c.Message)));
        var collections = h.Site.List("Collections");
        collections.Remove(collections.OfType<JsonObject>().Single(c => (string?)c["CollectionID"] == RingHarness.C2));
        var outcome = await h.ApplyAsync(prepared);
        Assert.False(outcome.Succeeded);
        Assert.Equal(1, h.CreateCalls);
        Assert.True(outcome.RingOutcomes[0].Succeeded);
        Assert.StartsWith(DeploymentService.SkippedPrefix, outcome.RingOutcomes[1].Message, StringComparison.Ordinal);
        Assert.Contains("does not exist any more", outcome.RingOutcomes[1].Message);
        Assert.StartsWith(DeploymentService.SkippedPrefix, outcome.RingOutcomes[2].Message, StringComparison.Ordinal);
        Assert.Contains("Ring 2 'Pilot'", outcome.Message);
        var run = h.Run(outcome.Run!.RunId);
        Assert.Equal([RingStatuses.Created, RingStatuses.Held, RingStatuses.Held], run.Rings.Select(r => r.Status));
        Assert.Single(h.Records, r => r.Event == AuditEvents.Create);

        var undo = await h.PrepareAsync(outcome.UndoChange!);
        Assert.False(undo.Blocked, string.Join(" | ", undo.Checks.Select(c => c.Message)));
        Assert.Single(undo.RingChanges);
        Assert.Equal([2, 3], undo.CancelledRings);
        var undone = await h.ApplyAsync(undo);
        Assert.True(undone.Succeeded, undone.Message);
        Assert.Empty(h.Site.List("AppAssignments"));
        var closed = h.Run(run.RunId);
        Assert.True(closed.IsClosed);
        Assert.Equal([RingStatuses.Removed, RingStatuses.Cancelled, RingStatuses.Cancelled], closed.Rings.Select(r => r.Status));
        var undoRecord = Assert.Single(h.Records, r => r.Event == AuditEvents.Undo);
        Assert.Equal((run.RunId, 1), (undoRecord.RunId, undoRecord.RingIndex));
    }

    [Fact]
    public async Task HoldCreatesRingOneOnlyAndPromoteCreatesTheNextHeldRing()
    {
        using var h = new RingHarness();
        var outcome = await h.CreateRunAsync(RingHarness.ThreeRings(hold: true) with { Rings = [.. RingHarness.ThreeRings().Rings.Select(r => r with { SuccessThresholdPercent = null })] }, DeploymentHarness.Package);
        Assert.True(outcome.Succeeded, outcome.Message);
        Assert.Equal(1, h.CreateCalls);
        var run = h.Run(outcome.Run!.RunId);
        Assert.True(run.HoldLaterRings);
        Assert.Equal([RingStatuses.Created, RingStatuses.Held, RingStatuses.Held], run.Rings.Select(r => r.Status));

        Assert.Contains("Ring 3 is not the next held ring", (await h.PrepareAsync(RingHarness.Request(RingChangeKind.Promote, run.RunId, 3))).Checks.Single(c => c.Result == "Fail").Message);
        var promote = await h.PrepareAsync(RingHarness.Request(RingChangeKind.Promote, run.RunId, 2));
        Assert.False(promote.Blocked, string.Join(" | ", promote.Checks.Where(c => c.Result != "Pass").Select(c => c.Message)));
        Assert.Equal(0, promote.ShiftMinutes);
        var promoted = await h.ApplyAsync(promote);
        Assert.True(promoted.Succeeded, promoted.Message);
        Assert.Equal([RingStatuses.Created, RingStatuses.Created, RingStatuses.Held], h.Run(run.RunId).Rings.Select(r => r.Status));
        Assert.Equal(2, h.Site.List("Adverts").Count);
    }

    [Fact]
    public async Task PromoteIsRefusedWhileThresholdCountsAreMissingOrLowAndOpensAtTheThreshold()
    {
        using var h = new RingHarness();
        var outcome = await h.CreateRunAsync(RingHarness.ThreeRings(hold: true), App);
        var run = h.Run(outcome.Run!.RunId);
        var refused = await h.PrepareAsync(RingHarness.Request(RingChangeKind.Promote, run.RunId, 2));
        Assert.Contains(refused.Checks, c => c.Result == "Fail" && c.Message.Contains("has not summarized this ring yet; the 90% threshold", StringComparison.Ordinal));

        h.Summarize(run.Rings[0].DeploymentId!, 10, 8);
        var counts = await h.Service.LiveCountsAsync(run, CancellationToken.None);
        Assert.Equal((RingLiveStates.Summarized, 10, 8, true), (counts[0].State, counts[0].Targeted, counts[0].Success, counts[0].CountsKnown));
        Assert.Contains((await h.PrepareAsync(RingHarness.Request(RingChangeKind.Promote, run.RunId, 2))).Checks, c => c.Message.Contains("Success 80% is below the 90% threshold", StringComparison.Ordinal));

        h.Site.List("Summaries").Clear();
        h.Summarize(run.Rings[0].DeploymentId!, 10, 9);
        var open = await h.PrepareAsync(RingHarness.Request(RingChangeKind.Promote, run.RunId, 2));
        Assert.False(open.Blocked, string.Join(" | ", open.Checks.Where(c => c.Result != "Pass").Select(c => c.Message)));

        h.Site.List("Summaries").Clear();
        h.Summarize(run.Rings[0].DeploymentId!, 10, 5);
        var late = await h.ApplyAsync(open);
        Assert.StartsWith(DeploymentService.SkippedPrefix, late.Message, StringComparison.Ordinal);
        Assert.Contains("below the 90% threshold", late.Message);
        Assert.Equal(1, h.CreateCalls);
    }

    [Fact]
    public async Task PromoteIsRefusedForAClosedRunAChangedFileAHeldLockAndAChangedObject()
    {
        using var h = new RingHarness();
        var plan = RingHarness.ThreeRings(hold: true) with { Rings = [.. RingHarness.ThreeRings().Rings.Select(r => r with { SuccessThresholdPercent = null })] };
        var run = h.Run((await h.CreateRunAsync(plan, App)).Run!.RunId);

        var prepared = await h.PrepareAsync(RingHarness.Request(RingChangeKind.Promote, run.RunId, 2));
        var path = Path.Combine(h.RunsFolder, run.FileName);
        File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddSeconds(5));
        var changed = await h.ApplyAsync(prepared);
        Assert.StartsWith(DeploymentService.SkippedPrefix, changed.Message, StringComparison.Ordinal);
        Assert.Contains("run file changed since the plan was shown", changed.Message);

        prepared = await h.PrepareAsync(RingHarness.Request(RingChangeKind.Promote, run.RunId, 2));
        using (h.Runs.Lock(run.FileName))
        {
            var locked = await h.ApplyAsync(prepared);
            Assert.Contains(run.FileName + ".lock", locked.Message);
            Assert.StartsWith(DeploymentService.SkippedPrefix, locked.Message, StringComparison.Ordinal);
        }

        var app = h.Site.List("Applications").OfType<JsonObject>().Single(a => (int?)a["CI_ID"] == 16779607);
        h.Site.List("Revisions").Add(app.DeepClone());
        app["CI_ID"] = 16779700;
        app["CIVersion"] = 14;
        app["DateLastModified"] = "2026-10-20T11:00:00";
        var newRevision = await h.PrepareAsync(RingHarness.Request(RingChangeKind.Promote, run.RunId, 2));
        Assert.Contains(newRevision.Checks, c => c.Result == "Fail" && c.Message.Contains("16779700", StringComparison.Ordinal) && c.Message.Contains("start a new run", StringComparison.Ordinal));
        Assert.Equal(1, h.CreateCalls);

        var stale = await h.ApplyAsync(prepared);
        Assert.StartsWith(DeploymentService.SkippedPrefix, stale.Message, StringComparison.Ordinal);

        await h.ApplyAsync(await h.PrepareAsync(RingHarness.Request(RingChangeKind.CloseRun, run.RunId)));
        await Assert.ThrowsAsync<DeploymentServiceException>(() => h.PrepareAsync(RingHarness.Request(RingChangeKind.Promote, run.RunId, 2)));
    }

    [Fact]
    public async Task APromoteAfterThePassedDeadlineShiftsThisAndEveryLaterHeldRing()
    {
        using var h = new RingHarness();
        var plan = RingHarness.ThreeRings(hold: true, purpose: "Required") with { Rings = [.. RingHarness.ThreeRings(purpose: "Required").Rings.Select(r => r with { SuccessThresholdPercent = null })] };
        var run = h.Run((await h.CreateRunAsync(plan, DeploymentHarness.TaskSequence, required: true)).Run!.RunId);
        h.UtcNow = new DateTime(2026, 10, 25, 14, 0, 0, DateTimeKind.Utc);
        var promote = await h.PrepareAsync(RingHarness.Request(RingChangeKind.Promote, run.RunId, 2, required: true));
        Assert.False(promote.Blocked, string.Join(" | ", promote.Checks.Where(c => c.Result != "Pass").Select(c => c.Message)));
        // Ring 2 was available 2026-10-23 09:00 local; now is 10:00 local, so it moves to 10:01.
        Assert.Equal(2 * 24 * 60 + 61, promote.ShiftMinutes);
        Assert.Contains(promote.Impact, l => l.Contains("moves 2941 minutes later", StringComparison.Ordinal));
        Assert.Equal(new DateTime(2026, 10, 25, 10, 1, 0), promote.Rows.Single().AvailableTime);
        var promoted = await h.ApplyAsync(promote);
        Assert.True(promoted.Succeeded, promoted.Message);
        var after = h.Run(run.RunId);
        Assert.Equal([0, 2941, 2941], after.Rings.Select(r => r.ShiftedMinutes));
        Assert.Equal(new DateTime(2026, 10, 27, 10, 1, 0), after.Rings[2].AvailableTime);
        var advert = h.Site.List("Adverts").OfType<JsonObject>().Single(a => (string?)a["CollectionID"] == RingHarness.C2);
        Assert.Equal("2026-10-25T10:01:00", (string?)advert["PresentTime"]);
    }

    [Fact]
    public async Task RequiredRingsNeedTheAcknowledgementInTheServiceAlsoWhenARingChangeCarriesTheFlag()
    {
        using var h = new RingHarness();
        var plan = RingHarness.ThreeRings(purpose: "Required");
        var prepared = await h.PrepareAsync(RingHarness.CreateRun(plan, App, await h.ExpandAsync(plan, App)));
        Assert.Equal((3, 1), (prepared.RequiredRings, prepared.RequiredDevices));
        var forged = prepared with { RingChanges = [.. prepared.RingChanges.Select(c => c with { RequiredConfirmed = true })] };
        var refused = await h.ApplyAsync(forged);
        Assert.StartsWith(DeploymentService.SkippedPrefix, refused.Message, StringComparison.Ordinal);
        Assert.Contains("3 Required rings", refused.Message);
        Assert.Equal(0, h.CreateCalls);
        Assert.Empty(h.Runs.List(false, out _));
        var created = await h.ApplyAsync(prepared with { RequiredConfirmed = true });
        Assert.True(created.Succeeded, created.Message);
        Assert.All(h.Site.List("AppAssignments").OfType<JsonObject>(), a => Assert.Equal(0, (int?)a["OfferTypeID"]));
    }

    [Fact]
    public async Task AStalePlanIsSkippedAndNothingIsCreated()
    {
        using var h = new RingHarness();
        var plan = RingHarness.ThreeRings();
        var prepared = await h.PrepareAsync(RingHarness.CreateRun(plan, DeploymentHarness.Package, await h.ExpandAsync(plan, DeploymentHarness.Package)));
        h.Site.List("Packages")[0]!["SourceVersion"] = 9;
        var outcome = await h.ApplyAsync(prepared);
        Assert.False(outcome.Succeeded);
        Assert.Equal(0, h.CreateCalls);
        Assert.StartsWith(DeploymentService.SkippedPrefix, outcome.RingOutcomes[0].Message, StringComparison.Ordinal);
        Assert.Contains("the plan is stale", outcome.RingOutcomes[0].Message);
        Assert.Null(outcome.UndoChange);
        Assert.All(h.Run(outcome.Run!.RunId).Rings, r => Assert.Equal(RingStatuses.Held, r.Status));
    }

    [Fact]
    public async Task AUtcPlanStoresUtcTimesWithoutAShift()
    {
        using var h = new RingHarness();
        var outcome = await h.CreateRunAsync(RingHarness.ThreeRings(purpose: "Required", utc: "Utc"), DeploymentHarness.UpdateGroup, required: true);
        Assert.True(outcome.Succeeded, outcome.Message);
        var stored = h.Site.List("UgAssignments").OfType<JsonObject>().OrderBy(a => (string?)a["TargetCollectionID"]).ToList();
        Assert.All(stored, a => Assert.True((bool)a["UseGMTTimes"]!));
        Assert.Equal(["2026-10-22T09:00:00", "2026-10-24T09:00:00", "2026-10-26T09:00:00"], stored.Select(a => (string?)a["EnforcementDeadline"]));
    }

    [Fact]
    public async Task UndoRunKeepsAndNamesADeploymentAnotherProcessChanged()
    {
        using var h = new RingHarness();
        var run = h.Run((await h.CreateRunAsync(RingHarness.ThreeRings(), App)).Run!.RunId);
        var changed = h.Site.List("AppAssignments").OfType<JsonObject>().Single(a => (string?)a["AssignmentUniqueID"] == run.Rings[1].DeploymentId);
        changed["StartTime"] = "2026-10-23T10:00:00";
        var undo = await h.PrepareAsync(RingHarness.Request(RingChangeKind.UndoRun, run.RunId));
        Assert.Equal([2], undo.KeptRings);
        Assert.Contains(undo.Impact, l => l.Contains("Ring 2 'Pilot'", StringComparison.Ordinal) && l.Contains("kept", StringComparison.Ordinal) && l.Contains("available time", StringComparison.Ordinal));
        var outcome = await h.ApplyAsync(undo);
        Assert.False(outcome.Succeeded);
        Assert.Contains(run.Rings[1].DeploymentId!, outcome.Message);
        Assert.Single(h.Site.List("AppAssignments"));
        var after = h.Run(run.RunId);
        Assert.False(after.IsClosed);
        Assert.Equal([RingStatuses.Removed, RingStatuses.Created, RingStatuses.Removed], after.Rings.Select(r => r.Status));
    }

    [Fact]
    public async Task UndoRunKeepsADeploymentWithoutAVerifiedCreateRecord()
    {
        using var h = new RingHarness();
        var run = h.Run((await h.CreateRunAsync(RingHarness.ThreeRings(), App)).Run!.RunId);
        var lines = File.ReadAllLines(h.Audit.Path);
        lines[0] = lines[0].Replace("\"Result\":\"Success\"", "\"Result\":\"Success\" ", StringComparison.Ordinal);
        File.WriteAllLines(h.Audit.Path, lines);
        var fresh = new RingService(h.Deployments, h.Deployments, h.Audit, h.Plans, h.Runs, utcNow: () => h.UtcNow, local: DeploymentHarness.Local);
        var undo = await fresh.PrepareAsync(RingHarness.Request(RingChangeKind.UndoRun, run.RunId), CancellationToken.None);
        Assert.Equal([1, 2, 3], undo.KeptRings.Order());
    }

    [Fact]
    public async Task ACrashBetweenCreatingAndCreatedIsAdoptedByReconcile()
    {
        using var h = new RingHarness();
        var plan = RingHarness.ThreeRings(hold: true);
        var prepared = await h.PrepareAsync(RingHarness.CreateRun(plan, App, await h.ExpandAsync(plan, App)));
        string? runFile = null;
        h.Site.AfterRun = p =>
        {
            if ((string?)p["Operation"] != "Create") return null;
            runFile = Directory.GetFiles(h.RunsFolder, "*.json").Single();
            File.SetAttributes(runFile, FileAttributes.ReadOnly);
            return null;
        };
        var outcome = await h.ApplyAsync(prepared);
        h.Site.AfterRun = null;
        File.SetAttributes(runFile!, FileAttributes.Normal);
        Assert.Contains("Reconcile", outcome.Message);
        var run = h.Run(outcome.Run?.RunId ?? h.Runs.List(false, out _).Single().RunId);
        Assert.Equal(RingStatuses.Creating, run.Rings[0].Status);
        Assert.Single(h.Site.List("AppAssignments"));

        var promote = await h.PrepareAsync(RingHarness.Request(RingChangeKind.Promote, run.RunId, 2));
        Assert.True(promote.Blocked);

        var reconciled = await h.Service.ReconcileAsync(run.RunId, CancellationToken.None);
        Assert.Equal(RingStatuses.Created, reconciled.Rings[0].Status);
        Assert.Equal((string?)h.Site.List("AppAssignments")[0]!["AssignmentUniqueID"], reconciled.Rings[0].DeploymentId);
        Assert.Contains("adopted", reconciled.Rings[0].Note);
    }

    private static async Task<(RingHarness H, RingRun Run)> CreatingRunAsync(Action<RingHarness>? before = null, bool twice = false, bool created = true)
    {
        var h = new RingHarness();
        var plan = RingHarness.ThreeRings(hold: true);
        var run = h.Runs.Create(new RingRun(Guid.NewGuid().ToString(), "", plan.Name, true, "LocalTime", DeploymentObjectType.Application, App.Id, App.Name, "", RingHarness.Account,
            h.UtcNow, [.. RingPlanRules.Expand(plan, RingHarness.Start, DeploymentHarness.Local).Select(r => new RingRunRing(r.Index, r.Name, r.CollectionId, "", r.Purpose, r.AvailableTime, r.Deadline,
                r.Index == 1 ? RingStatuses.Creating : RingStatuses.Held, r.Index == 1 ? DeploymentHarness.LocalNow : null, null, 0, "", r.SuccessThresholdPercent)
            { Definition = r.Definition, CreatingAtUtc = r.Index == 1 ? h.UtcNow : null, CreatingBy = r.Index == 1 ? RingHarness.Account : null })], false) { ModelId = App.ModelId }, "MCM00035");
        before?.Invoke(h);
        if (twice) h.Site.SetOption("CreateTwice:Application", true);
        if (created)
        {
            var options = RingPlanRules.Options(DeploymentObjectType.Application, plan.Rings[0], RingHarness.Start, null, false);
            var outcome = (await h.Deployments.ApplyAsync([await h.Deployments.PrepareAsync(DeploymentHarness.Create(App, options), CancellationToken.None)], new Progress<DeploymentOutcome>(), CancellationToken.None)).Single();
            Assert.True(outcome.Succeeded || twice, outcome.Message);
        }
        return (h, run);
    }

    [Fact]
    public async Task ReconcileKeepsTwoMatchingDeploymentsBlocked()
    {
        var (h, run) = await CreatingRunAsync(twice: true);
        using (h)
        {
            var ring = (await h.Service.ReconcileAsync(run.RunId, CancellationToken.None)).Rings[0];
            Assert.Equal(RingStatuses.Creating, ring.Status);
            Assert.Contains("2 deployments", ring.Note);
        }
    }

    [Fact]
    public async Task ReconcileWaitsFiveMinutesThenReturnsARingWithoutAMatchToHeld()
    {
        var (h, run) = await CreatingRunAsync(created: false);
        using (h)
        {
            h.UtcNow = h.UtcNow.AddMinutes(4);
            var waiting = (await h.Service.ReconcileAsync(run.RunId, CancellationToken.None)).Rings[0];
            Assert.Equal(RingStatuses.Creating, waiting.Status);
            Assert.Contains("5 minutes", waiting.Note);
            h.UtcNow = h.UtcNow.AddMinutes(2);
            Assert.Equal(RingStatuses.Held, (await h.Service.ReconcileAsync(run.RunId, CancellationToken.None)).Rings[0].Status);
        }
    }

    [Fact]
    public async Task ReconcileDoesNotAdoptADeploymentOfAnotherAccountOrWithOtherValuesOrMadeBeforeTheCreate()
    {
        var (h, run) = await CreatingRunAsync(x => x.Site.SetOption("CreatedBy", @"CONTOSO\OtherAdmin"));
        using (h)
        {
            Assert.Equal(RingStatuses.Creating, (await h.Service.ReconcileAsync(run.RunId, CancellationToken.None)).Rings[0].Status);
        }
        (h, run) = await CreatingRunAsync(x => x.Site.SetOption("CreatedUtc", "2026-10-20T11:50:00"));
        using (h)
        {
            Assert.Equal(RingStatuses.Creating, (await h.Service.ReconcileAsync(run.RunId, CancellationToken.None)).Rings[0].Status);
        }
        (h, run) = await CreatingRunAsync();
        using (h)
        {
            h.Site.List("AppAssignments")[0]!["StartTime"] = "2026-10-21T09:30:00";
            Assert.Equal(RingStatuses.Creating, (await h.Service.ReconcileAsync(run.RunId, CancellationToken.None)).Rings[0].Status);
        }
        (h, run) = await CreatingRunAsync();
        using (h)
        {
            h.Site.Fail("query:SMS_ApplicationAssignment");
            var ring = (await h.Service.ReconcileAsync(run.RunId, CancellationToken.None)).Rings[0];
            Assert.Equal(RingStatuses.Creating, ring.Status);
            Assert.DoesNotContain("SECRET", ring.Note, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task ReconcileMarksRemovedOnlyWhenTheDeploymentIsGoneAndClosesTheFinishedRun()
    {
        using var h = new RingHarness();
        var run = h.Run((await h.CreateRunAsync(RingHarness.ThreeRings(hold: true), App)).Run!.RunId);
        h.Site.Fail("query:SMS_ApplicationAssignment");
        var error = await h.Service.LiveCountsAsync(run, CancellationToken.None);
        Assert.Equal((RingLiveStates.Error, false), (error[0].State, error[0].CountsKnown));
        Assert.DoesNotContain("SECRET", error[0].Message, StringComparison.Ordinal);
        h.Site.Fail("query:SMS_ApplicationAssignment");
        Assert.Equal(RingStatuses.Created, (await h.Service.ReconcileAsync(run.RunId, CancellationToken.None)).Rings[0].Status);

        Assert.Equal(RingLiveStates.Found, (await h.Service.LiveCountsAsync(run, CancellationToken.None))[0].State);
        h.Site.Fail("query:SMS_DeploymentSummary");
        var unsummarized = await h.Service.LiveCountsAsync(run, CancellationToken.None);
        Assert.Equal((RingLiveStates.Found, false), (unsummarized[0].State, unsummarized[0].CountsKnown));

        h.Site.List("AppAssignments").Clear();
        h.Summarize(run.Rings[0].DeploymentId!, 4, 4);
        Assert.Equal(RingLiveStates.NotFound, (await h.Service.LiveCountsAsync(run, CancellationToken.None))[0].State);
        var after = await h.Service.ReconcileAsync(run.RunId, CancellationToken.None);
        Assert.Equal([RingStatuses.Removed, RingStatuses.Held, RingStatuses.Held], after.Rings.Select(r => r.Status));
        Assert.True(after.IsClosed);
        Assert.Empty(h.Runs.List(false, out _));
    }

    [Fact]
    public async Task AnIdOfTheWrongFormIsNeverTakenAsADeletion()
    {
        using var h = new RingHarness();
        var run = h.Run((await h.CreateRunAsync(RingHarness.ThreeRings(hold: true), App)).Run!.RunId);
        var path = Path.Combine(h.RunsFolder, run.FileName);
        var json = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        json["Rings"]![0]!["DeploymentID"] = "16777503";
        File.WriteAllText(path, json.ToJsonString());
        var counts = await h.Service.LiveCountsAsync(h.Run(run.RunId), CancellationToken.None);
        Assert.Equal(RingLiveStates.Error, counts[0].State);
        Assert.Contains("form", counts[0].Message);
        Assert.Equal(RingStatuses.Created, (await h.Service.ReconcileAsync(run.RunId, CancellationToken.None)).Rings[0].Status);
    }

    [Fact]
    public async Task CancelStopsBetweenRingsAndReportsTheRestAsSkipped()
    {
        using var h = new RingHarness();
        var plan = RingHarness.ThreeRings();
        var prepared = await h.PrepareAsync(RingHarness.CreateRun(plan, App, await h.ExpandAsync(plan, App)));
        using var cancel = new CancellationTokenSource();
        h.Site.AfterRun = p => { if ((string?)p["Operation"] == "Create") cancel.Cancel(); return null; };
        var outcome = await h.ApplyAsync(prepared, cancel.Token);
        Assert.Equal(1, h.CreateCalls);
        Assert.True(outcome.RingOutcomes[0].Succeeded);
        Assert.All(outcome.RingOutcomes.Skip(1), o => Assert.StartsWith(DeploymentService.SkippedPrefix + "the run was cancelled", o.Message, StringComparison.Ordinal));
        Assert.NotNull(outcome.UndoChange);
    }

    [Fact]
    public async Task ARingCreateFailureGivesNoRawExceptionText()
    {
        using var h = new RingHarness();
        var plan = RingHarness.ThreeRings();
        var prepared = await h.PrepareAsync(RingHarness.CreateRun(plan, App, await h.ExpandAsync(plan, App)));
        h.Site.Fail("create:Application");
        var outcome = await h.ApplyAsync(prepared);
        Assert.False(outcome.Succeeded);
        Assert.DoesNotContain("SECRET", outcome.Message + string.Join(" ", outcome.RingOutcomes.Select(o => o.Message)), StringComparison.Ordinal);
        Assert.Equal(RingStatuses.Held, h.Run(outcome.Run!.RunId).Rings[0].Status);
    }

    [Fact]
    public async Task AHistoryUndoOfARingRecordWorksAsForASingleDeployment()
    {
        using var h = new RingHarness();
        await h.CreateRunAsync(RingHarness.ThreeRings(), App);
        var record = h.Records.First(r => r.Event == AuditEvents.Create && r.RingIndex == 2);
        var fresh = new DeploymentService(h.Site, new DeploymentHarness.FakeCollections(), h.Audit, utcNow: () => DeploymentHarness.UtcNow, local: DeploymentHarness.Local);
        var remove = await fresh.PrepareAsync(DeploymentChangeBuilder.RemoveFromAudit(record)!, CancellationToken.None);
        Assert.False(remove.Blocked, string.Join(" | ", remove.Checks.Select(c => c.Message)));
        Assert.True((await fresh.ApplyAsync([remove], new Progress<DeploymentOutcome>(), CancellationToken.None)).Single().Succeeded);
        Assert.Equal(2, h.Site.List("AppAssignments").Count);
    }

    [Fact]
    public async Task CloseRunCancelsHeldRingsAndMovesTheFile()
    {
        using var h = new RingHarness();
        var run = h.Run((await h.CreateRunAsync(RingHarness.ThreeRings(hold: true), App)).Run!.RunId);
        var close = await h.PrepareAsync(RingHarness.Request(RingChangeKind.CloseRun, run.RunId));
        Assert.Equal([2, 3], close.CancelledRings);
        var closed = await h.ApplyAsync(close);
        Assert.True(closed.Succeeded, closed.Message);
        Assert.True(closed.Run!.IsClosed);
        Assert.Equal([RingStatuses.Created, RingStatuses.Cancelled, RingStatuses.Cancelled], closed.Run.Rings.Select(r => r.Status));
        Assert.Single(await h.Service.ListRunsAsync(true, CancellationToken.None));
        Assert.Single(h.Site.List("AppAssignments"));
    }
}
