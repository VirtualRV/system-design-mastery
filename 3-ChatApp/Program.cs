using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using ChatApp.Core;
using ChatApp.Tests;

// CLI Test Runner Flag
if (args.Contains("--test"))
{
    int exitCode = await ChatAppTestSuite.RunAllAsync();
    Environment.Exit(exitCode);
}

var builder = WebApplication.CreateBuilder(args);

// Register Core Chat Services as Singletons
var sessionRegistry = new LocalSessionRegistry();
var messageStore = new InMemoryMessageStore();
var presenceService = new InMemoryPresenceService();
var pushService = new MockPushNotificationService();
var messageRouter = new MessageRouter(sessionRegistry, messageStore, pushService);

builder.Services.AddSingleton<ISessionRegistry>(sessionRegistry);
builder.Services.AddSingleton<IMessageStore>(messageStore);
builder.Services.AddSingleton<IPresenceService>(presenceService);
builder.Services.AddSingleton<IPushNotificationService>(pushService);
builder.Services.AddSingleton(messageRouter);

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

app.UseWebSockets(new WebSocketOptions
{
    KeepAliveInterval = TimeSpan.FromSeconds(30)
});

// 1. WebSocket Gateway Endpoint
app.Map("/ws", async (HttpContext context, ISessionRegistry sessions, IPresenceService presence, MessageRouter router) =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        await context.Response.WriteAsync("WebSocket connection expected at /ws.");
        return;
    }

    string? userId = context.Request.Query["userId"];
    if (string.IsNullOrWhiteSpace(userId))
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        await context.Response.WriteAsync("Query parameter 'userId' is required.");
        return;
    }

    using var socket = await context.WebSockets.AcceptWebSocketAsync();
    var connection = new WebSocketClientConnection(userId, socket);
    sessions.Register(userId, connection);
    presence.RecordHeartbeat(userId);

    var buffer = new byte[1024 * 4];
    try
    {
        while (socket.State == WebSocketState.Open)
        {
            var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closed by client", CancellationToken.None);
                break;
            }

            string json = Encoding.UTF8.GetString(buffer, 0, result.Count);
            var packet = JsonSerializer.Deserialize<WebSocketPacket>(json);
            if (packet == null) continue;

            presence.RecordHeartbeat(userId);

            switch (packet.Type)
            {
                case PacketType.SendDirectMessage:
                    await router.HandleDirectMessageAsync(userId, packet);
                    break;

                case PacketType.SendGroupMessage:
                    // Example default group members
                    var members = new[] { "alice", "bob", "charlie" };
                    await router.HandleGroupMessageAsync(userId, packet, members);
                    break;

                case PacketType.MessageAck when packet.Status == MessageStatus.Read &&
                                                !string.IsNullOrEmpty(packet.ConversationId) &&
                                                !string.IsNullOrEmpty(packet.ServerMsgId) &&
                                                !string.IsNullOrEmpty(packet.RecipientId):
                    await router.HandleReadReceiptAsync(userId, packet.ConversationId, packet.ServerMsgId, packet.RecipientId);
                    break;

                case PacketType.Heartbeat:
                    presence.RecordHeartbeat(userId);
                    break;
            }
        }
    }
    catch (WebSocketException)
    {
        // Abrupt disconnect (e.g. client killed)
    }
    finally
    {
        sessions.Unregister(userId);
    }
});

// 2. Message History Query (Cassandra-style paginated query)
app.MapGet("/api/history/{conversationId}", async (string conversationId, IMessageStore store) =>
{
    var history = await store.GetHistoryAsync(conversationId, limit: 50);
    return Results.Ok(new
    {
        conversationId,
        count = history.Count,
        messages = history
    });
});

// 3. Presence Status Query
app.MapGet("/api/presence/{userId}", (string userId, IPresenceService presence) =>
{
    bool isOnline = presence.IsUserOnline(userId);
    var lastSeen = presence.GetLastSeen(userId);
    return Results.Ok(new
    {
        userId,
        status = isOnline ? "ONLINE" : "OFFLINE",
        lastSeen
    });
});

app.Run();
