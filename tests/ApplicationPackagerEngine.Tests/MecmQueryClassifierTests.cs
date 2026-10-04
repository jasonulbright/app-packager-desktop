using ApplicationPackagerEngine.Models;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// A site that cannot be reached must stay distinct from one that answers that it does
/// not carry the application: an absence clears the recorded version and counts as a
/// completed check. Errors written as ordinary output, or a nonzero exit code, are
/// failures, not absences.
///
/// This drives the classifier the query service actually calls; no site is contacted.
/// </summary>
public class MecmQueryClassifierTests
{
    [Fact]
    public void AFoundApplicationCarriesItsVersionAndMatchCount()
    {
        var result = MecmQueryClassifier.Classify(0, "FOUND|Contoso App|1.2.3|4", "");

        Assert.Equal(MecmQueryStatus.Found, result.Status);
        Assert.True(result.Found);
        Assert.False(result.Failed);
        Assert.Equal("Contoso App", result.DisplayName);
        Assert.Equal("1.2.3", result.SoftwareVersion);
        Assert.Equal(4, result.MatchCount);
    }

    /// <summary>An application the site carries with no version recorded is a real answer.</summary>
    [Fact]
    public void AFoundApplicationMayCarryAnEmptyVersionField()
    {
        var result = MecmQueryClassifier.Classify(0, "FOUND|Contoso App||2", "");

        Assert.Equal(MecmQueryStatus.Found, result.Status);
        Assert.Equal("Contoso App", result.DisplayName);
        Assert.Equal("", result.SoftwareVersion);
        Assert.Equal(2, result.MatchCount);
    }

    /// <summary>
    /// The script writes one complete record. A short one is malformed, not a smaller
    /// answer, and there is no contract under which a missing count means one match.
    /// </summary>
    [Theory]
    [InlineData("FOUND|")]
    [InlineData("FOUND|Contoso App")]
    [InlineData("FOUND|Contoso App|1.2.3")]
    [InlineData("FOUND|Contoso App|1.2.3|1|extra")]
    public void AFoundRecordOfTheWrongShapeIsAFailure(string stdout)
    {
        var result = MecmQueryClassifier.Classify(0, stdout, "");

        Assert.Equal(MecmQueryStatus.Failed, result.Status);
        Assert.Contains("malformed FOUND record", result.FailureReason);
    }

    [Theory]
    [InlineData("FOUND||1.2.3|1")]
    [InlineData("FOUND|   |1.2.3|1")]
    public void AFoundRecordWithNoApplicationNameIsAFailure(string stdout)
    {
        var result = MecmQueryClassifier.Classify(0, stdout, "");

        Assert.Equal(MecmQueryStatus.Failed, result.Status);
        Assert.Contains("no application name", result.FailureReason);
    }

    [Theory]
    [InlineData("FOUND|Contoso App|1.2.3|not-a-count")]
    [InlineData("FOUND|Contoso App|1.2.3|0")]
    [InlineData("FOUND|Contoso App|1.2.3|-4")]
    [InlineData("FOUND|Contoso App|1.2.3|")]
    public void AFoundRecordWithoutAPositiveMatchCountIsAFailure(string stdout)
    {
        var result = MecmQueryClassifier.Classify(0, stdout, "");

        Assert.Equal(MecmQueryStatus.Failed, result.Status);
        Assert.Contains("no usable match count", result.FailureReason);
    }

    /// <summary>A line that merely begins with the sentinel is not the sentinel.</summary>
    [Theory]
    [InlineData("NOT_FOUND_BUT_ERROR")]
    [InlineData("NOT_FOUNDish")]
    [InlineData("NOT_FOUND extra")]
    public void ALineThatOnlyStartsWithNotFoundIsAFailure(string stdout) =>
        Assert.Equal(MecmQueryStatus.Failed, MecmQueryClassifier.Classify(0, stdout, "").Status);

    /// <summary>A second, contradicting record means the run did more than answer.</summary>
    [Theory]
    [InlineData("NOT_FOUND\nERROR:QUERY_FAILED")]
    [InlineData("FOUND|Contoso App|1.2.3|1\nERROR:QUERY_FAILED")]
    [InlineData("FOUND|Contoso App|1.2.3|1\nFOUND|Other|2.0|1")]
    [InlineData("NOT_FOUND\r\nNOT_FOUND")]
    public void TrailingRecordsMakeTheAnswerUnusable(string stdout)
    {
        var result = MecmQueryClassifier.Classify(0, stdout, "");

        Assert.Equal(MecmQueryStatus.Failed, result.Status);
        Assert.Contains("records", result.FailureReason);
    }

