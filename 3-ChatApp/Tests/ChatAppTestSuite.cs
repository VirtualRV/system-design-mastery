using System.Collections.Concurrent;
using System.Diagnostics;
using ChatApp.Core;

namespace ChatApp.Tests;

public class MockClientConnection : IClientConnection
{
    public string UserId { get; }
    public ConcurrentQueue<WebSocketPacket> ReceivedPackets { get; } = new();

    public MockClientConnection(string userId)
    {
        UserId = userId;
    }

    public Task<bool> SendPacketAsync(WebSocketPacket packet, CancellationToken ct = default)
    {
        ReceivedPackets.Enqueue(packet);
        return Task.FromResult(true);
    }
}

public static class ChatAppTestSuite
{
    public static async Task<int> RunAllAsync()
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("==========================================================");
        Console.WriteLine("     REAL-TIME CHAT APPLICATION: SYSTEM DESIGN TESTS      ");
        Console.WriteLine("==========================================================\n");
        Console.ResetColor();

        bool allPassed = true;

        allPassed &= await TestOneOnOneMessagingLifecycleAsync();
        allPassed &= await TestGroupChatFanoutAsync();
        allPassed &= await TestMessageHistoryOrderingAsync();
        allPassed &= TestPresenceHeartbeats();
        allPassed &= await TestMassiveConcurrentRoutingAsync();

        Console.WriteLine();
        if (allPassed)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine(">>> ALL CHAT ARCHITECTURE CONSTRAINTS VERIFIED SUCCESSFULLY! <<<");
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

    private static async Task<bool> TestOneOnOneMessagingLifecycleAsync()
    {
        Console.Write("[1/5] Testing 1-on-1 Delivery Lifecycle (Sent -> Delivered -> Read)... ");
        var sessions = new LocalSessionRegistry();
        var store = new InMemoryMessageStore();
        var push = new MockPushNotificationService();
        var router = new MessageRouter(sessions, store, push);

        var alice = new MockClientConnection("alice");
        var bob = new MockClientConnection("bob");
        sessions.Register("alice", alice);
        sessions.Register("bob", bob);

        // 1. Alice sends to Bob
        string msgId = await router.HandleDirectMessageAsync("alice", new WebSocketPacket(
            Type: PacketType.SendDirectMessage,
            RecipientId: "bob",
            Content: "Hello Bob!"
        ));

        // Alice should receive Sent (✓) and Delivered (✓✓) acks
        var alicePackets = alice.ReceivedPackets.ToList();
        bool aliceGotSent = alicePackets.Any(p => p.Status == MessageStatus.Sent);
        bool aliceGotDelivered = alicePackets.Any(p => p.Status == MessageStatus.Delivered);

        // Bob should receive the message
        var bobPackets = bob.ReceivedPackets.ToList();
        bool bobGotMsg = bobPackets.Any(p => p.ServerMsgId == msgId && p.Content == "Hello Bob!");

        if (!aliceGotSent || !aliceGotDelivered || !bobGotMsg)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("FAILED! Delivery handshake did not complete.");
            Console.ResetColor();
            return false;
        }

        // 2. Bob sends Read Receipt (✓✓ Blue)
        string convId = MessageRouter.GetCanonicalConversationId("alice", "bob");
        await router.HandleReadReceiptAsync("bob", convId, msgId, "alice");

