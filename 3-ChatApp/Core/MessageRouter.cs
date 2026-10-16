using System.Collections.Concurrent;

namespace ChatApp.Core;

public interface IPushNotificationService
{
    void SendOfflinePush(string recipientId, string senderId, string preview);
    IReadOnlyList<string> GetPendingPushes(string recipientId);
}

public class MockPushNotificationService : IPushNotificationService
{
    private readonly ConcurrentDictionary<string, List<string>> _notifications = new();

    public void SendOfflinePush(string recipientId, string senderId, string preview)
    {
        var list = _notifications.GetOrAdd(recipientId, _ => new List<string>());
        lock (list)
        {
            list.Add($"New message from {senderId}: {preview}");
        }
    }

    public IReadOnlyList<string> GetPendingPushes(string recipientId)
    {
        if (_notifications.TryGetValue(recipientId, out var list))
        {
            lock (list) return list.ToList();
        }
        return Array.Empty<string>();
    }
}

public class MessageRouter
{
    private readonly ISessionRegistry _sessionRegistry;
    private readonly IMessageStore _messageStore;
    private readonly IPushNotificationService _pushService;
    private static long _sequenceCounter = 1000;

    public MessageRouter(
        ISessionRegistry sessionRegistry,
        IMessageStore messageStore,
        IPushNotificationService pushService)
    {
        _sessionRegistry = sessionRegistry;
        _messageStore = messageStore;
        _pushService = pushService;
    }

    /// <summary>
    /// Generates a monotonic, lexicographically sortable Message ID (timestamp_ms + sequence).
    /// </summary>
    public static string GenerateMonotonicId()
    {
        long epochMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        long seq = Interlocked.Increment(ref _sequenceCounter);
        return $"{epochMs:D13}_{seq:D6}";
    }

    /// <summary>
    /// Generates canonical deterministic 1-on-1 conversation ID so both users query the exact same partition.
    /// </summary>
    public static string GetCanonicalConversationId(string userA, string userB)
    {
        return string.CompareOrdinal(userA, userB) < 0
            ? $"{userA}_{userB}"
            : $"{userB}_{userA}";
    }

    public async Task<string> HandleDirectMessageAsync(string senderId, WebSocketPacket packet)
    {
        if (string.IsNullOrWhiteSpace(packet.RecipientId))
            throw new ArgumentException("RecipientId is required for direct messages.");

        string messageId = GenerateMonotonicId();
        string conversationId = GetCanonicalConversationId(senderId, packet.RecipientId);
        DateTime timestamp = DateTime.UtcNow;

        var message = new ChatMessage(
            MessageId: messageId,
            ConversationId: conversationId,
            SenderId: senderId,
            RecipientId: packet.RecipientId,
            GroupId: null,
            Content: packet.Content ?? string.Empty,
            Timestamp: timestamp,
            Status: MessageStatus.Sent
        );

        // 1. Store in DB
        await _messageStore.SaveMessageAsync(message);

        // 2. Ack to Sender: Status = SENT (✓)
        var senderConn = _sessionRegistry.GetConnection(senderId);
        if (senderConn != null)
        {
            await senderConn.SendPacketAsync(new WebSocketPacket(
                Type: PacketType.MessageAck,
                ClientMsgId: packet.ClientMsgId,
                ServerMsgId: messageId,
                ConversationId: conversationId,
                Status: MessageStatus.Sent,
                Timestamp: timestamp
            ));
        }

        // 3. Deliver to Recipient if online
        var recipientConn = _sessionRegistry.GetConnection(packet.RecipientId);
        if (recipientConn != null)
        {
            await recipientConn.SendPacketAsync(new WebSocketPacket(
                Type: PacketType.SendDirectMessage,
                ServerMsgId: messageId,
                ConversationId: conversationId,
                SenderId: senderId,
                RecipientId: packet.RecipientId,
                Content: packet.Content,
                Status: MessageStatus.Sent,
                Timestamp: timestamp
            ));

            // Mark delivered (✓✓)
            await _messageStore.UpdateStatusAsync(conversationId, messageId, MessageStatus.Delivered);

            if (senderConn != null)
            {
                await senderConn.SendPacketAsync(new WebSocketPacket(
                    Type: PacketType.MessageAck,
                    ServerMsgId: messageId,
                    ConversationId: conversationId,
                    Status: MessageStatus.Delivered
                ));
            }
        }
        else
        {
            // Recipient is offline -> Dispatch Push Notification
            _pushService.SendOfflinePush(packet.RecipientId, senderId, packet.Content ?? "");
        }

        return messageId;
    }

    public async Task<string> HandleGroupMessageAsync(string senderId, WebSocketPacket packet, IReadOnlyList<string> groupMembers)
    {
        if (string.IsNullOrWhiteSpace(packet.GroupId))
            throw new ArgumentException("GroupId is required for group messages.");

        string messageId = GenerateMonotonicId();
        DateTime timestamp = DateTime.UtcNow;

        var message = new ChatMessage(
            MessageId: messageId,
            ConversationId: packet.GroupId,
            SenderId: senderId,
            RecipientId: null,
            GroupId: packet.GroupId,
            Content: packet.Content ?? string.Empty,
            Timestamp: timestamp,
            Status: MessageStatus.Sent
        );

        // 1. Store once in group partition
        await _messageStore.SaveMessageAsync(message);

        // 2. Fanout to all connected members (excluding sender)
        var outboundPacket = new WebSocketPacket(
            Type: PacketType.SendGroupMessage,
            ServerMsgId: messageId,
            ConversationId: packet.GroupId,
            GroupId: packet.GroupId,
            SenderId: senderId,
            Content: packet.Content,
            Timestamp: timestamp
        );

        foreach (var memberId in groupMembers)
        {
            if (memberId == senderId) continue;

            var conn = _sessionRegistry.GetConnection(memberId);
            if (conn != null)
            {
                await conn.SendPacketAsync(outboundPacket);
            }
            else
            {
                _pushService.SendOfflinePush(memberId, senderId, $"[Group {packet.GroupId}] {packet.Content}");
            }
        }

        return messageId;
    }

    public async Task HandleReadReceiptAsync(string readerId, string conversationId, string messageId, string originalSenderId)
    {
        await _messageStore.UpdateStatusAsync(conversationId, messageId, MessageStatus.Read);

        var senderConn = _sessionRegistry.GetConnection(originalSenderId);
        if (senderConn != null)
        {
            await senderConn.SendPacketAsync(new WebSocketPacket(
                Type: PacketType.MessageAck,
                ServerMsgId: messageId,
                ConversationId: conversationId,
                Status: MessageStatus.Read
            ));
        }
    }
}
