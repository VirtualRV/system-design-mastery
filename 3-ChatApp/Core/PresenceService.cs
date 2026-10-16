using System.Collections.Concurrent;

namespace ChatApp.Core;

public interface IPresenceService
{
    void RecordHeartbeat(string userId);
    bool IsUserOnline(string userId);
    DateTime? GetLastSeen(string userId);
}

/// <summary>
/// Presence tracking engine with sliding TTL (simulates Redis TTL heartbeats).
/// </summary>
public class InMemoryPresenceService : IPresenceService
{
    private readonly ConcurrentDictionary<string, DateTime> _lastSeen = new();
    private readonly TimeSpan _timeout;

    public InMemoryPresenceService(TimeSpan? timeout = null)
    {
        _timeout = timeout ?? TimeSpan.FromSeconds(30);
    }

    public void RecordHeartbeat(string userId)
    {
        _lastSeen[userId] = DateTime.UtcNow;
    }

    public bool IsUserOnline(string userId)
    {
        if (_lastSeen.TryGetValue(userId, out var last))
        {
            return (DateTime.UtcNow - last) <= _timeout;
        }
        return false;
    }

    public DateTime? GetLastSeen(string userId)
    {
        return _lastSeen.TryGetValue(userId, out var last) ? last : null;
    }
}
