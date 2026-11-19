using System.Net;
using System.Net.Http.Headers;
using System.Diagnostics;

using RestSharp;

namespace XtremeIdiots.Portal.Sync.App;

internal sealed class ForumMemberRequestDeferredException(DateTimeOffset deferredUntilUtc)
    : OperationCanceledException($"Forum member synchronization was deferred until {deferredUntilUtc:O}.")
{
    public DateTimeOffset DeferredUntilUtc { get; } = deferredUntilUtc;
}

internal sealed class ForumMemberRequestPacer
{
    private static readonly TimeSpan RequestInterval = TimeSpan.FromSeconds(60d / 90);
    private static readonly TimeSpan MaximumFallbackDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MaximumDelayChunk = TimeSpan.FromDays(1);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Func<DateTimeOffset> _getUtcNow;
    private readonly Func<long> _getTimestamp;
    private readonly long _timestampFrequency;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Func<double> _getRandomValue;

    private long _nextRequestTimestamp;
    private int _consecutiveRateLimits;

    public ForumMemberRequestPacer()
        : this(
            () => DateTimeOffset.UtcNow,
            Stopwatch.GetTimestamp,
            Stopwatch.Frequency,
            (delay, cancellationToken) => Task.Delay(delay, cancellationToken),
            Random.Shared.NextDouble)
    {
    }

    internal ForumMemberRequestPacer(
        Func<DateTimeOffset> getUtcNow,
        Func<long> getTimestamp,
        long timestampFrequency,
        Func<TimeSpan, CancellationToken, Task> delay,
        Func<double> getRandomValue)
    {
        _getUtcNow = getUtcNow;
        _getTimestamp = getTimestamp;
        _timestampFrequency = timestampFrequency;
        _delay = delay;
        _getRandomValue = getRandomValue;
    }

    public async Task<RestResponse> ExecuteAsync(
        Func<CancellationToken, Task<RestResponse>> execute,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            while (true)
            {
                var waitTimestamp = _nextRequestTimestamp - _getTimestamp();
                if (waitTimestamp <= 0)
                {
                    break;
                }

                var wait = TimeSpan.FromSeconds((double)waitTimestamp / _timestampFrequency);
                await _delay(wait > MaximumDelayChunk ? MaximumDelayChunk : wait, cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            _nextRequestTimestamp = _getTimestamp() + ToTimestampTicks(RequestInterval);

            var response = await execute(cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                var nowUtc = _getUtcNow();
                var retryAfter = GetRetryAfter(response, nowUtc);
                var deferFor = retryAfter ?? GetFallbackDelay();
                _nextRequestTimestamp = Max(_nextRequestTimestamp, _getTimestamp() + ToTimestampTicks(deferFor));
                var remainingTicks = _nextRequestTimestamp - _getTimestamp();
                var deferredUntilUtc = _getUtcNow() + TimeSpan.FromSeconds((double)remainingTicks / _timestampFrequency);

                throw new ForumMemberRequestDeferredException(deferredUntilUtc);
            }

            _consecutiveRateLimits = 0;
            return response;
        }
        finally
        {
            _gate.Release();
        }
    }

    private TimeSpan GetFallbackDelay()
    {
        _consecutiveRateLimits = Math.Min(_consecutiveRateLimits + 1, 5);
        var maximumSeconds = Math.Min(Math.Pow(2, _consecutiveRateLimits), MaximumFallbackDelay.TotalSeconds);
        var minimumSeconds = maximumSeconds / 2;
        var jitteredSeconds = minimumSeconds + (_getRandomValue() * (maximumSeconds - minimumSeconds));

        return TimeSpan.FromSeconds(jitteredSeconds);
    }

    private static TimeSpan? GetRetryAfter(RestResponse response, DateTimeOffset now)
    {
        var retryAfterHeader = response.Headers?
            .FirstOrDefault(header => string.Equals(header.Name, "Retry-After", StringComparison.OrdinalIgnoreCase));
        var value = retryAfterHeader?.Value?.ToString();

        if (!RetryConditionHeaderValue.TryParse(value, out var retryCondition) || retryCondition is null)
        {
            return null;
        }

        if (retryCondition.Delta is { } delta)
        {
            return delta > TimeSpan.Zero ? delta : TimeSpan.Zero;
        }

        if (retryCondition.Date is { } date)
        {
            var delay = date - now;
            return delay > TimeSpan.Zero ? delay : TimeSpan.Zero;
        }

        return null;
    }

    private static DateTimeOffset Max(DateTimeOffset left, DateTimeOffset right) =>
        left >= right ? left : right;

    private long ToTimestampTicks(TimeSpan duration) =>
        (long)Math.Ceiling(duration.TotalSeconds * _timestampFrequency);

    private static long Max(long left, long right) =>
        left >= right ? left : right;
}
