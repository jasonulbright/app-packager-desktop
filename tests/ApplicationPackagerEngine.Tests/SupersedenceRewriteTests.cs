using System.Text;
using ApplicationPackagerEngine.Services.Supersedence;

namespace ApplicationPackagerEngine.Tests;

/// <summary>The optional rewrite of the version clause of an old application to Exact, and its log and restore.</summary>
public sealed class SupersedenceRewriteTests
{
    private static LineApplication App(string title, string version, params string[] dts) =>
        new("1", title, version, false, dts.Select((n, i) => new LineDeploymentType((i + 1).ToString(), n)).ToList());

    private static StoredClause FileClause(string op = "GreaterEquals", string expected = "1.0.0") =>
        new("File", "File_1", op, expected, @"C:\Vendor", "app.exe", false, "", "", "");

    private static readonly Func<LineApplication, StoredClause, ExactGuardVerdict> Allow = (_, _) => ExactGuardVerdict.Allowed;

    private static RewritePlan Plan(LineApplication app, StoredDetection detection, string[]? required = null,
        Func<LineApplication, StoredClause, ExactGuardVerdict>? guard = null) =>
        RewritePlanner.Plan([app],
            new Dictionary<string, StoredDetection> { [RewritePlanner.Key(app.Title, app.DeploymentTypes[0].Name)] = detection },
            new Dictionary<string, IReadOnlyList<string>> { [app.Title] = required ?? [] },
            guard ?? Allow);

    // ---- reading what the site stores ----

    [Fact]
    public void TheFileClauseOfARealDeploymentTypeIsRead()
    {
        var detection = OldVersionRewrite.ReadDetection(SupersedenceRealDetectionXmlTests.RealXml);

        Assert.Null(detection.NotRewritableReason);
        var clause = detection.Clause!;
        Assert.Equal(("File", "GreaterEquals", "1.0.0", @"C:\APDLab", "app.exe", false),
            (clause.Kind, clause.Operator, clause.ExpectedValue, clause.Path, clause.FileName, clause.Is64Bit));
        Assert.StartsWith("File_", clause.LogicalName);
    }

    [Fact]
    public void TheRegistryClauseOfARealDeploymentTypeIsRead()
    {
        var clause = OldVersionRewrite.ReadDetection(RealRegistryDetectionXml.Xml).Clause!;

        Assert.Equal(("Registry", "GreaterEquals", "1.0.0", "HKEY_LOCAL_MACHINE", @"SOFTWARE\APDLab", "DisplayVersion", true),
            (clause.Kind, clause.Operator, clause.ExpectedValue, clause.Hive, clause.Key, clause.ValueName, clause.Is64Bit));
        Assert.StartsWith("RegSetting_", clause.LogicalName);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("<<<not xml")]
    [InlineData("<AppMgmtDigest><DeploymentType/></AppMgmtDigest>")]
    public void ADocumentThatIsNotAnEnhancedDetectionIsNotRewritable(string? xml)
    {
        var detection = OldVersionRewrite.ReadDetection(xml);

        Assert.Null(detection.Clause);
        Assert.NotNull(detection.NotRewritableReason);
    }

    [Fact]
    public void ADetectionWithTwoClausesIsNotRewritable()
    {
        var two = SupersedenceRealDetectionXmlTests.RealXml.Replace("&lt;/Settings&gt;", "&lt;File LogicalName=\"File_2\"&gt;&lt;Path&gt;C:\\x&lt;/Path&gt;&lt;Filter&gt;y&lt;/Filter&gt;&lt;/File&gt;&lt;/Settings&gt;");

        Assert.Contains("more than one clause", OldVersionRewrite.ReadDetection(two).NotRewritableReason);
    }

    // ---- the plan ----

    [Fact]
    public void AClauseThatPassesEveryCheckIsRewritten()
    {
        var plan = Plan(App("Old 1.0", "1.0.0", "Old 1.0 - x64"), new(FileClause(), null));

        Assert.Equal(1, plan.ClauseCount);
        Assert.Equal(1, plan.ApplicationCount);
        Assert.Empty(plan.Blocked);
    }

    [Fact]
    public void AnApplicationWithARequiredDeploymentIsBlockedAndNamed()
    {
        var plan = Plan(App("Old 1.0", "1.0.0", "Old 1.0 - x64"), new(FileClause(), null), required: ["All Workstations"]);

        Assert.Equal(0, plan.ClauseCount);
        Assert.Contains("required deployment (All Workstations)", plan.Blocked.Single());
    }

