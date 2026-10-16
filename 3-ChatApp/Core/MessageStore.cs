using System.Collections.Concurrent;

namespace ChatApp.Core;

public interface IMessageStore
{
    Task SaveMessageAsync(ChatMessage message);
    Task<bool> UpdateStatusAsync(string conversationId, string messageId, MessageStatus status);
    Task<IReadOnlyList<ChatMessage>> GetHistoryAsync(string conversationId, int limit = 50);
}

/// <summary>
/// In-memory implementation of a Cassandra/ScyllaDB wide-column table:
/// PRIMARY KEY ((conversation_id), message_id) WITH CLUSTERING ORDER BY (message_id DESC)
/// </summary>
public class InMemoryMessageStore : IMessageStore
{
    // Partition key: conversation_id -> Clustering key: message_id (SortedDictionary descending)
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, ChatMessage>> _partitions = new();

    public Task SaveMessageAsync(ChatMessage message)
    {
        var partition = _partitions.GetOrAdd(message.ConversationId, _ => new ConcurrentDictionary<string, ChatMessage>());
        partition[message.MessageId] = message;
        return Task.CompletedTask;
    }

    public Task<bool> UpdateStatusAsync(string conversationId, string messageId, MessageStatus status)
    {
        if (_partitions.TryGetValue(conversationId, out var partition) &&
            partition.TryGetValue(messageId, out var existing))
        {
            partition[messageId] = existing with { Status = status };
            return Task.FromResult(true);
        }
        return Task.FromResult(false);
    }

    public Task<IReadOnlyList<ChatMessage>> GetHistoryAsync(string conversationId, int limit = 50)
    {
        if (!_partitions.TryGetValue(conversationId, out var partition))
        {
            return Task.FromResult<IReadOnlyList<ChatMessage>>(Array.Empty<ChatMessage>());
        }

        // Return sorted by MessageId (monotonic ULID timestamp) descending
        var history = partition.Values
            .OrderByDescending(m => m.MessageId)
            .Take(limit)
            .ToList();

        return Task.FromResult<IReadOnlyList<ChatMessage>>(history);
    }
}
