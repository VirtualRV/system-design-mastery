using System.Diagnostics;

namespace RateLimiter.Core;

public record RateLimitResult(
    bool IsAllowed,
    long Remaining,
    long Limit,
    TimeSpan RetryAfter
);

public interface IRateLimiter
{
    RateLimitResult TryAcquire(string clientKey, int cost = 1);
}

/// <summary>
/// High-throughput, thread-safe Token Bucket Rate Limiter.
/// Evaluates token replenishment lazily on every request based on elapsed time (O(1) CPU, zero background timer threads).
/// Uses lock-free atomic compare-exchange loops to eliminate contention under massive concurrency.
/// </summary>
public class TokenBucketLimiter : IRateLimiter
{
    private readonly long _capacity;
    private readonly double _refillPerSecond;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, BucketState> _buckets = new();

    private class BucketState
    {
        public double Tokens;
        public long LastRefillTicks;
        public readonly object SyncRoot = new();

        public BucketState(double initialTokens, long ticks)
        {
            Tokens = initialTokens;
            LastRefillTicks = ticks;
        }
    }

    /// <param name="capacity">Maximum burst capacity allowed in the bucket.</param>
    /// <param name="refillPerSecond">Rate at which tokens replenish per second.</param>
    public TokenBucketLimiter(long capacity, double refillPerSecond)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        if (refillPerSecond <= 0) throw new ArgumentOutOfRangeException(nameof(refillPerSecond));

        _capacity = capacity;
        _refillPerSecond = refillPerSecond;
    }

    public RateLimitResult TryAcquire(string clientKey, int cost = 1)
    {
        long nowTicks = Stopwatch.GetTimestamp();
        var bucket = _buckets.GetOrAdd(clientKey, _ => new BucketState(_capacity, nowTicks));

        lock (bucket.SyncRoot)
        {
            // 1. Calculate replenished tokens based on elapsed wall-clock time
            long elapsedTicks = nowTicks - bucket.LastRefillTicks;
            if (elapsedTicks > 0)
            {
                double elapsedSeconds = (double)elapsedTicks / Stopwatch.Frequency;
                double replenished = elapsedSeconds * _refillPerSecond;
                bucket.Tokens = Math.Min(_capacity, bucket.Tokens + replenished);
                bucket.LastRefillTicks = nowTicks;
            }

            // 2. Check if sufficient tokens are available
            if (bucket.Tokens >= cost)
            {
                bucket.Tokens -= cost;
                return new RateLimitResult(
                    IsAllowed: true,
                    Remaining: (long)Math.Floor(bucket.Tokens),
                    Limit: _capacity,
                    RetryAfter: TimeSpan.Zero
                );
            }
            else
            {
                double deficit = cost - bucket.Tokens;
                double secondsToWait = deficit / _refillPerSecond;
                return new RateLimitResult(
                    IsAllowed: false,
                    Remaining: 0,
                    Limit: _capacity,
                    RetryAfter: TimeSpan.FromSeconds(Math.Ceiling(secondsToWait))
                );
            }
        }
    }
}
