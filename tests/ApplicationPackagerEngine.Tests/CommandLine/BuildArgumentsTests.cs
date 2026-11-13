using ApplicationPackagerBuild;
using ApplicationPackagerEngine.Models.Workbench;
using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests.CommandLine;

public sealed class BuildArgumentsTests
{
    private static BuildOptions Parsed(params string[] args)
    {
        var parsed = BuildArguments.Parse(args);
        Assert.True(parsed.Ok, parsed.Error);
        return parsed.Options!;
    }

    private static string Refused(params string[] args)
    {
        var parsed = BuildArguments.Parse(args);
        Assert.False(parsed.Ok);
        return parsed.Error!;
    }

    [Theory]
    [InlineData("--help")]
    [InlineData("-h")]
    [InlineData("-?")]
    [InlineData("--HELP")]
    public void HelpNeedsNoOtherArgument(string arg) => Assert.True(Parsed(arg).Help);

    [Fact]
    public void TheVersionRuleAndTheSupersedenceModeParseAndOutrankNothingWhenAbsent()
    {
        var given = Parsed("--application", "a", "--package", "--version-rule", "Exact", "--supersedence", "replace-uninstall");
        var absent = Parsed("--application", "a", "--package");

        Assert.Equal("Exact", given.VersionRule);
        Assert.Equal("replace-uninstall", given.Supersedence);
        Assert.Null(absent.VersionRule);
        Assert.Null(absent.Supersedence);
    }

    [Theory]
    [InlineData("--version-rule", "sometimes", "Unknown --version-rule value 'sometimes'; expected exact or minimum.")]
    [InlineData("--supersedence", "maybe", "Unknown --supersedence value 'maybe'; expected off, replace or replace-uninstall.")]
    public void AnUnknownVersionRuleOrModeIsRefusedWithTheAllowedWords(string option, string value, string message) =>
        Assert.Equal(message, Refused("--application", "a", "--package", option, value));

    [Fact]
    public void ReplaceAndUninstallWithAMinimumRuleOnTheCommandLineIsRefusedAtParseTime() =>
        Assert.Equal(ApplicationPackagerEngine.Services.Supersedence.SupersedenceResolver.BlockedText,
            Refused("--application", "a", "--package", "--version-rule", "minimum", "--supersedence", "replace-uninstall"));

    [Theory]
    [InlineData("plan")]
    [InlineData("APPLY")]
    [InlineData("restore")]
    public void TheRewriteOfTheOldVersionsParsesAndTakesTheConfirmation(string word)
    {
        var options = Parsed("--application", "a", "--rewrite-old-versions", word, "--yes");

        Assert.Equal(word.ToLowerInvariant(), options.RewriteOldVersions);
        Assert.True(options.Yes);
        Assert.False(Parsed("--application", "a", "--rewrite-old-versions", word).Yes);
        Assert.Null(Parsed("--application", "a", "--package").RewriteOldVersions);
    }

    [Fact]
    public void TheRewriteRefusesAnUnknownWordACombinationAndAYesWithoutIt()
    {
        Assert.Equal("Unknown --rewrite-old-versions value 'now'; expected plan, apply or restore.",
            Refused("--application", "a", "--rewrite-old-versions", "now"));
        Assert.Equal("--rewrite-old-versions runs alone: it does not stage or package.",
            Refused("--application", "a", "--rewrite-old-versions", "plan", "--package"));
        Assert.Equal("Option '--yes' applies to --rewrite-old-versions only.", Refused("--application", "a", "--package", "--yes"));
    }

    [Fact]
    public void TheRecordOfAnInstalledValueAndTheAcceptanceOfABlockParseAloneWithAVersion()
    {
        var record = Parsed("--application", "a", "--version", "1.2.3", "--record-installed-value", "1.2.3.4");
        var accept = Parsed("--application", "a", "--version", "1.2.3", "--accept-exact-block");

        Assert.Equal("1.2.3.4", record.RecordInstalledValue);
        Assert.True(record.IsSupersedenceRecord);
        Assert.False(record.AcceptExactBlock);
        Assert.True(accept.AcceptExactBlock);
        Assert.True(accept.IsSupersedenceRecord);
        Assert.False(Parsed("--application", "a", "--package").IsSupersedenceRecord);
    }

    [Fact]
    public void TheRecordRefusesAMissingVersionACombinationAndAValueThatIsNoVersion()
    {
        Assert.Equal("--record-installed-value and --accept-exact-block need --version: the value belongs to one version.",
            Refused("--application", "a", "--accept-exact-block"));
        Assert.Equal("--record-installed-value and --accept-exact-block run alone: they do not stage, package or rewrite.",
            Refused("--application", "a", "--version", "1.0", "--accept-exact-block", "--package"));
        Assert.Equal("--record-installed-value and --accept-exact-block are separate runs; give one.",
            Refused("--application", "a", "--version", "1.0", "--accept-exact-block", "--record-installed-value", "1.0"));
        Assert.Contains("is not a version",
            Refused("--application", "a", "--version", "1.0", "--record-installed-value", "x; rm"));
        Assert.Contains("needs a value", Refused("--application", "a", "--version", "1.0", "--record-installed-value"));
    }