    [Theory]
    [InlineData("Equals", "1.0.0", "the operator is Equals")]
    [InlineData("GreaterEquals", "0.9.0", "is not the version of the application")]
    public void AClauseThatIsNotAGreaterEqualsOfTheVersionOfTheApplicationIsLeftWithTheReason(string op, string expected, string reason)
    {
        var plan = Plan(App("Old 1.0", "1.0.0", "Old 1.0 - x64"), new(FileClause(op, expected), null));

        Assert.Equal(0, plan.ClauseCount);
        Assert.Contains(reason, plan.Items.Single().Skip);
    }

    [Fact]
    public void AClauseThatFailsTheExactGuardIsNotRewrittenAndTheReasonIsListed()
    {
        var plan = Plan(App("Old 1.0", "1.0.0", "Old 1.0 - x64"), new(FileClause(), null),
            guard: (_, _) => new ExactGuardVerdict(ClauseResult.ExactBlocked, "no installed value is recorded for version 1.0.0"));

        Assert.Equal(0, plan.ClauseCount);
        Assert.Contains("no installed value", plan.Items.Single().Skip);
    }

    [Fact]
    public void ADetectionThatCannotBeReadIsLeftWithItsReason()
    {
        var plan = Plan(App("Old 1.0", "1.0.0", "Old 1.0 - x64"), new(null, "the detection has more than one clause or a connector, which is not rewritten"));

        Assert.Contains("connector", plan.Items.Single().Skip);
    }

    [Fact]
    public void TheGuardOfTheRunnerNeedsARecordedValueForTheVersionOfTheOldApplication()
    {
        var settings = new SupersedenceSettings();
        settings.InstalledValues[ExactGuard.RecordKey("catalog:vendor", "1.0.0")] = "1.0.0";
        var scope = new RewriteScope("m", "catalog:vendor", "MCM", null, settings);

        Assert.Equal(ClauseResult.ExactApplied, OldVersionRewriteRunner.Guard(scope, App("Old", "1.0.0", "d"), FileClause()).Result);
        // A value for another version is no value for this one; a value that differs blocks.
        Assert.Equal(ClauseResult.ExactBlocked, OldVersionRewriteRunner.Guard(scope, App("Old", "0.9.0", "d"), FileClause(expected: "0.9.0")).Result);
        settings.InstalledValues[ExactGuard.RecordKey("catalog:vendor", "1.0.0")] = "1.0.0.5";
        Assert.Contains("1.0.0.5", OldVersionRewriteRunner.Guard(scope, App("Old", "1.0.0", "d"), FileClause()).Detail);
    }

    // ---- the scripts ----

    [Fact]
    public void TheRewriteScriptRemovesTheClauseByItsLogicalNameAndAddsTheSameClauseWithTheOtherOperator()
    {
        var script = OldVersionRewrite.BuildRewriteScript("MCM", null, "Old 1.0", "Old 1.0 - x64", FileClause(), "IsEquals");

        Assert.Contains(@"New-CMDetectionClauseFile -Path 'C:\Vendor' -FileName 'app.exe' -PropertyType Version -Value -ExpressionOperator IsEquals -ExpectedValue '1.0.0'", script);
        Assert.Contains("Set-CMScriptDeploymentType -ApplicationName 'Old 1.0' -DeploymentTypeName 'Old 1.0 - x64' -RemoveDetectionClause 'File_1' -AddDetectionClause $rewriteClause", script);
        Assert.Contains("Rewritten|{0}|{1}|{2}|{3}|{4}", script);
        // The names go out as base64 so that a separator in a title cannot turn a rewrite into a failure.
        Assert.Contains("(ConvertTo-RewriteField 'Old 1.0')", script);
        // The clause object is built off the CM drive, as the publish script does.
        Assert.True(script.IndexOf("Set-Location C:", StringComparison.Ordinal) < script.IndexOf("New-CMDetectionClauseFile", StringComparison.Ordinal));
    }

    [Fact]
    public void TheRegistryRewriteKeepsTheHiveTheKeyTheValueAndTheView()
    {
        var clause = OldVersionRewrite.ReadDetection(RealRegistryDetectionXml.Xml).Clause!;

        var script = OldVersionRewrite.BuildRewriteScript("MCM", null, "Reg", "Reg - x64", clause, "IsEquals");

        Assert.Contains(@"New-CMDetectionClauseRegistryKeyValue -Hive LocalMachine -KeyName 'SOFTWARE\APDLab' -ValueName 'DisplayVersion' -PropertyType Version -Value -ExpressionOperator IsEquals -ExpectedValue '1.0.0' -Is64Bit", script);
    }

