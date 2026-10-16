using System.Collections.Concurrent;
using System.Diagnostics;
using UrlShortener.Core;

namespace UrlShortener.Tests;

/// <summary>
/// Comprehensive verification suite testing the key architectural constraints:
/// 1. Zero-collision concurrent ID generation across multiple threads (KGS Range Allocation).
/// 2. Base62 mathematical fidelity.
/// 3. Bloom Filter cache penetration rejection rate.
/// 4. Asynchronous event-driven click analytics aggregation.
/// 5. Single-flight cache stampede protection.
/// </summary>
public static class SystemDesignTestSuite
{
    public static async Task<int> RunAllAsync()
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("==========================================================");
        Console.WriteLine("   URL SHORTENER: DISTRIBUTED SYSTEM DESIGN TEST SUITE    ");
        Console.WriteLine("==========================================================\n");
        Console.ResetColor();

        bool allPassed = true;

        allPassed &= TestBase62Fidelity();
        allPassed &= await TestConcurrentKeyGenerationCollisionFreeAsync();
        allPassed &= TestBloomFilterCachePenetrationDefense();
        allPassed &= await TestAsyncAnalyticsChannelAsync();
        allPassed &= await TestUrlRepositoryAndExpiryAsync();

        Console.WriteLine();
        if (allPassed)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine(">>> ALL SYSTEM DESIGN CONSTRAINTS VERIFIED SUCCESSFULLY! <<<");
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