    /// <summary>An uninitialized result must not pass for an answer.</summary>
    [Fact]
    public void TheDefaultResultIsAFailure()
    {
        var blank = new MecmQueryResult();

        Assert.Equal(MecmQueryStatus.Failed, blank.Status);
        Assert.True(blank.Failed);
        Assert.False(blank.Found);
    }

    /// <summary>The site answered and does not carry it. This is the only established absence.</summary>
    [Fact]
    public void NotFoundIsACompletedCheck()
    {
        var result = MecmQueryClassifier.Classify(0, "NOT_FOUND", "");

        Assert.Equal(MecmQueryStatus.NotFound, result.Status);
        Assert.False(result.Found);
        Assert.False(result.Failed);
        Assert.Null(result.FailureReason);
    }

    [Fact]
    public void TrailingWhitespaceDoesNotChangeTheReading() =>
        Assert.Equal(MecmQueryStatus.NotFound,
            MecmQueryClassifier.Classify(0, "  NOT_FOUND\r\n", "").Status);

    [Theory]
    [InlineData("ERROR:CM_CMDLETS_NOT_AVAILABLE", 3)]
    [InlineData("ERROR:SITE_DRIVE_FAILED", 4)]
    [InlineData("ERROR:QUERY_FAILED", 5)]
    public void AnErrorSentinelIsAFailureNotAnAbsence(string sentinel, int exitCode)
    {
        var result = MecmQueryClassifier.Classify(exitCode, sentinel, "");

        Assert.Equal(MecmQueryStatus.Failed, result.Status);
        Assert.True(result.Failed);
        Assert.False(result.Found);
        Assert.Equal(sentinel, result.FailureReason);
    }

    /// <summary>An error sentinel stays a failure even when the script exited zero.</summary>
    [Fact]
    public void AnErrorSentinelWithAZeroExitIsStillAFailure() =>
        Assert.Equal(MecmQueryStatus.Failed,
            MecmQueryClassifier.Classify(0, "ERROR:SITE_DRIVE_FAILED", "").Status);

    [Fact]
    public void NoOutputAtAllIsAFailure()
    {
        var result = MecmQueryClassifier.Classify(0, "", "");

        Assert.Equal(MecmQueryStatus.Failed, result.Status);
        Assert.Contains("no output", result.FailureReason);
    }

    [Fact]
    public void NullOutputIsAFailure() =>
        Assert.Equal(MecmQueryStatus.Failed, MecmQueryClassifier.Classify(0, null, null).Status);

    /// <summary>A crashed host writes to stderr and produces nothing the script defined.</summary>
    [Fact]
    public void UnrecognisedOutputIsAFailureThatQuotesWhatCameBack()
    {
        var result = MecmQueryClassifier.Classify(
            1, "", "The term 'Get-CMApplication' is not recognized.");

        Assert.Equal(MecmQueryStatus.Failed, result.Status);
        Assert.Contains("Get-CMApplication", result.FailureReason);
        Assert.Contains("exit 1", result.FailureReason);
    }

    [Fact]
    public void GarbageOnStdoutIsAFailure()
    {
        var result = MecmQueryClassifier.Classify(0, "At line:1 char:1 ...", "");

        Assert.Equal(MecmQueryStatus.Failed, result.Status);
        Assert.Contains("unexpected query result", result.FailureReason);
    }

    /// <summary>
    /// A run that printed a result and then died is not a result. Ignoring the exit code
    /// lets a partial run pass for an answer.
    /// </summary>
    [Fact]
    public void ANonzeroExitBesideAFoundLineIsAFailure()
    {
        var result = MecmQueryClassifier.Classify(1, "FOUND|Contoso App|1.2.3|1", "");

        Assert.Equal(MecmQueryStatus.Failed, result.Status);
        Assert.Contains("exited 1", result.FailureReason);
    }

    [Fact]
    public void ANonzeroExitBesideNotFoundIsAFailure()
    {
        var result = MecmQueryClassifier.Classify(255, "NOT_FOUND", "");

        Assert.Equal(MecmQueryStatus.Failed, result.Status);
        Assert.Contains("exited 255", result.FailureReason);
    }

    /// <summary>
    /// Only a completed check may clear a recorded version. The loop reads Failed to decide
    /// whether the last known version survives.
    /// </summary>
    [Theory]
    [InlineData("NOT_FOUND", 0, false)]
    [InlineData("FOUND|App|1.0|1", 0, false)]
    [InlineData("ERROR:SITE_DRIVE_FAILED", 4, true)]
    [InlineData("", 0, true)]
    [InlineData("nonsense", 0, true)]
    public void OnlyAnAnsweredQueryMayClearARecordedVersion(string stdout, int exitCode, bool failed) =>
        Assert.Equal(failed, MecmQueryClassifier.Classify(exitCode, stdout, "").Failed);
}
