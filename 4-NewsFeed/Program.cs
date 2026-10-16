using Microsoft.AspNetCore.Mvc;
using NewsFeed.Core;
using NewsFeed.Tests;

// CLI Test Runner Flag
if (args.Contains("--test"))
{
    int exitCode = await NewsFeedTestSuite.RunAllAsync();
    Environment.Exit(exitCode);
}

var builder = WebApplication.CreateBuilder(args);

// Register Core Singletons
var postStore = new InMemoryPostStore();
var timelineStore = new InMemoryTimelineStore();
var socialGraph = new InMemorySocialGraphService();
var feedService = new FeedService(postStore, timelineStore, socialGraph);

// Seed Demo Users
socialGraph.RegisterUser(new User("alice", "Alice"));
socialGraph.RegisterUser(new User("bob", "Bob"));
socialGraph.RegisterUser(new User("elon", "Elon Musk", IsCelebrity: true));

// Default follow: Alice follows Bob & Elon
socialGraph.Follow("alice", "bob");
socialGraph.Follow("alice", "elon");

builder.Services.AddSingleton<IPostStore>(postStore);
builder.Services.AddSingleton<ITimelineStore>(timelineStore);
builder.Services.AddSingleton<ISocialGraphService>(socialGraph);
builder.Services.AddSingleton(feedService);

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

// 1. Publish Post Endpoint
app.MapPost("/api/posts", ([FromBody] CreatePostRequest request, FeedService feed) =>
{
    if (string.IsNullOrWhiteSpace(request.AuthorId) || string.IsNullOrWhiteSpace(request.Content))
    {
        return Results.BadRequest(new { error = "AuthorId and Content are required." });
    }

    var post = feed.PublishPost(request.AuthorId, request.Content);
    return Results.Created($"/api/posts/{post.PostId}", post);
});

// 2. Follow Endpoint
app.MapPost("/api/follow", ([FromBody] FollowRequest request, ISocialGraphService graph) =>
{
    graph.Follow(request.FollowerId, request.FolloweeId);
    return Results.Ok(new { message = $"{request.FollowerId} now follows {request.FolloweeId}." });
});

// 3. Get Home Feed Endpoint (with Cursor-based pagination)
app.MapGet("/api/feed", (
    [FromQuery] string userId,
    [FromQuery] int limit,
    [FromQuery] string? cursor,
    FeedService feed) =>
{
    if (string.IsNullOrWhiteSpace(userId))
    {
        return Results.BadRequest(new { error = "Query parameter 'userId' is required." });
    }

    int pageSize = limit is > 0 and <= 50 ? limit : 10;
    var feedResult = feed.GetHomeFeed(userId, pageSize, cursor);
    return Results.Ok(feedResult);
});

app.Run();

public record CreatePostRequest(string AuthorId, string Content);
public record FollowRequest(string FollowerId, string FolloweeId);