    [Fact]
    public void UsageNamesEveryOptionTheParserAccepts()
    {
        foreach (var option in new[]
                 {
                     "--application", "--profile", "--version", "--target", "--stage", "--package", "--download-root",
                     "--estimated-minutes", "--maximum-minutes", "--packagers-root", "--log-folder", "--site-code",
                     "--provider-machine-name", "--file-server-path", "--comment", "--wsus-server", "--wsus-port",
                     "--wsus-use-ssl", "--wsus-classification", "--wsus-approval-group", "--wsus-decline-superseded",
                     "--on-existing", "--local-source-folder", "--data-root", "--version-rule", "--supersedence", "--rewrite-old-versions", "--yes",
                     "--record-installed-value", "--accept-exact-block", "--help"
                 })
            Assert.Contains(option, BuildArguments.Usage);
    }

    [Fact]
    public void ApplicationIsRequired() =>
        Assert.Equal("--application is required. Run AppPackagerBuild --help for usage.", Refused("--stage"));

    [Fact]
    public void APhaseIsRequired() =>
        Assert.Equal("Specify --stage, --package, or both.", Refused("--application", "putty"));

    [Fact]
    public void AMecmAndWsusPackageNeedsTheStageOfTheSameRun()
    {
        Assert.Equal("A MECMAndWSUS package publishes the build staged in the same run; pass --stage together with --package.",
            Refused("--application", "putty", "--target", "MECMAndWSUS", "--package"));
        Assert.True(Parsed("--application", "putty", "--target", "MECMAndWSUS", "--package", "--stage").Package);
    }

    [Fact]
    public void AWsusOnlyPackageMayRunAlone() =>
        Assert.Equal(RunTargets.WsusOnly, Parsed("--application", "putty", "--target", "wsusonly", "--package").Target);

    [Fact]
    public void EveryTargetNameParsesToItsCanonicalSpelling()
    {
        foreach (var target in RunTargets.All)
            Assert.Equal(target, Parsed("--application", "a", "--stage", "--target", target.ToLowerInvariant()).Target);
        Assert.Equal(RunTargets.Mecm, Parsed("--application", "a", "--stage").Target);
    }

    [Fact]
    public void AnUnknownTargetListsTheKnownOnes() =>
        Assert.Equal("Unknown target 'Sccm'; expected ContentOnly, MECM, MECMAndIntune, IntuneOnly, MECMAndWSUS, WSUSOnly.",
            Refused("--application", "a", "--stage", "--target", "Sccm"));

    [Fact]
    public void MalformedCommandLinesAreRefusedWithTheReason()
    {
        Assert.Equal("Unknown option '--bogus'. Run AppPackagerBuild --help for usage.", Refused("--application", "a", "--stage", "--bogus"));
        Assert.Equal("Option '--profile' is given more than once.", Refused("--application", "a", "--stage", "--profile", "x", "--profile", "y"));
        Assert.Equal("Option '--stage' is given more than once.", Refused("--application", "a", "--stage", "--stage"));
        Assert.Equal("Option '--profile' needs a value.", Refused("--application", "a", "--stage", "--profile"));
        Assert.Equal("Option '--profile' needs a value.", Refused("--application", "a", "--profile", "--stage"));
        Assert.Equal("Unexpected argument 'putty'. Run AppPackagerBuild --help for usage.", Refused("putty", "--stage"));
        Assert.Equal("Option '--stage' takes no value, or =true or =false.", Refused("--application", "a", "--stage=maybe"));
    }

