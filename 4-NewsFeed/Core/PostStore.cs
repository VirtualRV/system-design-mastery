using System.Collections.Concurrent;

namespace NewsFeed.Core;

public interface IPostStore
{
    void SavePost(Post post);
    Post? GetPost(string postId);
    IReadOnlyDictionary<string, Post> GetPostsBatch(IEnumerable<string> postIds);
}

public class InMemoryPostStore : IPostStore
{
    private readonly ConcurrentDictionary<string, Post> _posts = new();

    public void SavePost(Post post)
    {
        _posts[post.PostId] = post;
    }

    public Post? GetPost(string postId)
    {
        _posts.TryGetValue(postId, out var post);
        return post;
    }

    public IReadOnlyDictionary<string, Post> GetPostsBatch(IEnumerable<string> postIds)
    {
        var dict = new Dictionary<string, Post>();
        foreach (var id in postIds)
        {
            if (_posts.TryGetValue(id, out var post))
            {
                dict[id] = post;
            }
        }
        return dict;
    }
}