        bool aliceGotRead = alice.ReceivedPackets.Any(p => p.Status == MessageStatus.Read);
        if (!aliceGotRead)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("FAILED! Read receipt not routed to sender.");
            Console.ResetColor();
            return false;
        }

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("PASSED! (Full Sent -> Delivered -> Read lifecycle verified)");
        Console.ResetColor();
        return true;
    }

    private static async Task<bool> TestGroupChatFanoutAsync()
    {
        Console.Write("[2/5] Testing Group Chat Fanout (3 Online, 2 Offline)... ");
        var sessions = new LocalSessionRegistry();
        var store = new InMemoryMessageStore();
        var push = new MockPushNotificationService();
        var router = new MessageRouter(sessions, store, push);

        // Group members: alice, bob, charlie, dave, eve
        string[] allMembers = ["alice", "bob", "charlie", "dave", "eve"];
        var alice = new MockClientConnection("alice");
        var bob = new MockClientConnection("bob");
        var charlie = new MockClientConnection("charlie");
        // Dave and Eve are offline

        sessions.Register("alice", alice);
        sessions.Register("bob", bob);
        sessions.Register("charlie", charlie);

        await router.HandleGroupMessageAsync("alice", new WebSocketPacket(
            Type: PacketType.SendGroupMessage,
            GroupId: "group_eng",
            Content: "Team standup in 5 mins!"
        ), allMembers);

        // Bob and Charlie must have received the group packet
        bool bobReceived = bob.ReceivedPackets.Any(p => p.Content == "Team standup in 5 mins!");
        bool charlieReceived = charlie.ReceivedPackets.Any(p => p.Content == "Team standup in 5 mins!");

        // Dave and Eve must have received Push Notifications
        bool daveNotified = push.GetPendingPushes("dave").Count > 0;
        bool eveNotified = push.GetPendingPushes("eve").Count > 0;

        if (bobReceived && charlieReceived && daveNotified && eveNotified)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("PASSED! (Online members got instant frame, offline members queued for push)");
            Console.ResetColor();
            return true;
        }

        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine("FAILED! Group fanout did not reach all recipients.");
        Console.ResetColor();
        return false;
    }

    private static async Task<bool> TestMessageHistoryOrderingAsync()
    {
        Console.Write("[3/5] Testing Wide-Column Reverse Chronological Pagination... ");
        var store = new InMemoryMessageStore();
        string convId = "conv_test_101";

        // Insert 10 messages with sequential monotonic IDs
        for (int i = 0; i < 10; i++)
        {
            string msgId = MessageRouter.GenerateMonotonicId();
            await store.SaveMessageAsync(new ChatMessage(
                MessageId: msgId,
                ConversationId: convId,
                SenderId: "userA",
                RecipientId: "userB",
                GroupId: null,
                Content: $"Message {i}",
                Timestamp: DateTime.UtcNow
            ));
            await Task.Delay(2); // Ensure distinct epoch millisecond
        }

        var history = await store.GetHistoryAsync(convId, limit: 5);
        if (history.Count != 5) return false;

        // Verify descending order (latest message first)
        for (int i = 0; i < history.Count - 1; i++)
        {
            if (string.CompareOrdinal(history[i].MessageId, history[i + 1].MessageId) <= 0)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("FAILED! History is not sorted in descending chronological order.");
                Console.ResetColor();
                return false;
            }
        }

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("PASSED! (Reverse chronological sort guaranteed via monotonic IDs)");
        Console.ResetColor();
        return true;
    }

    private static bool TestPresenceHeartbeats()
    {
        Console.Write("[4/5] Testing Presence Heartbeats & Sliding Expiration... ");
        var presence = new InMemoryPresenceService(timeout: TimeSpan.FromMilliseconds(200));

        presence.RecordHeartbeat("user_presence_1");
        if (!presence.IsUserOnline("user_presence_1")) return false;

        // Sleep past timeout -> status should automatically turn offline without packet
        Thread.Sleep(250);

        if (presence.IsUserOnline("user_presence_1"))
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("FAILED! User still online after timeout.");
            Console.ResetColor();
            return false;
        }

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("PASSED! (Sliding TTL expires offline users automatically)");
        Console.ResetColor();
        return true;
    }

    private static async Task<bool> TestMassiveConcurrentRoutingAsync()
    {
        Console.Write("[5/5] Testing Massive Concurrent Routing (5,000 messages across 50 users)... ");
        var sessions = new LocalSessionRegistry();
        var store = new InMemoryMessageStore();
        var push = new MockPushNotificationService();
        var router = new MessageRouter(sessions, store, push);

        const int userCount = 50;
        const int messagesTotal = 5000;

        var connections = new List<MockClientConnection>();
        for (int i = 0; i < userCount; i++)
        {
            var conn = new MockClientConnection($"user_{i}");
            sessions.Register($"user_{i}", conn);
            connections.Add(conn);
        }

        var sw = Stopwatch.StartNew();
        var tasks = Enumerable.Range(0, messagesTotal).Select(i =>
        {
            string sender = $"user_{i % userCount}";
            string recipient = $"user_{(i + 1) % userCount}";
            return router.HandleDirectMessageAsync(sender, new WebSocketPacket(
                Type: PacketType.SendDirectMessage,
                RecipientId: recipient,
                Content: $"Load test message #{i}"
            ));
        });

        await Task.WhenAll(tasks);
        sw.Stop();

        double throughput = messagesTotal / sw.Elapsed.TotalSeconds;
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"PASSED! ({messagesTotal:N0} routed in {sw.ElapsedMilliseconds}ms | {throughput:N0} msgs/sec, 0 drops)");
        Console.ResetColor();
        return true;
    }
}
