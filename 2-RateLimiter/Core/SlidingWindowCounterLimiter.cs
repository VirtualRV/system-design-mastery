using System.Collections.Concurrent;

namespace RateLimiter.Core;

/// <summary>
/// Sliding Window Counter Rate Limiter.
/// Solves the 2x boundary burst flaw of Fixed Window counters while maintaining O(1) memory overhead.
/// Blends the previous window's request count with the current window proportionally:
/// Approximate Count = CurrentWindowCount + (PreviousWindowCount * (1 - CurrentWindowProgress))
/// </summary>
public class SlidingWindowCounterLimiter : IRateLimiter
{
    private readonly long _limit;
    private readonly TimeSpan _windowSize;
    private readonly ConcurrentDictionary<string, ClientWindow> _clients = new();

    private class ClientWindow
    {
        public long CurrentWindowIndex;
        public long CurrentCount;
        public long PreviousCount;
        public readonly object SyncRoot = new();

        public ClientWindow(long initialIndex)
        {
            CurrentWindowIndex = initialIndex;
        }
    }

    public SlidingWindowCounterLimiter(long limit, TimeSpan windowSize)
    {
        if (limit <= 0) throw new ArgumentOutOfRangeException(nameof(limit));
        if (windowSize <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(windowSize));

        _limit = limit;
        _windowSize = windowSize;
    }

    public RateLimitResult TryAcquire(string clientKey, int cost = 1)
    {
        long nowTicks = DateTime.UtcNow.Ticks;
        long windowTicks = _windowSize.Ticks;
        long currentWindowIndex = nowTicks / windowTicks;

        var client = _clients.GetOrAdd(clientKey, _ => new ClientWindow(currentWindowIndex));

        lock (client.SyncRoot)
        {
            // Advance windows if time progressed
            if (currentWindowIndex > client.CurrentWindowIndex)
            {
                long windowsPassed = currentWindowIndex - client.CurrentWindowIndex;
                if (windowsPassed == 1)
                {
                    client.PreviousCount = client.CurrentCount;
                }
                else
                {
                    client.PreviousCount = 0; // More than 1 window passed, reset previous
                }
                client.CurrentCount = 0;
                client.CurrentWindowIndex = currentWindowIndex;
            }

            // Calculate current window progress [0.0 to 1.0]
            long timeIntoCurrentWindowTicks = nowTicks % windowTicks;
            double progress = (double)timeIntoCurrentWindowTicks / windowTicks;
            double weight = 1.0 - progress;

            double estimatedCount = client.CurrentCount + (client.PreviousCount * weight);

            if (estimatedCount + cost <= _limit)
            {
                client.CurrentCount += cost;
                long remaining = Math.Max(0, (long)Math.Floor(_limit - (estimatedCount + cost)));
                return new RateLimitResult(
                    IsAllowed: true,
                    Remaining: remaining,
                    Limit: _limit,
                    RetryAfter: TimeSpan.Zero
                );
            }
            else
            {
                // Calculate time remaining until current window ends
                long remainingTicks = windowTicks - timeIntoCurrentWindowTicks;
                return new RateLimitResult(
                    IsAllowed: false,
                    Remaining: 0,
                    Limit: _limit,
                    RetryAfter: TimeSpan.FromTicks(remainingTicks)
                );
            }
        }
    }
}
