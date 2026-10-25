using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests.Deployments.Rings;

/// <summary>New plans are created without overwrite; a replace checks the file stamp the editor read.</summary>
public sealed class RingPlanStoreConcurrencyTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "apkg-ringplans-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
    }

    private RingPlanStore Store() => new(() => _folder);

    private static RingPlan Plan(string description) =>
        new("Office", description, false, "LocalTime", [RingPlanRulesTests.Ring("QA", "MCM00098")], false);

    [Fact]
    public async Task SessionsThatCreateTheSameNewPlanAtOnceNeverOverwriteEachOther()
    {
        Store().List();
        using var start = new ManualResetEventSlim();
        var saved = 0;
        var refused = 0;
        var tasks = Enumerable.Range(0, 16).Select(i => Task.Factory.StartNew(() =>
        {
            start.Wait();
            try
            {
                Store().Save(Plan("session " + i), replace: false);
                Interlocked.Increment(ref saved);
                return "session " + i;
            }
            catch (DeploymentServiceException ex)
            {
                Assert.True(ex.Message.Contains("exists", StringComparison.Ordinal) || ex.Message.Contains("Another session", StringComparison.Ordinal), ex.Message);
                Interlocked.Increment(ref refused);
                return null;
            }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();
        start.Set();
        var winners = (await Task.WhenAll(tasks)).OfType<string>().ToList();
        Assert.Equal(1, saved);
        Assert.Equal(15, refused);
        Assert.Equal(winners.Single(), Store().List().Single(p => p.Name == "Office").Description);
    }

    [Fact]
    public void AReplaceIsRefusedWhenThePlanChangedSinceTheEditorReadIt()
    {
        Store().Save(Plan("first"), replace: false);
        var opened = Store().List().Single(p => p.Name == "Office");
        Assert.NotEqual("", opened.FileStamp);
        Store().Save(Plan("another session"), replace: true, expectedFileStamp: Store().List().Single(p => p.Name == "Office").FileStamp);

        var ex = Assert.Throws<DeploymentServiceException>(() => Store().Save(opened with { Description = "mine" }, replace: true, expectedFileStamp: opened.FileStamp));
        Assert.Contains("changed since", ex.Message);
        Assert.Equal("another session", Store().List().Single(p => p.Name == "Office").Description);

        var fresh = Store().List().Single(p => p.Name == "Office");
        Assert.Equal("mine", Store().Save(fresh with { Description = "mine" }, replace: true, expectedFileStamp: fresh.FileStamp).Description);
    }

    [Fact]
    public void AReplaceWithAStampIsRefusedWhenThePlanWasDeleted()
    {
        Store().Save(Plan("first"), replace: false);
        var opened = Store().List().Single(p => p.Name == "Office");
        Store().Delete("Office");
        var ex = Assert.Throws<DeploymentServiceException>(() => Store().Save(opened, replace: true, expectedFileStamp: opened.FileStamp));
        Assert.Contains("does not exist any more", ex.Message);
    }

    [Fact]
    public void TheRingServiceUsesTheStampThePlanCarries()
    {
        using var h = new RingHarness();
        h.Plans.Save(Plan("first"), replace: false);
        var opened = h.Service.ListPlans().Single(p => p.Name == "Office");
        h.Plans.Save(Plan("another session"), replace: true);
        Assert.Throws<DeploymentServiceException>(() => h.Service.SavePlan(opened with { Description = "mine" }, replace: true));
    }
}
