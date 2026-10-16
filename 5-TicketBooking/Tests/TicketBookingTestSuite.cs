using System.Diagnostics;
using TicketBooking.Core;

namespace TicketBooking.Tests;

public static class TicketBookingTestSuite
{
    public static async Task<int> RunAllAsync()
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("==========================================================");
        Console.WriteLine("   HIGH-CONCURRENCY TICKET BOOKING: ARCHITECTURE TESTS    ");
        Console.WriteLine("==========================================================\n");
        Console.ResetColor();

        bool allPassed = true;

        allPassed &= TestHoldAndConfirmHappyPath();
        allPassed &= await TestExtremeConcurrencyZeroDoubleBookingAsync();
        allPassed &= await TestAutomaticReservationExpirationAsync();
        allPassed &= TestIdempotencyKeyProtection();

        Console.WriteLine();
        if (allPassed)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine(">>> ALL TICKET BOOKING CONSTRAINTS & ACID LOCKS VERIFIED! <<<");
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

    private static bool TestHoldAndConfirmHappyPath()
    {
        Console.Write("[1/4] Testing Seat Hold & Checkout Flow (A1)... ");
        var service = new SeatInventoryService();

        var holdRes = service.TryHoldSeat("A1", "user_alice", 600);
        if (!holdRes.Success || holdRes.Seat?.Status != SeatStatus.Held)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("FAILED! Could not hold seat A1.");
            Console.ResetColor();
            return false;
        }

        var confirmRes = service.ConfirmBooking("A1", "user_alice", "idem_key_alice_1");
        if (!confirmRes.Success || string.IsNullOrEmpty(confirmRes.TicketNumber))
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("FAILED! Could not confirm booking.");
            Console.ResetColor();
            return false;
        }

        var finalSeat = service.GetSeat("A1");
        if (finalSeat?.Status != SeatStatus.Booked)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("FAILED! Seat status not set to Booked.");
            Console.ResetColor();
            return false;
        }

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"PASSED! (Seat held for 10m -> confirmed with ticket {confirmRes.TicketNumber})");
        Console.ResetColor();
        return true;
    }

    private static async Task<bool> TestExtremeConcurrencyZeroDoubleBookingAsync()
    {
        Console.Write("[2/4] Testing Extreme Concurrency (1,000 threads competing for seat B5)... ");
        var service = new SeatInventoryService();
        const int totalThreads = 1000;
        const string hotSeat = "B5";

        int successCount = 0;
        int failureCount = 0;

        var sw = Stopwatch.StartNew();
        var tasks = Enumerable.Range(0, totalThreads).Select(i => Task.Run(() =>
        {
            var res = service.TryHoldSeat(hotSeat, $"buyer_{i}", 600);
            if (res.Success)
            {
                Interlocked.Increment(ref successCount);
            }
            else
            {
                Interlocked.Increment(ref failureCount);
            }
        }));

        await Task.WhenAll(tasks);
        sw.Stop();

        if (successCount == 1 && failureCount == (totalThreads - 1))
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"PASSED! ({sw.ElapsedMilliseconds}ms | Exactly 1 hold granted, 999 rejected. ZERO double bookings!)");
            Console.ResetColor();
            return true;
        }

        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"FAILED! Race condition detected! Successes: {successCount}, Failures: {failureCount}");
        Console.ResetColor();
        return false;
    }

    private static async Task<bool> TestAutomaticReservationExpirationAsync()
    {
        Console.Write("[3/4] Testing Delayed Queue Auto-Release Expiration... ");
        var service = new SeatInventoryService();

        // Hold seat C3 for 1 second
        var hold = service.TryHoldSeat("C3", "user_timeout", holdSeconds: 1);
        if (!hold.Success) return false;

        // Verify currently held
        if (service.GetSeat("C3")?.Status != SeatStatus.Held) return false;

        // Wait 1.1s for expiration
        await Task.Delay(1100);

        var seatAfterTimeout = service.GetSeat("C3");
        if (seatAfterTimeout?.Status == SeatStatus.Available)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("PASSED! (Expired reservation automatically returned to available pool)");
            Console.ResetColor();
            return true;
        }

        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"FAILED! Seat status after timeout is {seatAfterTimeout?.Status}");
        Console.ResetColor();
        return false;
    }

    private static bool TestIdempotencyKeyProtection()
    {
        Console.Write("[4/4] Testing Idempotency Keys (Network Retry Deduplication)... ");
        var service = new SeatInventoryService();
        service.TryHoldSeat("D2", "user_retry", 600);

        const string idempotencyKey = "client_request_uuid_998877";

        // First click
        var res1 = service.ConfirmBooking("D2", "user_retry", idempotencyKey);
        // Duplicate click (e.g. user double clicked or network retry)
        var res2 = service.ConfirmBooking("D2", "user_retry", idempotencyKey);

        if (res1.Success && res2.Success && res1.TicketNumber == res2.TicketNumber)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"PASSED! (Duplicate payment request safely returned identical ticket {res1.TicketNumber})");
            Console.ResetColor();
            return true;
        }

        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine("FAILED! Idempotency key failed to deduplicate request.");
        Console.ResetColor();
        return false;
    }
}
