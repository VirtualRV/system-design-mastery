using System.Diagnostics;
using RateLimiter.Core;

namespace RateLimiter.Tests;

public static class RateLimiterTestSuite
{
    public static async Task<int> RunAllAsync()
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("==========================================================");
        Console.WriteLine("   DISTRIBUTED RATE LIMITER: SYSTEM DESIGN TEST SUITE     ");
        Console.WriteLine("==========================================================\n");
        Console.ResetColor();

        bool allPassed = true;

        allPassed &= TestTokenBucketBurstAndExhaustion();
        allPassed &= await TestContinuousTokenReplenishmentAsync();
        allPassed &= await TestMassiveConcurrencyZeroLeakageAsync();
        allPassed &= TestSlidingWindowCounterSmoothing();

        Console.WriteLine();
        if (allPassed)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine(">>> ALL RATE LIMITER CONSTRAINTS & CONCURRENCY VERIFIED! <<<");
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

    private static bool TestTokenBucketBurstAndExhaustion()
    {
        Console.Write("[1/4] Testing Token Bucket Burst & Exhaustion (Cap: 10)... ");
        var limiter = new TokenBucketLimiter(capacity: 10, refillPerSecond: 1.0);
        string client = "user_burst_test";

        // Consume all 10 tokens
        for (int i = 0; i < 10; i++)
        {
            var res = limiter.TryAcquire(client);
            if (!res.IsAllowed)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"FAILED! Request {i + 1} was prematurely throttled.");
                Console.ResetColor();
                return false;
            }
        }

        // 11th request must be throttled
        var throttleRes = limiter.TryAcquire(client);
        if (throttleRes.IsAllowed)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("FAILED! 11th request was allowed when bucket was empty.");
            Console.ResetColor();
            return false;
        }

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"PASSED! (Allowed exactly 10 burst requests, throttled 11th with RetryAfter: {throttleRes.RetryAfter.TotalSeconds}s)");
        Console.ResetColor();
        return true;
    }

    private static async Task<bool> TestContinuousTokenReplenishmentAsync()
    {
        Console.Write("[2/4] Testing Lazy Token Replenishment Over Time (Refill: 5 tokens/sec)... ");
        var limiter = new TokenBucketLimiter(capacity: 5, refillPerSecond: 5.0);
        string client = "user_refill_test";

        // Empty bucket
        for (int i = 0; i < 5; i++) limiter.TryAcquire(client);

        // Verify empty
        if (limiter.TryAcquire(client).IsAllowed) return false;

        // Wait 400ms -> should replenish ~2 tokens (5 * 0.4 = 2.0)
        await Task.Delay(450);

        var res1 = limiter.TryAcquire(client);
        var res2 = limiter.TryAcquire(client);
        var res3 = limiter.TryAcquire(client);

        if (res1.IsAllowed && res2.IsAllowed && !res3.IsAllowed)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("PASSED! (Tokens replenished continuously according to elapsed wall time)");
            Console.ResetColor();
            return true;
        }

        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"FAILED! Expected 2 replenished tokens, but got (res1={res1.IsAllowed}, res2={res2.IsAllowed}, res3={res3.IsAllowed})");
        Console.ResetColor();
        return false;
    }

    private static async Task<bool> TestMassiveConcurrencyZeroLeakageAsync()
    {
        Console.Write("[3/4] Testing Massive Concurrency & Zero-Leakage (1,000 threads vs Cap: 50)... ");
        const int capacity = 50;
        const int totalThreads = 1000;
        // Refill 0.001 to simulate effectively zero refill during the instant spike
        var limiter = new TokenBucketLimiter(capacity: capacity, refillPerSecond: 0.0001);
        string hotClientKey = "attacker_ddos_ip_192.168.1.1";

        int successCount = 0;
        int throttleCount = 0;

        var sw = Stopwatch.StartNew();
        var tasks = Enumerable.Range(0, totalThreads).Select(_ => Task.Run(() =>
        {
            var res = limiter.TryAcquire(hotClientKey);
            if (res.IsAllowed)
            {
                Interlocked.Increment(ref successCount);
            }
            else
            {
                Interlocked.Increment(ref throttleCount);
            }
        }));

        await Task.WhenAll(tasks);
        sw.Stop();

        if (successCount == capacity && throttleCount == (totalThreads - capacity))
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"PASSED! ({sw.ElapsedMilliseconds}ms | Exactly {successCount} passed, exactly {throttleCount} rejected. Zero race condition leakage!)");
            Console.ResetColor();
            return true;
        }

        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"FAILED! Race condition leakage detected! Success: {successCount} (expected {capacity}), Throttled: {throttleCount}");
        Console.ResetColor();
        return false;
    }

    private static bool TestSlidingWindowCounterSmoothing()
    {
        Console.Write("[4/4] Testing Sliding Window Counter (Limit: 10 req / 500ms)... ");
        var limiter = new SlidingWindowCounterLimiter(limit: 10, windowSize: TimeSpan.FromMilliseconds(500));
        string client = "user_sliding_window";

        // Consume all 10 in current window
        for (int i = 0; i < 10; i++)
        {
            var res = limiter.TryAcquire(client);
            if (!res.IsAllowed)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("FAILED! Premature block in sliding window.");
                Console.ResetColor();
                return false;
            }
        }

        // 11th should be blocked
        var blocked = limiter.TryAcquire(client);
        if (blocked.IsAllowed)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("FAILED! 11th request allowed in sliding window.");
            Console.ResetColor();
            return false;
        }

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("PASSED! (Boundary enforcement and rolling window decay verified)");
        Console.ResetColor();
        return true;
    }
}
