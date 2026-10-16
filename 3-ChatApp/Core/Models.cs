using System.Text.Json.Serialization;

namespace ChatApp.Core;

public enum MessageStatus
{
    Sent = 1,       // Received and stored by server (✓)
    Delivered = 2,  // Delivered to recipient's active socket (✓✓)
    Read = 3        // Read by recipient (✓✓ blue)
}

public record ChatMessage(
    string MessageId,
    string ConversationId,
    string SenderId,
    string? RecipientId,
    string? GroupId,
    string Content,
    DateTime Timestamp,
    MessageStatus Status = MessageStatus.Sent
);

public enum PacketType
{
    SendDirectMessage,
    SendGroupMessage,
    MessageAck,
    Heartbeat,
    PresenceUpdate,
    TypingIndicator
}

public record WebSocketPacket(
    [property: JsonPropertyName("type")] PacketType Type,
    [property: JsonPropertyName("clientMsgId")] string? ClientMsgId = null,
    [property: JsonPropertyName("serverMsgId")] string? ServerMsgId = null,
    [property: JsonPropertyName("conversationId")] string? ConversationId = null,
    [property: JsonPropertyName("recipientId")] string? RecipientId = null,
    [property: JsonPropertyName("groupId")] string? GroupId = null,
    [property: JsonPropertyName("content")] string? Content = null,
    [property: JsonPropertyName("status")] MessageStatus? Status = null,
    [property: JsonPropertyName("senderId")] string? SenderId = null,
    [property: JsonPropertyName("timestamp")] DateTime? Timestamp = null
);
