using System.Collections.Concurrent;

namespace NewsFeed.Core;

public interface ISocialGraphService
{
    void RegisterUser(User user);
    User? GetUser(string userId);
    void Follow(string followerId, string followeeId);
    void Unfollow(string followerId, string followeeId);
    IReadOnlyList<string> GetFollowers(string followeeId);
    IReadOnlyList<string> GetFollowedCelebrities(string followerId);
    bool IsCelebrity(string userId);
}

public class InMemorySocialGraphService : ISocialGraphService
{
    private readonly ConcurrentDictionary<string, User> _users = new();
    // followeeId -> Set of followerIds
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _followers = new();
    // followerId -> Set of followeeIds
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _following = new();

    public void RegisterUser(User user)
    {
        _users[user.UserId] = user;
    }

    public User? GetUser(string userId)
    {
        _users.TryGetValue(userId, out var user);
        return user;
    }

    public void Follow(string followerId, string followeeId)
    {
        var followerSet = _followers.GetOrAdd(followeeId, _ => new ConcurrentDictionary<string, byte>());
        followerSet[followerId] = 0;

        var followingSet = _following.GetOrAdd(followerId, _ => new ConcurrentDictionary<string, byte>());
        followingSet[followeeId] = 0;
    }

    public void Unfollow(string followerId, string followeeId)
    {
        if (_followers.TryGetValue(followeeId, out var followerSet))
        {
            followerSet.TryRemove(followerId, out _);
        }
        if (_following.TryGetValue(followerId, out var followingSet))
        {
            followingSet.TryRemove(followeeId, out _);
        }
    }

    public IReadOnlyList<string> GetFollowers(string followeeId)
    {
        if (_followers.TryGetValue(followeeId, out var set))
        {
            return set.Keys.ToList();
        }
        return Array.Empty<string>();
    }

    public IReadOnlyList<string> GetFollowedCelebrities(string followerId)
    {
        if (!_following.TryGetValue(followerId, out var followees))
        {
            return Array.Empty<string>();
        }

        return followees.Keys
            .Where(id => _users.TryGetValue(id, out var u) && u.IsCelebrity)
            .ToList();
    }

    public bool IsCelebrity(string userId)
    {
        return _users.TryGetValue(userId, out var user) && user.IsCelebrity;
    }
}
