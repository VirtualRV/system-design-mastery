namespace NewsFeed.Core;

public class FeedService
{
    private readonly IPostStore _postStore;
    private readonly ITimelineStore _timelineStore;
    private readonly ISocialGraphService _socialGraph;
    private static long _sequence = 1000;

    public FeedService(
        IPostStore postStore,
        ITimelineStore timelineStore,
        ISocialGraphService socialGraph)
    {
        _postStore = postStore;
        _timelineStore = timelineStore;
        _socialGraph = socialGraph;
    }

    public Post PublishPost(string authorId, string content)
    {
        long ticks = DateTime.UtcNow.Ticks;
        long seq = Interlocked.Increment(ref _sequence);
        string postId = $"post_{ticks}_{seq}";

        var post = new Post(
            PostId: postId,
            AuthorId: authorId,
            Content: content,
            CreatedAtTicks: ticks
        );

        // 1. Persist full post
        _postStore.SavePost(post);

        var entry = new TimelineEntry(postId, ticks);

        // 2. Hybrid Fanout Decision
        if (_socialGraph.IsCelebrity(authorId))
        {
            // CELEBRITY: Skip Fanout! Write only to Celebrity Outbox
            _timelineStore.AddEntry($"outbox:{authorId}", entry);
        }
        else
        {
            // REGULAR USER: Fanout-on-Write to all followers' Redis timelines
            var followers = _socialGraph.GetFollowers(authorId);
            foreach (var followerId in followers)
            {
                _timelineStore.AddEntry($"timeline:{followerId}", entry);
            }
        }

        return post;
    }

    public FeedResponse GetHomeFeed(string userId, int limit = 10, string? cursor = null)
    {
        long? maxScoreExclusive = null;
        if (!string.IsNullOrWhiteSpace(cursor) && long.TryParse(cursor, out long parsedScore))
        {
            maxScoreExclusive = parsedScore;
        }

        // 1. Fetch precomputed timeline from Redis (for regular followees)
        var precomputed = _timelineStore.GetRange($"timeline:{userId}", maxScoreExclusive, limit * 2);

        // 2. Fetch posts from Celebrities followed by this user (Pull Model)
        var followedCelebrities = _socialGraph.GetFollowedCelebrities(userId);
        var celebrityEntries = new List<TimelineEntry>();

        foreach (var celebId in followedCelebrities)
        {
            var celebPosts = _timelineStore.GetRange($"outbox:{celebId}", maxScoreExclusive, limit);
            celebrityEntries.AddRange(celebPosts);
        }

        // 3. In-Memory K-Way Merge: Combine precomputed and celebrity streams
        var merged = precomputed
            .Concat(celebrityEntries)
            .OrderByDescending(e => e.Score)
            .ThenByDescending(e => e.PostId)
            .Take(limit + 1) // Take 1 extra to check if HasMore
            .ToList();

        bool hasMore = merged.Count > limit;
        var pagedEntries = merged.Take(limit).ToList();

        // 4. Batch Hydration: Retrieve full post payloads from storage
        var postIds = pagedEntries.Select(e => e.PostId).Distinct();
        var postMap = _postStore.GetPostsBatch(postIds);

        var finalPosts = pagedEntries
            .Where(e => postMap.ContainsKey(e.PostId))
            .Select(e => postMap[e.PostId])
            .ToList();

        // 5. Cursor Generation: Lowest score in current page becomes next cursor
        string? nextCursor = hasMore && pagedEntries.Count > 0
            ? pagedEntries.Last().Score.ToString()
            : null;

        return new FeedResponse(finalPosts, nextCursor, hasMore);
    }
}
