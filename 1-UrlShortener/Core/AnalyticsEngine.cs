using System.Collections.Concurrent;
using System.Threading.Channels;

namespace UrlShortener.Core;

public record ClickEvent(
    string ShortCode,
    DateTime Timestamp,
    string? Referrer,
    string? UserAgent,
    string? IpAddress
);

public record UrlAnalytics(
    string ShortCode,
    long TotalClicks,
    Dictionary<string, int> TopReferrers,
    List<DateTime> RecentClicks
);

public interface IAnalyticsEngine
{
    bool EnqueueClick(ClickEvent click);
    UrlAnalytics GetAnalytics(string shortCode);
}

/// <summary>
/// High-throughput event ingestion engine using System.Threading.Channels.
/// Simulates Kafka ingestion: writes to the channel are non-blocking (<1 microsecond),
/// allowing the HTTP redirect to return 307 immediately.
/// Background processor consumes and aggregates clicks in batches.
/// </summary>
public class ChannelAnalyticsEngine : IAnalyticsEngine, IDisposable
{
    private readonly Channel<ClickEvent> _channel;
    private readonly ConcurrentDictionary<string, ClickAggregate> _aggregates = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _consumerTask;

    private class ClickAggregate
    {
        public long TotalClicks;
        public ConcurrentDictionary<string, int> Referrers { get; } = new();
        public ConcurrentQueue<DateTime> RecentTimestamps { get; } = new();
    }

    public ChannelAnalyticsEngine(int channelCapacity = 100_000)
    {
        var options = new BoundedChannelOptions(channelCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest, // Prioritize system liveness under severe backpressure
            SingleWriter = false,
            SingleReader = true
        };

        _channel = Channel.CreateBounded<ClickEvent>(options);
        _consumerTask = Task.Run(ProcessBatchQueueAsync);
    }

    public bool EnqueueClick(ClickEvent click)
    {
        // Non-blocking write: Never blocks the HTTP redirection path
        return _channel.Writer.TryWrite(click);
    }

    public UrlAnalytics GetAnalytics(string shortCode)
    {
        if (!_aggregates.TryGetValue(shortCode, out var agg))
        {
            return new UrlAnalytics(shortCode, 0, new Dictionary<string, int>(), new List<DateTime>());
        }

        return new UrlAnalytics(
            ShortCode: shortCode,
            TotalClicks: Volatile.Read(ref agg.TotalClicks),
            TopReferrers: agg.Referrers.OrderByDescending(r => r.Value).Take(5).ToDictionary(k => k.Key, v => v.Value),
            RecentClicks: agg.RecentTimestamps.Take(10).ToList()
        );
    }

    private async Task ProcessBatchQueueAsync()
    {
        var reader = _channel.Reader;
        var batch = new List<ClickEvent>(500);

        while (!_cts.Token.IsCancellationRequested)
        {
            try
            {
                // Wait for data
                if (await reader.WaitToReadAsync(_cts.Token))
                {
                    while (reader.TryRead(out var click))
                    {
                        batch.Add(click);
                        if (batch.Count >= 500) break;
                    }

                    // Process batch
                    foreach (var evt in batch)
                    {
                        var agg = _aggregates.GetOrAdd(evt.ShortCode, _ => new ClickAggregate());
                        Interlocked.Increment(ref agg.TotalClicks);

                        string refDomain = string.IsNullOrWhiteSpace(evt.Referrer) ? "Direct" : evt.Referrer;
                        agg.Referrers.AddOrUpdate(refDomain, 1, (_, count) => count + 1);

                        agg.RecentTimestamps.Enqueue(evt.Timestamp);
                        while (agg.RecentTimestamps.Count > 50)
                        {
                            agg.RecentTimestamps.TryDequeue(out _);
                        }
                    }

                    batch.Clear();
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _channel.Writer.Complete();
        _cts.Dispose();
    }
}
