using System.Net;
using System.Net.Http.Headers;
using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests;

public class IntuneRetryTests
{
    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, true, true)]
    [InlineData(HttpStatusCode.TooManyRequests, false, true)]
    [InlineData(HttpStatusCode.ServiceUnavailable, true, true)]
    [InlineData(HttpStatusCode.ServiceUnavailable, false, true)]
    [InlineData(HttpStatusCode.BadGateway, true, true)]
    [InlineData(HttpStatusCode.BadGateway, false, false)]
    [InlineData(HttpStatusCode.GatewayTimeout, true, true)]
    [InlineData(HttpStatusCode.GatewayTimeout, false, false)]
    [InlineData(HttpStatusCode.InternalServerError, true, true)]
    [InlineData(HttpStatusCode.InternalServerError, false, false)]
    [InlineData(HttpStatusCode.RequestTimeout, true, true)]
    [InlineData(HttpStatusCode.RequestTimeout, false, false)]
    [InlineData(HttpStatusCode.BadRequest, true, false)]
    [InlineData(HttpStatusCode.Unauthorized, true, false)]
    [InlineData(HttpStatusCode.Forbidden, true, false)]
    [InlineData(HttpStatusCode.NotFound, true, false)]
    [InlineData(HttpStatusCode.Conflict, true, false)]
    [InlineData(HttpStatusCode.OK, true, false)]
    public void ARefusalForLoadIsRepeatedForEveryRequestAndOtherTransientStatusesOnlyForRepeatableOnes(HttpStatusCode status, bool repeatable, bool expected)
    {
        Assert.Equal(expected, IntuneRetry.IsTransient(status, repeatable));
    }

    [Fact]
    public void ALostConnectionIsRepeatedOnlyForARepeatableRequestAndNeverAfterACancel()
    {
        var lost = new HttpRequestException("connection reset");
        Assert.True(IntuneRetry.IsTransient(lost, repeatable: true, cancellationRequested: false));
        Assert.False(IntuneRetry.IsTransient(lost, repeatable: false, cancellationRequested: false));
        Assert.False(IntuneRetry.IsTransient(lost, repeatable: true, cancellationRequested: true));
        Assert.True(IntuneRetry.IsTransient(new TaskCanceledException(), repeatable: true, cancellationRequested: false));
        Assert.False(IntuneRetry.IsTransient(new TaskCanceledException(), repeatable: true, cancellationRequested: true));
        Assert.True(IntuneRetry.IsTransient(new IOException(), repeatable: true, cancellationRequested: false));
        Assert.True(IntuneRetry.IsTransient(new TimeoutException(), repeatable: true, cancellationRequested: false));
    }

    [Fact]
    public void AnAnswerWithAStatusIsNotALostConnection()
    {
        var answered = new HttpRequestException("400", null, HttpStatusCode.BadRequest);

        Assert.False(IntuneRetry.IsTransient(answered, repeatable: true, cancellationRequested: false));
        Assert.False(IntuneRetry.IsTransient(new InvalidOperationException("x"), repeatable: true, cancellationRequested: false));
    }

    [Theory]
    [InlineData(0, 2)]
    [InlineData(1, 4)]
    [InlineData(2, 8)]
    [InlineData(3, 16)]
    [InlineData(4, 32)]
    public void TheWaitDoublesWithEachRetry(int attempt, double seconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(seconds), IntuneRetry.Delay(attempt, null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(60), () => 0));
    }

    [Fact]
    public void TheRandomPartAddsAtMostAQuarter()
    {
        Assert.Equal(TimeSpan.FromSeconds(2.5), IntuneRetry.Delay(0, null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(60), () => 1));
        Assert.Equal(TimeSpan.FromSeconds(2.25), IntuneRetry.Delay(0, null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(60), () => 0.5));
    }

    [Fact]
    public void TheWaitNeverPassesTheMaximumAndAHugeAttemptDoesNotOverflow()
    {
        Assert.Equal(TimeSpan.FromSeconds(60), IntuneRetry.Delay(10, null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(60), () => 1));
        Assert.Equal(TimeSpan.FromSeconds(60), IntuneRetry.Delay(5000, null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(60), () => 1));
    }

    [Fact]
    public void RetryAfterReplacesTheComputedWaitAndIsCapped()
    {
        Assert.Equal(TimeSpan.FromSeconds(5), IntuneRetry.Delay(3, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(60)));
        Assert.Equal(TimeSpan.FromSeconds(60), IntuneRetry.Delay(0, TimeSpan.FromSeconds(500), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(60)));
        Assert.Equal(TimeSpan.Zero, IntuneRetry.Delay(3, TimeSpan.Zero, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(60)));
    }

    [Fact]
    public void ABaseDelayOfZeroMeansNoWait()
    {
        Assert.Equal(TimeSpan.Zero, IntuneRetry.Delay(4, null, TimeSpan.Zero, TimeSpan.FromSeconds(60)));
    }

    [Fact]
    public void RetryAfterIsReadAsSecondsOrAsADate()
    {
        var now = new DateTimeOffset(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);
        using var seconds = new HttpResponseMessage();
        seconds.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(7));
        using var future = new HttpResponseMessage();
        future.Headers.RetryAfter = new RetryConditionHeaderValue(now.AddSeconds(30));
        using var past = new HttpResponseMessage();
        past.Headers.RetryAfter = new RetryConditionHeaderValue(now.AddSeconds(-30));
        using var none = new HttpResponseMessage();

        Assert.Equal(TimeSpan.FromSeconds(7), IntuneRetry.RetryAfter(seconds.Headers, now));
        Assert.Equal(TimeSpan.FromSeconds(30), IntuneRetry.RetryAfter(future.Headers, now));
        Assert.Equal(TimeSpan.Zero, IntuneRetry.RetryAfter(past.Headers, now));
        Assert.Null(IntuneRetry.RetryAfter(none.Headers, now));
    }

    [Fact]
    public void TheDefaultsRetryFiveTimesAndRenewTheUploadAddressThreeTimes()
    {
        var options = new IntunePublishOptions();

        Assert.Equal(5, options.MaxRetries);
        Assert.Equal(TimeSpan.FromSeconds(2), options.RetryBaseDelay);
        Assert.Equal(TimeSpan.FromSeconds(60), options.RetryMaxDelay);
        Assert.Equal(3, options.MaxStorageUriRenewals);
    }
}