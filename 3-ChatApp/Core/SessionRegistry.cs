using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace ChatApp.Core;

public interface IClientConnection
{
    string UserId { get; }
    Task<bool> SendPacketAsync(WebSocketPacket packet, CancellationToken ct = default);
}

public class WebSocketClientConnection : IClientConnection
{
    public string UserId { get; }
    public WebSocket Socket { get; }
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    public WebSocketClientConnection(string userId, WebSocket socket)
    {
        UserId = userId;
        Socket = socket;
    }

    public async Task<bool> SendPacketAsync(WebSocketPacket packet, CancellationToken ct = default)
    {
        if (Socket.State != WebSocketState.Open) return false;

        string json = JsonSerializer.Serialize(packet);
        byte[] bytes = Encoding.UTF8.GetBytes(json);

        await _sendLock.WaitAsync(ct);
        try
        {
            if (Socket.State == WebSocketState.Open)
            {
                await Socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct);
                return true;
            }
            return false;
        }
        catch
        {
            return false;
        }
        finally
        {
            _sendLock.Release();
        }
    }
}

public interface ISessionRegistry
{
    void Register(string userId, IClientConnection connection);
    void Unregister(string userId);
    IClientConnection? GetConnection(string userId);
    bool IsOnline(string userId);
}

public class LocalSessionRegistry : ISessionRegistry
{
    private readonly ConcurrentDictionary<string, IClientConnection> _sessions = new();

    public void Register(string userId, IClientConnection connection)
    {
        _sessions[userId] = connection;
    }

    public void Unregister(string userId)
    {
        _sessions.TryRemove(userId, out _);
    }

    public IClientConnection? GetConnection(string userId)
    {
        _sessions.TryGetValue(userId, out var conn);
        return conn;
    }

    public bool IsOnline(string userId)
    {
        return _sessions.ContainsKey(userId);
    }
}