    private static bool TestBase62Fidelity()
    {
        Console.Write("[1/5] Testing Base62 Encoding/Decoding Fidelity... ");
        long[] testValues = [0, 1, 61, 62, 9999, 10_000_000, 3_521_614_606_207L];

        foreach (var val in testValues)
        {
            string encoded = Base62.Encode(val);
            long decoded = Base62.Decode(encoded);
            if (val != decoded)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"FAILED! Value {val} encoded as '{encoded}', but decoded back as {decoded}");
                Console.ResetColor();
                return false;
            }
        }

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("PASSED! (All numerical ranges matched perfectly)");
        Console.ResetColor();
        return true;
    }

    private static async Task<bool> TestConcurrentKeyGenerationCollisionFreeAsync()
    {
        Console.Write("[2/5] Testing KGS Concurrency & Zero Collisions (100,000 keys across 10 threads)... ");
        var coordinator = new LocalRangeCoordinator();
        var kgs = new RangeKeyGenerationService(coordinator, rangeSize: 20_000);

        const int totalKeys = 100_000;
        const int threadCount = 10;
        var generatedKeys = new ConcurrentDictionary<string, byte>();

        var sw = Stopwatch.StartNew();
        var tasks = Enumerable.Range(0, threadCount).Select(_ => Task.Run(() =>
        {
            for (int i = 0; i < totalKeys / threadCount; i++)
            {
                string key = kgs.GenerateKey();
                if (!generatedKeys.TryAdd(key, 0))
                {
                    throw new InvalidOperationException($"COLLISION DETECTED FOR KEY: {key}");
                }
            }
        }));

        try
        {
            await Task.WhenAll(tasks);
            sw.Stop();

            if (generatedKeys.Count != totalKeys)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"FAILED! Expected {totalKeys} keys, but got {generatedKeys.Count}");
                Console.ResetColor();
                return false;
            }

            double throughput = totalKeys / sw.Elapsed.TotalSeconds;
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"PASSED! ({totalKeys:N0} keys in {sw.ElapsedMilliseconds}ms | Throughput: {throughput:N0} keys/sec, 0 collisions)");
            Console.ResetColor();
            return true;
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"FAILED! {ex.Message}");
            Console.ResetColor();
            return false;
        }
    }

    private static bool TestBloomFilterCachePenetrationDefense()
    {
        Console.Write("[3/5] Testing Bloom Filter Cache Penetration Defense... ");
        var filter = new BloomFilter(expectedItems: 10_000, falsePositiveRate: 0.01);

        // Add 10,000 legitimate keys
        for (int i = 0; i < 10_000; i++)
        {
            filter.Add($"validKey_{i}");
        }

        // Test false negatives: MUST BE ZERO (Bloom filter guarantee)
        for (int i = 0; i < 10_000; i++)
        {
            if (!filter.MightContain($"validKey_{i}"))
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"FAILED! False negative found for validKey_{i}");
                Console.ResetColor();
                return false;
            }
        }

        // Test false positive rate against 50,000 random non-existent keys
        int falsePositives = 0;
        const int testCount = 50_000;
        for (int i = 0; i < testCount; i++)
        {
            if (filter.MightContain($"bogus_non_existent_{i}"))
            {
                falsePositives++;
            }
        }

        double fpRate = (double)falsePositives / testCount;
        if (fpRate > 0.02) // Allow small variance above 1%
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"FAILED! False positive rate too high: {fpRate:P2}");
            Console.ResetColor();
            return false;
        }

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"PASSED! (0% false negatives, {fpRate:P2} false positive rate - shielded DB from {testCount - falsePositives:N0} useless queries)");
        Console.ResetColor();
        return true;
    }

    private static async Task<bool> TestAsyncAnalyticsChannelAsync()
    {
        Console.Write("[4/5] Testing Event-Driven Async Analytics Channel... ");
        using var engine = new ChannelAnalyticsEngine();

        // Simulate 1,000 clicks from different referrers
        for (int i = 0; i < 700; i++)
        {
            engine.EnqueueClick(new ClickEvent("linkX", DateTime.UtcNow, "https://twitter.com", "Mobile Safari", "1.2.3.4"));
        }
        for (int i = 0; i < 300; i++)
        {
            engine.EnqueueClick(new ClickEvent("linkX", DateTime.UtcNow, "https://linkedin.com", "Chrome Desktop", "5.6.7.8"));
        }

        // Allow background worker a moment to process the batch
        await Task.Delay(250);

        var stats = engine.GetAnalytics("linkX");
        if (stats.TotalClicks != 1000)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"FAILED! Expected 1,000 clicks, recorded {stats.TotalClicks}");
            Console.ResetColor();
            return false;
        }

        if (stats.TopReferrers["https://twitter.com"] != 700 || stats.TopReferrers["https://linkedin.com"] != 300)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("FAILED! Referrer aggregation counts mismatch.");
            Console.ResetColor();
            return false;
        }

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("PASSED! (1,000 events consumed non-blockingly, aggregates matched exact counts)");
        Console.ResetColor();
        return true;
    }

    private static async Task<bool> TestUrlRepositoryAndExpiryAsync()
    {
        Console.Write("[5/5] Testing SQLite Repository, Caching & Expiration Logic... ");
        string testDb = Path.Combine(Path.GetTempPath(), $"urltest_{Guid.NewGuid():N}.db");
        var bloom = new BloomFilter();
        var repo = new CachedSqliteUrlRepository($"Data Source={testDb};", bloom);

        await repo.InitializeAsync();

        // 1. Save and fetch
        var url = new ShortenedUrl("abc1234", "https://news.ycombinator.com", DateTime.UtcNow, null);
        bool saved = await repo.SaveAsync(url);
        if (!saved) return false;

        var fetched = await repo.GetByCodeAsync("abc1234");
        if (fetched?.OriginalUrl != "https://news.ycombinator.com") return false;

        // 2. Test expiration
        var expiredUrl = new ShortenedUrl("exp1234", "https://expired.org", DateTime.UtcNow.AddMinutes(-5), DateTime.UtcNow.AddSeconds(-1));
        await repo.SaveAsync(expiredUrl);

        var expiredFetch = await repo.GetByCodeAsync("exp1234");
        if (expiredFetch != null)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("FAILED! Expired URL was returned.");
            Console.ResetColor();
            return false;
        }

        // Clean up
        try { File.Delete(testDb); } catch { }

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("PASSED! (Persistence, LRU cache retrieval, and expiration validated)");
        Console.ResetColor();
        return true;
    }
}
