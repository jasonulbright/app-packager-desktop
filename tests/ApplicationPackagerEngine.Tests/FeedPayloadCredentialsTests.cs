using ApplicationPackagerEngine.Scrapers;
using ApplicationPackagerEngine.Services.VendorImport;

namespace ApplicationPackagerEngine.Tests;

/// <summary>The credential of an authenticated feed on the curl path: no -L, the header on standard input, the redirects followed by the program.</summary>
public sealed class FeedPayloadCredentialsTests
{
    private const string Secret = "PAYLOAD-SECRET-7f3a9c-VALUE";

    private static FeedCredential Key(string host = "cdn.vendor.example", FeedAuthMethod method = FeedAuthMethod.HeaderKey, string? header = null)
    {
        var entry = new FeedCredentialEntry("feed-a", host, 443, method, header, method == FeedAuthMethod.Basic ? "alice" : null);
        return new FeedCredential(entry, Secret);
    }

    [Fact]
    public void TheHeaderGoesIntoTheConfigForStandardInputAndNeverIntoTheArguments()
    {
        var config = FeedPayloadCredentials.ConfigFor(Key(header: "X-License-Key"));
        var arguments = CurlProcess.BuildStartInfo(["--silent", "--max-redirs", "0"], "https://cdn.vendor.example/agent.msi", readConfigFromStdin: true).ArgumentList.ToList();

        Assert.Equal("header = \"X-License-Key: " + Secret + "\"\n", config);
        Assert.DoesNotContain(arguments, a => a.Contains(Secret));
        Assert.DoesNotContain("-L", arguments);
        Assert.Contains("-K", arguments);
    }

    [Fact]
    public void ABasicCredentialIsEncodedAndAValueWithAQuoteOrALineBreakIsRefused()
    {
        Assert.Contains("Authorization: Basic ", FeedPayloadCredentials.ConfigFor(Key(method: FeedAuthMethod.Basic)));
        var bad = new FeedCredential(Key().Entry, "value\"; header = \"X-Other: 1");

        Assert.Throws<InvalidOperationException>(() => FeedPayloadCredentials.ConfigFor(bad));
        Assert.Throws<InvalidOperationException>(() => FeedPayloadCredentials.ConfigFor(new FeedCredential(Key().Entry, "line\nbreak-value")));
    }

    [Fact]
    public void ARedirectInsideTheBoundHostKeepsTheCredentialAndARedirectToAnotherHostDropsIt()
    {
        var start = new Uri("https://cdn.vendor.example/a");
        var credential = Key();

        var inside = FeedPayloadCredentials.Next(start, 302, "/b", credential, 0);
        var outside = FeedPayloadCredentials.Next(start, 302, "https://files.example/c", credential, 0);

        Assert.Equal(new Uri("https://cdn.vendor.example/b"), inside.Next);
        Assert.True(inside.SendCredential);
        Assert.False(outside.SendCredential);
        Assert.Equal(new Uri("https://files.example/c"), outside.Next);
    }

    [Fact]
    public void ADowngradeTooManyRedirectsAndARefusedKeyAreRefused()
    {
        var start = new Uri("https://cdn.vendor.example/a");
        var credential = Key();

        Assert.NotNull(FeedPayloadCredentials.Next(start, 302, "http://cdn.vendor.example/a", credential, 0).Error);
        Assert.NotNull(FeedPayloadCredentials.Next(start, 302, "/b", credential, 5).Error);
        Assert.Contains("refused the credential", FeedPayloadCredentials.Next(start, 401, null, credential, 0).Error);
        Assert.True(FeedPayloadCredentials.Next(start, 200, null, credential, 0).Done);
    }


    [Fact]
    public void ATransferThatBrokeAfterTheAnswerIsNotDoneAnEmptyRedirectIsAnErrorAndStatusZeroGivesNoAnswer()
    {
        var start = new Uri("https://cdn.vendor.example/a");
        var credential = Key();

        Assert.False(FeedPayloadCredentials.Next(start, 200, null, credential, 0, exitCode: 18).Done);
        Assert.Contains("not complete", FeedPayloadCredentials.Next(start, 200, null, credential, 0, exitCode: 18).Error);
        Assert.Contains("no address", FeedPayloadCredentials.Next(start, 302, "", credential, 0).Error);
        Assert.Contains("no answer", FeedPayloadCredentials.Next(start, 0, null, credential, 0, exitCode: 6).Error);
    }

    [Fact]
    public void ARequestOfAnImportedDefinitionToACredentialedHostRunsWithoutLocationAndWithTheSecretOnStandardInput()
    {
        var previous = FeedPayloadCredentials.Provider;
        try
        {
            var entry = new FeedCredentialEntry("feed-a", "127.0.0.1", 1, FeedAuthMethod.HeaderKey, "X-License-Key", null);
            FeedPayloadCredentials.Provider = _ => new FeedCredential(entry, Secret);
            using var scope = ImportedRunScope.Enter();

            using var process = CurlProcess.Start(["-L", "--silent", "--max-time", "5"], "https://127.0.0.1:1/page");

            var arguments = process.StartInfo.ArgumentList.ToList();
            Assert.DoesNotContain("-L", arguments);
            Assert.Contains("-K", arguments);
            Assert.DoesNotContain(arguments, a => a.Contains(Secret));
            try { process.Kill(true); } catch (InvalidOperationException) { }
        }
        finally { FeedPayloadCredentials.Provider = previous; }
    }
    [Fact]
    public void OnlyAKeyOrBasicEntryOnTheExactBoundHostIsUsedForAPayload()
    {
        var previous = FeedPayloadCredentials.Provider;
        try
        {
            FeedPayloadCredentials.Provider = _ => Key(method: FeedAuthMethod.OAuth);
            Assert.Null(FeedPayloadCredentials.For(new Uri("https://cdn.vendor.example/a")));
            FeedPayloadCredentials.Provider = _ => Key();
            Assert.NotNull(FeedPayloadCredentials.For(new Uri("https://cdn.vendor.example/a")));
            Assert.Null(FeedPayloadCredentials.For(new Uri("https://evil.example/a")));
            FeedPayloadCredentials.Provider = null;
            Assert.Null(FeedPayloadCredentials.For(new Uri("https://cdn.vendor.example/a")));
        }
        finally { FeedPayloadCredentials.Provider = previous; }
    }
}