    [Fact]
    public void EveryOptionMapsToItsField()
    {
        var o = Parsed("--application", "catalog:7zip", "--profile=Managed", "--version", "1.2", "--target", "MECMAndIntune",
            "--stage", "--package", "--download-root", @"D:\ap", "--estimated-minutes", "10", "--maximum-minutes", "25",
            "--packagers-root", @"D:\pk", "--log-folder", @"D:\logs", "--site-code", "LAB", "--provider-machine-name", "cm01.lab",
            "--file-server-path", @"\\fs\sccm$", "--comment", "CHG0001", "--wsus-server", "wsus01.lab", "--wsus-port", "8531",
            "--wsus-use-ssl", "--wsus-classification", "SecurityUpdates", "--wsus-approval-group", "Pilot",
            "--wsus-decline-superseded", "--on-existing", "overwrite", "--local-source-folder", @"D:\src", "--data-root", @"D:\data");

        Assert.Equal("catalog:7zip", o.Application);
        Assert.Equal("Managed", o.Profile);
        Assert.Equal("1.2", o.Version);
        Assert.Equal(RunTargets.MecmAndIntune, o.Target);
        Assert.True(o.Stage);
        Assert.True(o.Package);
        Assert.Equal(@"D:\ap", o.DownloadRoot);
        Assert.Equal(10, o.EstimatedMinutes);
        Assert.Equal(25, o.MaximumMinutes);
        Assert.Equal(@"D:\pk", o.PackagersRoot);
        Assert.Equal(@"D:\logs", o.LogFolder);
        Assert.Equal("LAB", o.SiteCode);
        Assert.Equal("cm01.lab", o.ProviderMachineName);
        Assert.Equal(@"\\fs\sccm$", o.FileServerPath);
        Assert.Equal("CHG0001", o.Comment);
        Assert.Equal("wsus01.lab", o.WsusServer);
        Assert.Equal(8531, o.WsusPort);
        Assert.True(o.WsusUseSsl);
        Assert.Equal("SecurityUpdates", o.WsusClassification);
        Assert.Equal("Pilot", o.WsusApprovalGroup);
        Assert.True(o.WsusDeclineSuperseded);
        Assert.Equal(OnExistingBehavior.Overwrite, o.OnExisting);
        Assert.Equal(@"D:\src", o.LocalSourceFolder);
        Assert.Equal(@"D:\data", o.DataRoot);
    }

    [Fact]
    public void OmittedOptionsInheritAndAWsusSwitchCanTurnAStoredChoiceOff()
    {
        var o = Parsed("--application", "a", "--stage");
        Assert.Equal(ApplicationIds.DefaultProfileId, o.Profile);
        Assert.Null(o.WsusUseSsl);
        Assert.Null(o.WsusDeclineSuperseded);
        Assert.Null(o.WsusPort);
        Assert.Null(o.SiteCode);
        Assert.Equal("", o.Comment);
        Assert.Equal(OnExistingBehavior.Skip, o.OnExisting);

        var off = Parsed("--application", "a", "--stage", "--wsus-use-ssl=false", "--wsus-decline-superseded=FALSE");
        Assert.False(off.WsusUseSsl);
        Assert.False(off.WsusDeclineSuperseded);
    }

    [Fact]
    public void AnEmptyCommentIsAValue() =>
        Assert.Equal("", Parsed("--application", "a", "--package", "--comment", "").Comment);

    [Theory]
    [InlineData("0")]
    [InlineData("65536")]
    [InlineData("85x")]
    [InlineData("-1")]
    public void TheWsusPortIsAPortNumber(string port) =>
        Assert.Equal("Option '--wsus-port' takes a port number from 1 to 65535.", Refused("--application", "a", "--stage", "--wsus-port", port));

    [Fact]
    public void MinutesAreWholeNumbers() =>
        Assert.Equal("Option '--estimated-minutes' takes a whole number of minutes.", Refused("--application", "a", "--stage", "--estimated-minutes", "1.5"));

    [Theory]
    [InlineData("Skip", OnExistingBehavior.Skip)]
    [InlineData("OVERWRITE", OnExistingBehavior.Overwrite)]
    [InlineData("fail", OnExistingBehavior.Fail)]
    public void OnExistingTakesTheEngineBehaviors(string value, OnExistingBehavior expected) =>
        Assert.Equal(expected, Parsed("--application", "a", "--package", "--on-existing", value).OnExisting);

    [Theory]
    [InlineData("Ask")]
    [InlineData("1")]
    [InlineData("Skip,Overwrite")]
    public void OnExistingRefusesAnythingElse(string value) =>
        Assert.Equal($"Unknown --on-existing value '{value}'; expected Skip, Overwrite or Fail.",
            Refused("--application", "a", "--package", "--on-existing", value));

    [Theory]
    [InlineData("data-root")]
    [InlineData("download-root")]
    [InlineData("packagers-root")]
    [InlineData("log-folder")]
    [InlineData("local-source-folder")]
    [InlineData("file-server-path")]
    [InlineData("site-code")]
    [InlineData("provider-machine-name")]
    [InlineData("wsus-server")]
    public void AnEmptyPathRootServerOrSiteIsRefused(string option)
    {
        Assert.Equal($"Option '--{option}' was given an empty value; pass a value or leave the option out.",
            Refused("--application", "a", "--stage", "--" + option, ""));
        Assert.Equal($"Option '--{option}' was given an empty value; pass a value or leave the option out.",
            Refused("--application", "a", "--stage", $"--{option}=  "));
    }
}

[Collection(ProcessEnvironmentCollection.Name)]
public sealed class BuildProgramTests
{
    [Fact]
    public async Task AnInvalidDataRootEndsWithAnErrorAndExitCodeOne()
    {
        var before = Environment.GetEnvironmentVariable(AppDataPaths.DataRootVariable);

        var exitCode = await Program.Main(["--application", "a", "--stage", "--data-root", "C:\\bad\0root"]);

        Assert.Equal(1, exitCode);
        Assert.Equal(before, Environment.GetEnvironmentVariable(AppDataPaths.DataRootVariable));
    }
}
