using System.Collections.Concurrent;

namespace NewsFeed.Core;

public interface ITimelineStore
{
    void AddEntry(string timelineKey, TimelineEntry entry, int maxCapacity = 800);
    IReadOnlyList<TimelineEntry> GetRange(string timelineKey, long? maxScoreExclusive, int limit);
    int GetCount(string timelineKey);
}

/// <summary>
/// In-memory simulation of Redis Sorted Sets (ZSET) for timelines and celebrity outboxes.
/// Maintains items sorted descending by Score (Timestamp).
/// Automatically caps timelines to maxCapacity (simulating ZREMRANGEBYRANK).
/// </summary>
public class InMemoryTimelineStore : ITimelineStore
{
    private readonly ConcurrentDictionary<string, SortedSet<TimelineEntry>> _timelines = new();
    private readonly ConcurrentDictionary<string, object> _locks = new();

    public void AddEntry(string timelineKey, TimelineEntry entry, int maxCapacity = 800)
    {
        var syncLock = _locks.GetOrAdd(timelineKey, _ => new object());
        lock (syncLock)
        {
            var set = _timelines.GetOrAdd(timelineKey, _ => new SortedSet<TimelineEntry>());
            set.Add(entry);

            // Evict oldest items if exceeding capacity
            while (set.Count > maxCapacity)
            {
                set.Remove(set.Max!); // In descending order, set.Max is the lowest score (oldest)
            }
        }
    }

    public IReadOnlyList<TimelineEntry> GetRange(string timelineKey, long? maxScoreExclusive, int limit)
    {
        if (!_timelines.TryGetValue(timelineKey, out var set))
        {
            return Array.Empty<TimelineEntry>();
        }

        var syncLock = _locks.GetOrAdd(timelineKey, _ => new object());
        lock (syncLock)
        {
            IEnumerable<TimelineEntry> query = set;

            if (maxScoreExclusive.HasValue)
            {
                query = query.Where(e => e.Score < maxScoreExclusive.Value);
            }

            return query.Take(limit).ToList();
        }
    }

    public int GetCount(string timelineKey)
    {
        return _timelines.TryGetValue(timelineKey, out var set) ? set.Count : 0;
    }
}
