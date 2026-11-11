using System.Net;
using System.Net.Http.Headers;

namespace ApplicationPackagerEngine.Services;

/// <summary>
/// When and how long the Intune publish waits before it sends a request again. A request that
/// the service refused for load (429, 503) did not run, so any request may be repeated. A request
/// that failed in some other transient way (408, 500, 502, 504, a lost connection) may have run, so
/// only a request that can be repeated without harm (a read, a blob block, an update with the same
/// body) is sent again. A create is never sent again after such a failure: it could make a second app.
/// </summary>
public static class IntuneRetry
{
    /// <summary>True when the answer says "not now" and the request did not run.</summary>
    public static bool RefusedForLoad(HttpStatusCode status) => status is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable;

    /// <summary>
    /// True when the status is worth another try for this kind of request. A request that creates the
    /// app is repeated only after 429 (<paramref name="throttleOnly"/>): a front end can answer 503 after the
    /// back end has run the request, and a second app with the same identity would stop every later publish.
    /// </summary>
    public static bool IsTransient(HttpStatusCode status, bool repeatable, bool throttleOnly = false) =>
        throttleOnly ? status == HttpStatusCode.TooManyRequests
        : RefusedForLoad(status)
        || (repeatable && status is HttpStatusCode.RequestTimeout or HttpStatusCode.InternalServerError or HttpStatusCode.BadGateway or HttpStatusCode.GatewayTimeout);

    /// <summary>True for a failure with no answer (a lost connection, a timeout of the call) of a request that can be repeated.</summary>
    public static bool IsTransient(Exception exception, bool repeatable, bool cancellationRequested) =>
        repeatable && !cancellationRequested
        && (exception is HttpRequestException { StatusCode: null } or TimeoutException or IOException
            || exception is TaskCanceledException);

    /// <summary>
    /// The wait before attempt number <paramref name="attempt"/> + 1 (0 for the first retry): the value of
    /// Retry-After when the service sent one, otherwise the base delay doubled for each earlier retry with
    /// up to a quarter added at random, never longer than <paramref name="max"/>. A Retry-After is honored up to <see cref="RetryAfterCap"/>.
    /// </summary>
    public static TimeSpan Delay(int attempt, TimeSpan? retryAfter, TimeSpan baseDelay, TimeSpan max, Func<double>? random = null)
    {
        var computed = TimeSpan.Zero;
        if (baseDelay > TimeSpan.Zero)
        {
            var factor = Math.Pow(2, Math.Min(attempt, 20));
            var jitter = 1 + 0.25 * (random?.Invoke() ?? Random.Shared.NextDouble());
            computed = TimeSpan.FromTicks((long)Math.Min(baseDelay.Ticks * factor * jitter, TimeSpan.MaxValue.Ticks / 2));
            if (computed > max) computed = max;
        }
        // The service knows how long it needs. What it asks for is honored up to RetryAfterCap, even
        // beyond the maximum of the computed wait, because a shorter wait meets the same throttle again.
        // A Retry-After of 0 means no wait.
        if (retryAfter is { } asked && asked >= TimeSpan.Zero)
            return asked > RetryAfterCap ? RetryAfterCap : asked;
        return computed;
    }

    /// <summary>The longest wait that a Retry-After header can ask for.</summary>
    public static readonly TimeSpan RetryAfterCap = TimeSpan.FromMinutes(5);

    /// <summary>The Retry-After header as a wait: seconds, or a date; null when absent or not valid.</summary>
    public static TimeSpan? RetryAfter(HttpResponseHeaders headers, DateTimeOffset now)
    {
        var value = headers.RetryAfter;
        if (value is null) return null;
        if (value.Delta is { } delta) return delta < TimeSpan.Zero ? TimeSpan.Zero : delta;
        if (value.Date is { } date) return date > now ? date - now : TimeSpan.Zero;
        return null;
    }
}
