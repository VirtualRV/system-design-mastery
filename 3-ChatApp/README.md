# Real-Time Distributed Chat Application (.NET 10)

A distributed real-time chat architecture implementing **WebSockets**, a **Session Registry**, **1-on-1 and Group Fanout**, **LSM-tree chronological storage (Cassandra/ScyllaDB pattern)**, and **heartbeat presence tracking**.

---

## Architectural Highlights

1. **Monotonic Message Sequencing ([MessageRouter.cs](file:///C:/Users/Vikas/.gemini/antigravity/scratch/ChatApp/Core/MessageRouter.cs))**:
   - Generates lexicographically sortable message IDs combining 64-bit epoch milliseconds with a monotonic sequence counter (`timestamp_ms + sequence`).
   - Completely eliminates NTP clock skew bugs where messages arrive out of order across servers.

2. **Handshake Delivery Lifecycle ([Models.cs](file:///C:/Users/Vikas/.gemini/antigravity/scratch/ChatApp/Core/Models.cs))**:
   - **Sent ($\checkmark$)**: Stored in database, acknowledged to sender.
   - **Delivered ($\checkmark\checkmark$)**: Emitted down recipient's active socket.
   - **Read ($\color{blue}\checkmark\checkmark$)**: Read acknowledgment routed back to sender.

3. **Hybrid Fanout Engine**:
   - 1-on-1 routing routes directly to the recipient's connection via the session registry.
   - Group messages write once to the group partition and fan out to all online connected members, routing offline members to the push notification queue.

4. **Cassandra/ScyllaDB Wide-Column Data Model ([MessageStore.cs](file:///C:/Users/Vikas/.gemini/antigravity/scratch/ChatApp/Core/MessageStore.cs))**:
   - Partition Key: `(conversation_id)`
   - Clustering Key: `message_id DESC`
   - Reverse chronological timeline pagination executes in contiguous sequential memory.

5. **Heartbeat Presence ([PresenceService.cs](file:///C:/Users/Vikas/.gemini/antigravity/scratch/ChatApp/Core/PresenceService.cs))**:
   - Clients send heartbeats every 30 seconds.
   - Sliding TTL automatically marks users `OFFLINE` if no heartbeat is received within the timeout window without needing an explicit disconnect packet.

---

## How to Run

### 1. Run the Architecture Benchmark & Test Suite
```powershell
dotnet run -- --test
```

### 2. Start the WebSocket Server
```powershell
dotnet run
```

### 3. Connect via WebSocket
Connect any WebSocket client (e.g., Postman, browser, or wscat) to:
```text
ws://localhost:5000/ws?userId=alice
```
