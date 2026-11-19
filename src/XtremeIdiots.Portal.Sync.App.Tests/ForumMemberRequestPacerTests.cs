using System.Net;

using RestSharp;

using XtremeIdiots.Portal.Sync.App;

namespace XtremeIdiots.Portal.Sync.App.Tests;

public class ForumMemberRequestPacerTests
{
    [Fact]
    public async Task ExecuteAsync_PacesConcurrentRequestsAtNinetyPerMinute()
    {
        var clock = new TestClock();
        var pacer = clock.CreatePacer();
        var response = new RestResponse { StatusCode = HttpStatusCode.OK };
        var firstRequestStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstRequest = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var firstRequest = pacer.ExecuteAsync(async _ =>
        {
            firstRequestStarted.SetResult();
            await releaseFirstRequest.Task;
            return response;
        }, CancellationToken.None);
        await firstRequestStarted.Task;
        var secondRequest = pacer.ExecuteAsync(_ => Task.FromResult(response), CancellationToken.None);
        releaseFirstRequest.SetResult();
        await Task.WhenAll(firstRequest, secondRequest);

        Assert.Equal(TimeSpan.FromSeconds(60d / 90), clock.Elapsed);
    }

    [Fact]
    public async Task ExecuteAsync_RateLimitDefersUsingRetryAfterHeader()
    {
        var clock = new TestClock();
        var pacer = clock.CreatePacer();
        var response = new RestResponse
        {
            StatusCode = HttpStatusCode.TooManyRequests,
            Headers = [new HeaderParameter("Retry-After", "12", false)]
        };

        var exception = await Assert.ThrowsAsync<ForumMemberRequestDeferredException>(
            () => pacer.ExecuteAsync(_ => Task.FromResult(response), CancellationToken.None));

        Assert.Equal(clock.Now.AddSeconds(12), exception.DeferredUntilUtc);
    }

    [Fact]
    public async Task ExecuteAsync_RateLimitDefersUsingRetryAfterDate()
    {
        var clock = new TestClock();
        var pacer = clock.CreatePacer();
        var retryAt = clock.Now.AddSeconds(12);
        var response = new RestResponse
        {
            StatusCode = HttpStatusCode.TooManyRequests,
            Headers = [new HeaderParameter("Retry-After", retryAt.ToString("R"), false)]
        };

        var exception = await Assert.ThrowsAsync<ForumMemberRequestDeferredException>(
            () => pacer.ExecuteAsync(_ => Task.FromResult(response), CancellationToken.None));

        Assert.Equal(retryAt, exception.DeferredUntilUtc);
    }

    [Fact]
    public async Task ExecuteAsync_RateLimitWithoutRetryAfterUsesBoundedJitteredBackoff()
    {
        var clock = new TestClock();
        var pacer = clock.CreatePacer(randomValue: () => 0.5);
        var response = new RestResponse { StatusCode = HttpStatusCode.TooManyRequests };

        var exception = await Assert.ThrowsAsync<ForumMemberRequestDeferredException>(
            () => pacer.ExecuteAsync(_ => Task.FromResult(response), CancellationToken.None));

        Assert.Equal(clock.Now.AddSeconds(1.5), exception.DeferredUntilUtc);
    }

    [Fact]
    public async Task ExecuteAsync_RepeatedRateLimitsCapFallbackDelayAtThirtySeconds()
    {
        var clock = new TestClock();
        var pacer = clock.CreatePacer(randomValue: () => 1);
        var response = new RestResponse { StatusCode = HttpStatusCode.TooManyRequests };
        ForumMemberRequestDeferredException? lastException = null;

        for (var attempt = 0; attempt < 6; attempt++)
        {
            lastException = await Assert.ThrowsAsync<ForumMemberRequestDeferredException>(
                () => pacer.ExecuteAsync(_ => Task.FromResult(response), CancellationToken.None));
        }

        Assert.NotNull(lastException);
        Assert.Equal(clock.Now.AddSeconds(30), lastException.DeferredUntilUtc);
    }

    private sealed class TestClock
    {
        public DateTimeOffset Now { get; private set; } = new(2026, 11, 19, 20, 0, 0, TimeSpan.Zero);
        public TimeSpan Elapsed { get; private set; }

        public ForumMemberRequestPacer CreatePacer(Func<double>? randomValue = null) =>
            new(
                () => Now,
                () => Now.UtcDateTime.Ticks,
                TimeSpan.TicksPerSecond,
                (delay, cancellationToken) =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Now += delay;
                    Elapsed += delay;
                    return Task.CompletedTask;
                },
                randomValue ?? (() => 0.5));
    }
}
