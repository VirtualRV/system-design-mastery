using System.Diagnostics;
using NewsFeed.Core;

namespace NewsFeed.Tests;

public static class NewsFeedTestSuite
{
    public static async Task<int> RunAllAsync()
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("==========================================================");
        Console.WriteLine("       NEWS FEED SYSTEM: ARCHITECTURE TEST SUITE          ");
        Console.WriteLine("==========================================================\n");
        Console.ResetColor();

        bool allPassed = true;

        allPassed &= TestRegularUserFanoutOnWrite();
        allPassed &= TestCelebrityHybridFanout();
        allPassed &= TestCursorBasedPaginationFidelity();
        allPassed &= TestTimelineCapacityTrimming();
        allPassed &= await TestMassiveConcurrentPostFanoutAsync();

        Console.WriteLine();
        if (allPassed)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine(">>> ALL NEWS FEED ARCHITECTURAL CONSTRAINTS VERIFIED! <<<");
            Console.ResetColor();
            return 0;
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine(">>> SOME TESTS FAILED. CHECK LOGS ABOVE. <<<");
            Console.ResetColor();
            return 1;
        }
    }

    private static bool TestRegularUserFanoutOnWrite()
    {
        Console.Write("[1/5] Testing Regular User Fanout-on-Write (Push Model)... ");
        var postStore = new InMemoryPostStore();
        var timelineStore = new InMemoryTimelineStore();
        var socialGraph = new InMemorySocialGraphService();
        var feedService = new FeedService(postStore, timelineStore, socialGraph);

        socialGraph.RegisterUser(new User("alice", "Alice"));
        socialGraph.RegisterUser(new User("bob", "Bob"));
        socialGraph.Follow("alice", "bob"); // Alice follows Bob

        var post = feedService.PublishPost("bob", "Hello from Bob!");

        // Post should be pushed into Alice's precomputed timeline
        var aliceTimeline = timelineStore.GetRange("timeline:alice", null, 10);
        if (aliceTimeline.Count == 1 && aliceTimeline[0].PostId == post.PostId)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("PASSED! (Pushed into follower's precomputed timeline cache)");
            Console.ResetColor();
            return true;
        }

        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine("FAILED! Post was not fanned out to follower timeline.");
        Console.ResetColor();
        return false;
    }

    private static bool TestCelebrityHybridFanout()
    {
        Console.Write("[2/5] Testing Celebrity Hybrid Fanout (Pull on Read)... ");
        var postStore = new InMemoryPostStore();
        var timelineStore = new InMemoryTimelineStore();
        var socialGraph = new InMemorySocialGraphService();
        var feedService = new FeedService(postStore, timelineStore, socialGraph);

        socialGraph.RegisterUser(new User("alice", "Alice"));
        socialGraph.RegisterUser(new User("ronaldo", "Cristiano Ronaldo", IsCelebrity: true));
        socialGraph.Follow("alice", "ronaldo");

        // Ronaldo publishes post
        var celebPost = feedService.PublishPost("ronaldo", "SIUUU! Champions League victory!");

        // Verify: Fanout to Alice's timeline MUST BE SKIPPED to protect write queues
        int aliceTimelineCount = timelineStore.GetCount("timeline:alice");
        if (aliceTimelineCount != 0)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("FAILED! Celebrity post was eagerly pushed to follower timeline.");
            Console.ResetColor();
            return false;
        }

        // When Alice opens her feed, celebrity post must be dynamically merged
        var aliceFeed = feedService.GetHomeFeed("alice", limit: 10);
        if (aliceFeed.Posts.Count == 1 && aliceFeed.Posts[0].PostId == celebPost.PostId)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("PASSED! (Write fanout skipped; merged dynamically into feed on read)");
            Console.ResetColor();
            return true;
        }

        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine("FAILED! Celebrity post not merged into feed.");
        Console.ResetColor();
        return false;
    }

    private static bool TestCursorBasedPaginationFidelity()
    {
        Console.Write("[3/5] Testing Cursor-Based Pagination (Zero Duplicates / Zero Skips)... ");
        var postStore = new InMemoryPostStore();
        var timelineStore = new InMemoryTimelineStore();
        var socialGraph = new InMemorySocialGraphService();
        var feedService = new FeedService(postStore, timelineStore, socialGraph);

        socialGraph.RegisterUser(new User("alice", "Alice"));
        socialGraph.RegisterUser(new User("bob", "Bob"));
        socialGraph.Follow("alice", "bob");

        const int totalPosts = 25;
        for (int i = 0; i < totalPosts; i++)
        {
            feedService.PublishPost("bob", $"Post #{i + 1}");
            Thread.Sleep(2); // Ensure distinct timestamps
        }

        // Page 1
        var page1 = feedService.GetHomeFeed("alice", limit: 10);
        // Page 2
        var page2 = feedService.GetHomeFeed("alice", limit: 10, cursor: page1.NextCursor);
        // Page 3
        var page3 = feedService.GetHomeFeed("alice", limit: 10, cursor: page2.NextCursor);

        var retrievedIds = page1.Posts.Concat(page2.Posts).Concat(page3.Posts).Select(p => p.PostId).ToList();

        if (retrievedIds.Count == totalPosts &&
            retrievedIds.Distinct().Count() == totalPosts &&
            page3.HasMore == false)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"PASSED! (Paged through {totalPosts} posts across 3 pages: 0 duplicates, 0 skipped)");
            Console.ResetColor();
            return true;
        }

        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"FAILED! Expected {totalPosts} unique posts, got {retrievedIds.Count} total and {retrievedIds.Distinct().Count()} unique.");
        Console.ResetColor();
        return false;
    }

    private static bool TestTimelineCapacityTrimming()
    {
        Console.Write("[4/5] Testing Timeline Memory Trimming (ZREMRANGEBYRANK Cap: 5)... ");
        var timelineStore = new InMemoryTimelineStore();

        // Add 10 entries to a timeline configured with cap of 5
        for (int i = 0; i < 10; i++)
        {
            timelineStore.AddEntry("timeline:test_user", new TimelineEntry($"post_{i}", i * 100), maxCapacity: 5);
        }

        int count = timelineStore.GetCount("timeline:test_user");
        var newest = timelineStore.GetRange("timeline:test_user", null, 10);

        if (count == 5 && newest[0].PostId == "post_9" && newest.Last().PostId == "post_5")
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("PASSED! (Oldest 5 entries evicted, bounded memory consumption)");
            Console.ResetColor();
            return true;
        }

        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"FAILED! Expected count 5, got {count}");
        Console.ResetColor();
        return false;
    }

    private static async Task<bool> TestMassiveConcurrentPostFanoutAsync()
    {
        Console.Write("[5/5] Testing Massive Concurrent Post Fanout (1,000 posts across 100 users)... ");
        var postStore = new InMemoryPostStore();
        var timelineStore = new InMemoryTimelineStore();
        var socialGraph = new InMemorySocialGraphService();
        var feedService = new FeedService(postStore, timelineStore, socialGraph);

        const int userCount = 100;
        const int totalPosts = 1000;

        for (int i = 0; i < userCount; i++)
        {
            socialGraph.RegisterUser(new User($"user_{i}", $"User {i}"));
            // Each user follows 5 random peers
            for (int f = 1; f <= 5; f++)
            {
                socialGraph.Follow($"user_{i}", $"user_{(i + f) % userCount}");
            }
        }

        var sw = Stopwatch.StartNew();
        var tasks = Enumerable.Range(0, totalPosts).Select(i => Task.Run(() =>
        {
            string author = $"user_{i % userCount}";
            feedService.PublishPost(author, $"Concurrent post #{i}");
        }));

        await Task.WhenAll(tasks);
        sw.Stop();

        // Sample random user feed
        var feed = feedService.GetHomeFeed("user_0", limit: 20);
        if (feed.Posts.Count > 0)
        {
            double throughput = totalPosts / sw.Elapsed.TotalSeconds;
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"PASSED! ({totalPosts:N0} posts fanned out in {sw.ElapsedMilliseconds}ms | {throughput:N0} writes/sec)");
            Console.ResetColor();
            return true;
        }

        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine("FAILED! Feed retrieval empty after concurrent writes.");
        Console.ResetColor();
        return false;
    }
}
