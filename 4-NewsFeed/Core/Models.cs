namespace NewsFeed.Core;

public record User(
    string UserId,
    string Username,
    bool IsCelebrity = false
);

public record Post(
    string PostId,
    string AuthorId,
    string Content,
    long CreatedAtTicks,
    int LikeCount = 0
)
{
    public DateTime CreatedAt => new(CreatedAtTicks, DateTimeKind.Utc);
}

public record TimelineEntry(
    string PostId,
    long Score // Typically CreatedAtTicks or Ranking Score
) : IComparable<TimelineEntry>
{
    public int CompareTo(TimelineEntry? other)
    {
        if (other == null) return 1;
        // Primary sort: Score descending (latest/highest first)
        int scoreComparison = other.Score.CompareTo(Score);
        if (scoreComparison != 0) return scoreComparison;
        // Secondary sort: PostId tiebreaker descending
        return string.Compare(other.PostId, PostId, StringComparison.Ordinal);
    }
}

public record FeedResponse(
    IReadOnlyList<Post> Posts,
    string? NextCursor,
    bool HasMore
);