    [Fact]
    public void AnOperatorOtherThanTheTwoIsRefused() =>
        Assert.Throws<ArgumentException>(() => OldVersionRewrite.BuildRewriteScript("MCM", null, "A", "D", FileClause(), "NotEquals"));

    [Fact]
    public void TheDeploymentScriptAsksOneApplicationAndTheRequiredOnesAreRead()
    {
        var script = OldVersionRewrite.BuildDeploymentScript("MCM", null, "O'Neil");
        string B64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s));
        var output = $"APDDEP|{B64("All Workstations")}|0\nAPDDEP|{B64("Pilot")}|2\nnoise\n";

        Assert.Contains("$rewriteApplication = 'O''Neil'", script);
        Assert.Contains("Get-CMApplicationDeployment -Name $rewriteApplication -DisableWildcardHandling", script);
        Assert.Equal(["All Workstations"], OldVersionRewrite.RequiredCollections(output));
    }

    // ---- the log and the restore ----

    private static RewriteLogEntry Entry(int minute, string app = "Old 1.0", string dt = "Old 1.0 - x64", string oldOp = "GreaterEquals", string newOp = "IsEquals", bool restored = false) =>
        new(new DateTimeOffset(2026, 11, 12, 10, minute, 0, TimeSpan.Zero), app, dt, oldOp, newOp, "File_1", restored);

    [Fact]
    public void TheLogRoundTripsAndSkipsALineThatCannotBeRead()
    {
        var text = RewriteLog.Serialize(Entry(1)) + "\nnot json\n" + RewriteLog.Serialize(Entry(2, app: "Other"));

        var entries = RewriteLog.Read(text);

        Assert.Equal(["Old 1.0", "Other"], entries.Select(e => e.Application));
    }

    [Fact]
    public void TheScriptOutputBecomesLogEntries()
    {
        var at = new DateTimeOffset(2026, 11, 12, 10, 0, 0, TimeSpan.Zero);

        string B64(string s) => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(s));
        var entries = RewriteLog.FromScriptOutput($"noise\nRewritten|{B64("Old 1.0")}|{B64("Old 1.0 - x64")}|GreaterEquals|IsEquals|{B64("File_1")}\n", at);

        var entry = Assert.Single(entries);
        Assert.Equal(("Old 1.0", "Old 1.0 - x64", "GreaterEquals", "IsEquals", "File_1"),
            (entry.Application, entry.DeploymentType, entry.OldOperator, entry.NewOperator, entry.LogicalName));
    }

    [Fact]
    public void RestoreWritesBackTheFirstOperatorOfEachRewrittenTypeAndOnlyForTheLine()
    {
        var log = new[] { Entry(1), Entry(2, oldOp: "IsEquals", newOp: "GreaterEquals", restored: false), Entry(3, app: "Other App") };

        var restore = RewriteLog.ToRestore(log, ["Old 1.0"]);

        var entry = Assert.Single(restore);
        Assert.Equal("GreaterEquals", entry.OldOperator);
        Assert.Equal("Old 1.0", entry.Application);
    }

    [Fact]
    public void ATypeThatWasRestoredIsNotRestoredAgain()
    {
        var log = new[] { Entry(1), Entry(2, oldOp: "IsEquals", newOp: "GreaterEquals", restored: true) };

        Assert.Empty(RewriteLog.ToRestore(log, ["Old 1.0"]));
    }

    [Fact]
    public void ATypeThatWasRewrittenAgainAfterARestoreIsRestoredToTheOriginalOperator()
    {
        var log = new[] { Entry(1), Entry(2, oldOp: "IsEquals", newOp: "GreaterEquals", restored: true), Entry(3) };

        Assert.Equal("GreaterEquals", Assert.Single(RewriteLog.ToRestore(log, ["Old 1.0"])).OldOperator);
    }

    [Fact]
    public void ATitleThatHoldsASeparatorStillGivesALogEntry()
    {
        string B64(string s) => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(s));

        var entry = Assert.Single(RewriteLog.FromScriptOutput(
            $"Rewritten|{B64("Foo | Bar 1.0")}|{B64("Foo | Bar 1.0 - x64")}|GreaterEquals|IsEquals|{B64("File_1")}", DateTimeOffset.UtcNow));

        Assert.Equal(("Foo | Bar 1.0", "Foo | Bar 1.0 - x64"), (entry.Application, entry.DeploymentType));
    }

    [Fact]
    public void RestoreTakesTheFirstRewriteEntryAndNeverARestoreEntryWhenTheFirstLineWasLost()
    {
        // The first rewrite line is lost (a bad line is skipped by Read): what is left starts with a restore entry.
        var log = new[] { Entry(2, oldOp: "IsEquals", newOp: "GreaterEquals", restored: true), Entry(3) };

        Assert.Equal("GreaterEquals", Assert.Single(RewriteLog.ToRestore(log, ["Old 1.0"])).OldOperator);
        Assert.Empty(RewriteLog.ToRestore([Entry(2, oldOp: "IsEquals", newOp: "GreaterEquals", restored: true)], ["Old 1.0"]));
    }

    // ---- which applications are old ----

    private static LineApplication Line(string title, string version, bool expired = false) =>
        new(title, title, version, expired, [new LineDeploymentType("1", title + " - x64")]);

    [Fact]
    public void EveryApplicationThatHoldsTheNewestVersionIsCurrentAlsoUnderAnotherTitleAndForm()
    {
        var old = OldVersionRewriteRunner.OldOnes([Line("Foo 2.0", "2.0"), Line("Foo 2.0 (renamed)", "2.0.0"), Line("Foo 1.0", "1.0")]);

        Assert.Equal(["Foo 1.0"], old.Select(a => a.Title));
    }

    [Fact]
    public void ARetiredNewestOrASingleApplicationLeavesNothingToRewriteOrOnlyTheOlderOnes()
    {
        Assert.Empty(OldVersionRewriteRunner.OldOnes([Line("Foo 1.0", "1.0")]));
        // The retired 3.0 is not the newest: 2.0 is, and only 1.0 is old.
        Assert.Equal(["Foo 1.0"], OldVersionRewriteRunner.OldOnes([Line("Foo 3.0", "3.0", expired: true), Line("Foo 2.0", "2.0"), Line("Foo 1.0", "1.0")]).Select(a => a.Title));
        Assert.Empty(OldVersionRewriteRunner.OldOnes([Line("Foo x", "not-a-version")]));
    }

    [Fact]
    public void TwoApplicationsThatHoldATypeOfTheSameNameNeverShareADetection()
    {
        var one = LineApp("Foo 1.0", "1.0", "Install");
        var two = LineApp("Foo 1.0 x", "1.0", "Install");
        var detections = new Dictionary<string, StoredDetection>
        {
            [RewritePlanner.Key("Foo 1.0", "Install")] = new(FileClause() with { LogicalName = "File_ONE", Path = @"C:\one" }, null),
            [RewritePlanner.Key("Foo 1.0 x", "Install")] = new(FileClause() with { LogicalName = "File_TWO", Path = @"C:\two" }, null)
        };

        var plan = RewritePlanner.Plan([one, two], detections, new Dictionary<string, IReadOnlyList<string>>(), Allow);

        Assert.Equal(["File_ONE", "File_TWO"], plan.Items.Select(i => i.Clause!.LogicalName));
    }

    private static LineApplication LineApp(string title, string version, string dtName) =>
        new(title, title, version, false, [new LineDeploymentType("1", dtName)]);

    [Fact]
    public void ARegistryClauseOfAnotherHiveIsLeftAtPlanTimeWithItsReason()
    {
        var clause = new StoredClause("Registry", "Reg_1", "GreaterEquals", "1.0.0", "", "", true, "HKEY_CLASSES_ROOT", "K", "V");

        var plan = Plan(App("Old 1.0", "1.0.0", "Old 1.0 - x64"), new(clause, null));

        Assert.Equal(0, plan.ClauseCount);
        Assert.Contains("HKEY_CLASSES_ROOT", plan.Items.Single().Skip);
    }

    [Fact]
    public void TheDeploymentScriptReadsTheNameLiterallyAndEndsWithAMarker()
    {
        var script = OldVersionRewrite.BuildDeploymentScript("MCM", null, "Foo [x64] 1.0");

        Assert.Contains("-DisableWildcardHandling", script);
        Assert.Contains("$rewriteApplication = 'Foo [x64] 1.0'", script);
        Assert.Contains("APDDEPEND", script);
        Assert.True(OldVersionRewrite.DeploymentsComplete("APDDEP|x|2\nAPDDEPEND\n"));
        // A script that printed nothing is not an answer of "no required deployment".
        Assert.False(OldVersionRewrite.DeploymentsComplete(""));
        Assert.False(OldVersionRewrite.DeploymentsComplete("APDDEP|x|0\n"));
    }

    [Fact]
    public void TheConfirmationTextSaysThatADeviceWithANewerVersionReadsNotInstalled() =>
        Assert.Contains("not installed", RewritePlanner.ConfirmationText);
}
