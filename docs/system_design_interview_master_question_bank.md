# System Design Master Question Bank: The 5 Foundational Projects

In FAANG/Tier-1 interviews, interviewers do not just ask you to draw boxes. They probe with **7 distinct question archetypes**:
1. **Scoping & Requirement Curveballs**
2. **Back-of-the-Envelope Capacity Math**
3. **Data Modeling & Storage Engine Selection**
4. **Distributed Concurrency & Race Conditions**
5. **Network Protocols & HTTP Semantics**
6. **Failure Modes, Split-Brain & Self-Healing**
7. **Abuse, Security & Rate Limiting**

Below is the complete question bank categorized by project, complete with the **Junior Trap**, the **Staff-Level Model Answer**, and the **Follow-Up Curveball**.

---

## 🔗 Project 1: URL Shortener (TinyURL / Bitly)

### Q1: "Why not just hash the URL with MD5 or SHA-256 and take the first 7 characters?"
- **Junior Trap**: *"Hashing is fast and deterministic, so it's a great choice."*
- **The Problem**: Due to the **Birthday Paradox**, collision probability exceeds 50% after hashing just $\sqrt{62^7} \approx 1.8 \text{ Million}$ URLs! You must query the database on every write to check if the hash is taken, append salt, and re-hash. Under high write load, this creates massive DB contention.
- **Staff-Level Answer**:
  > *"Instead of hashing, I propose a **Range-Based Key Generation Service (KGS)**. A coordinator (ZooKeeper/Etcd) leases contiguous blocks of 1,000,000 integers to stateless app workers. Each worker increments locally in memory using `Interlocked.Increment` and encodes the integer into Base62. This gives $O(1)$ lockless generation, zero database round-trips, and mathematically guarantees zero collisions across 3.5 Trillion keys with 7 characters ($62^7$)."*
- **Follow-up Curveball**: *"What if an app worker crashes with 500,000 unused keys in its leased block?"*
  - **Answer**: *"We abandon that block and let the new worker lease the next range. With $3.5 \text{ Trillion}$ keys, losing a few million keys over years is less than $0.001\%$ of total capacity and completely negligible compared to the latency savings."*

### Q2: "What HTTP status code should you return on redirection: 301, 302, or 307?"
- **Junior Trap**: *"301 Moved Permanently is standard."*
- **The Problem**: A 301 is permanently cached in the user's browser. Subsequent clicks will never hit your servers again, blinding your analytics and rendering link expiration or custom editing impossible.
- **Staff-Level Answer**:
  > *"I use **HTTP 307 (Temporary Redirect)**. Unlike 301, it is not cached permanently by default, guaranteeing every click reaches our edge servers for real-time telemetry, referrer tracking, and geo-location analytics. Furthermore, unlike legacy 302, 307 strictly preserves the HTTP request method."*

### Q3: "How do you protect the service from Cache Penetration attacks?"
- **Junior Trap**: *"I will increase the Redis cache size."*
- **The Problem**: If an attacker sends millions of requests for non-existent keys (`/fake123`, `/bogus999`), none of them exist in Redis. Every single request bypasses the cache and hammers the database.
- **Staff-Level Answer**:
  > *"I implement an in-memory **Bloom Filter** at the API Gateway before touching the cache or database. If the Bloom filter returns `false`, the key definitely does not exist in the database, and we return `404 Not Found` immediately with 0ms DB load. If it returns `true`, we proceed to Cache $\rightarrow$ DB."*

### Q4: "How do you handle expired URLs without running continuous heavy database deletes?"
- **Staff-Level Answer**:
  > *"We combine **Passive Deletion** with **Active Sweeping**:
  > 1. **Passive**: When a user visits a link, we check `expires_at < NOW()`. If expired, we return `410 Gone` and enqueue an async background deletion.
  > 2. **Active**: A low-priority background worker runs during off-peak hours using indexed range scans on `expires_at` to purge expired records in batches."*

---

## ⚡ Project 2: Distributed Rate Limiter

### Q1: "Why is Fixed Window Counter considered dangerous for critical APIs?"
- **Junior Trap**: *"Fixed window is easy and uses almost no memory, so it's fine."*
- **Staff-Level Answer**:
  > *"Fixed Window suffers from the **2x Boundary Burst flaw**. If the limit is 100 req/min, a client can send 100 requests at 00:59 (end of window 1) and another 100 requests at 01:00 (start of window 2). The system processes 200 requests within a 2-second window, which can take down downstream microservices. I recommend **Token Bucket** (allows controlled bursts up to bucket capacity) or **Sliding Window Counter** (smooth rolling boundary)."*

### Q2: "In a cluster of 50 API Gateways, how do you prevent race conditions when checking rate limits?"
- **Junior Trap**: *"Each gateway calls Redis `GET tokens`, decrements in code, and calls `SET tokens`."*
- **The Problem**: Two simultaneous requests to different gateway nodes will both read 1 token remaining, decrement to 0, and allow both through.
- **Staff-Level Answer**:
  > *"To ensure atomic concurrency across distributed nodes, all token calculation and decrement logic must run inside Redis via an **atomic Lua script**. Because Redis executes Lua scripts as a single atomic transaction without interleaving, race conditions are completely eliminated. Alternatively, we use the Redis Cell module (`CL.THROTTLE`)."*

### Q3: "Calling Redis over the network on every HTTP request adds 1–2ms. How do you scale to 500,000 req/sec?"
- **Staff-Level Answer**:
  > *"We use **Local Memory Token Batching**:
  > 1. Instead of requesting 1 token from Redis per HTTP request, each API Gateway node leases a batch of 50 tokens from Redis in one network call.
  > 2. The gateway decrements tokens in local RAM using atomic CPU instructions (`Interlocked.Decrement`) in $< 1\mu\text{s}$.
  > 3. Only when local tokens hit 0 does it request the next batch from Redis. This cuts Redis network calls by $50\times$."*

### Q4: "What happens if the Redis Rate Limiter cluster fails or experiences a network partition?"
- **Staff-Level Answer**:
  > *"We implement a **Fail-Open Strategy with Circuit Breaking**. If Redis is unreachable, the rate limiter falls back to local in-memory heuristic limits or allows traffic through while firing high-priority alerts to SREs. We fail-open because shutting down the entire platform for paying customers is worse than temporary degraded throttling."*

---

## 💬 Project 3: Real-Time Chat Application (WhatsApp / Slack)

### Q1: "Why WebSockets over HTTP Long-Polling or Server-Sent Events (SSE)?"
- **Staff-Level Answer**:
  > *"1. **HTTP Long-Polling**: Terrible overhead. Every poll opens/closes TCP sockets and sends heavy HTTP headers (cookies, user-agents), wasting massive bandwidth.
  > 2. **Server-Sent Events (SSE)**: SSE is half-duplex (server-to-client only). For the client to send a message, it must issue a separate HTTP POST, doubling connection overhead.
  > 3. **WebSockets**: Persistent, full-duplex TCP connection. After the initial HTTP handshake, frame overhead is just **2 bytes**, delivering sub-10ms bi-directional messaging."*

### Q2: "How do you prevent servers from running out of memory with 10 Million concurrent WebSockets?"
- **Staff-Level Answer**:
  > *"1. **Kernel Tuning**: Tune Linux TCP buffers (`net.ipv4.tcp_rmem` and `tcp_wmem`) from default 128KB down to 4KB per idle socket.
  > 2. **Ephemeral Gateways**: Keep WebSocket Gateways strictly stateless with respect to business logic. The gateway only holds the socket and routes bytes.
  > 3. **Math**: $10\text{M} \times 6\text{ KB} \approx 60\text{ GB of RAM}$. A cluster of 15 nodes with 32 GB RAM each can comfortably hold 10M concurrent connections."*

### Q3: "Server clocks drift by milliseconds (NTP skew). How do you guarantee message ordering?"
- **Staff-Level Answer**:
  > *"Never use server wall clocks (`DateTime.UtcNow`) as the primary ordering key. I use **ULIDs or Snowflake IDs** that combine 48 bits of epoch milliseconds with an atomic monotonic sequence counter. Within each conversation partition, messages are indexed by `(conversation_id, message_id DESC)`. This guarantees total chronological ordering on all devices."*

### Q4: "How do you handle group chats with 50,000 members (Discord/Slack) vs small groups (WhatsApp 50 members)?"
- **Staff-Level Answer**:
  > *"We use two different fanout models based on group size:
  > - **Small Groups (< 256 members)**: **Fanout-on-Write**. The message is duplicated into each member's private inbox. Fast reads on client devices.
  > - **Large Channels (50,000+ members)**: **Channel-Topic Pub/Sub**. The message is stored **once** in the channel timeline. Active members subscribe to the channel's message topic. Zero duplicate storage."*

### Q5: "How does the Presence Service detect offline users if someone enters a tunnel or drops signal?"
- **Staff-Level Answer**:
  > *"We use **Sliding Heartbeats in Redis**:
  > 1. The client sends a heartbeat ping over the WebSocket every 30 seconds.
  > 2. Presence service updates Redis with a TTL: `SET user:id:presence "ONLINE" EX 45`.
  > 3. If the user disconnects abruptly without sending a disconnect packet, the Redis key expires after 45 seconds, automatically switching status to `OFFLINE` without network flooding."*

---

## 📰 Project 4: News Feed System (Twitter / Instagram)

### Q1: "Explain why neither Fanout-on-Write nor Fanout-on-Read works by itself at scale."
- **Staff-Level Answer**:
  > *"1. **Pure Fanout-on-Write (Push)**: When an author posts, we write to all followers' timelines. If a celebrity with 80M followers posts, that single post generates **80 Million Redis writes**. The write queue clogs for 20 minutes, delaying feeds for the entire platform.
  > 2. **Pure Fanout-on-Read (Pull)**: When a user opens their feed, we query all people they follow and merge posts in SQL. If a user follows 1,000 accounts, merging 10,000 rows on every page refresh crashes the database and violates the $< 200\text{ms}$ read SLA."*

### Q2: "How does the Hybrid Fanout Model solve the Celebrity Problem?"
- **Staff-Level Answer**:
  > *"We split users by follower count threshold (e.g. 25,000 followers):
  > - **Regular Users**: Use **Fanout-on-Write**. Their posts are eagerly pushed into their few followers' precomputed Redis timelines (`ZSET`).
  > - **Celebrities**: Use **Fanout-on-Read**. When they post, we **skip** follower fanout entirely and write only to the celebrity's outbox.
  > - **On Feed Read**: The user's feed service fetches their precomputed Redis timeline, fetches recent posts from followed celebrities, and performs a fast, in-memory **K-way merge**."*

### Q3: "Why is SQL `OFFSET 1000 LIMIT 10` forbidden for news feeds?"
- **Staff-Level Answer**:
  > *"1. **Data Drift & Duplicates**: If a user is viewing page 1 and 3 new posts are published, page 2 (`OFFSET 10`) shifts indices, showing duplicate posts the user already saw.
  > 2. **Performance**: In SQL, `OFFSET 100000` requires the database engine to scan and discard 100,000 rows before returning 10.
  > 3. **The Solution**: **Seek-based Cursor Pagination** (`WHERE post_id < :cursor ORDER BY post_id DESC LIMIT 10`). In Redis: `ZREVRANGEBYSCORE timeline (cursor_score -inf LIMIT 0 10`. It is strictly $O(\log N)$ and immune to data drift."*

### Q4: "How do you prevent Redis timeline memory from ballooning to petabytes?"
- **Staff-Level Answer**:
  > *"1. **Timeline Trimming**: We only cache the latest **800 post IDs** per active user using `ZREMRANGEBYRANK`.
  > 2. **ID-Only Caching**: Redis only stores lightweight `(post_id, score)` tuples (~32 bytes per entry). Full post text, media, and author metadata are hydrated in a single batch query (`GetPostsBatch`).
  > 3. **Active User TTL**: We only generate timelines for users who logged in within the last 7 days. Inactive users are evicted to cold storage."*

---

## 🎟️ Project 5: High-Concurrency Ticket Booking (Ticketmaster)

### Q1: "Why can't you use `SELECT FOR UPDATE` in PostgreSQL to lock seats during a flash sale?"
- **Junior Trap**: *"I will wrap the checkout in a transaction and lock the row using `SELECT FOR UPDATE`."*
- **The Problem**: When 500,000 concurrent buyers rush for 50,000 seats at 10:00:00 AM, 500,000 database connections attempt to acquire row-level locks. This causes massive lock contention, deadlocks, connection pool exhaustion, and completely crashes the database within 5 seconds.
- **Staff-Level Answer**:
  > *"We must never expose the database directly to flash sale traffic. I implement the **3 Concurrency Gates Architecture**:
  > 1. **Gate 1: Edge Waiting Room**: Cloudflare / Leaky Bucket queue admits users at a steady rate matching backend capacity.
  > 2. **Gate 2: In-Memory Distributed Lock (Redis)**: We atomically check and hold the seat in Redis via Lua scripts in $< 1\text{ms}$. If another user holds the seat, we return an immediate 409 Conflict without touching the DB.
  > 3. **Gate 3: Relational DB (ACID)**: Only the single winning buyer proceeds to checkout and commits to PostgreSQL using optimistic concurrency versioning (`WHERE version = 1`)."*

### Q2: "How do you automatically release seats after 10 minutes without running continuous heavy database polling queries?"
- **Junior Trap**: *"A cron job runs `SELECT * FROM seats WHERE status = 'HELD' AND held_until < NOW()` every second."*
- **The Problem**: Constant table scans create severe disk I/O and lock contention on the primary seats table.
- **Staff-Level Answer**:
  > *"We use **Delayed Message Queues (Dead Letter Exchanges / Redis Keyspace Notifications)**:
  > When a seat is held at timestamp $T$, the booking service publishes a message to RabbitMQ with a 10-minute TTL. When the message matures at $T+10\text{min}$, a worker checks if payment completed. If still `HELD`, it flips the seat back to `AVAILABLE` and broadcasts a WebSocket update to clients. Zero database polling."*

### Q3: "How do you handle payment gateway idempotency if the user double-clicks 'Pay' or the network drops?"
- **Staff-Level Answer**:
  > *"The client generates a unique UUID **`Idempotency-Key`** on checkout initiation.
  > 1. The server acquires an atomic lock in Redis: `SETNX idempotency:<key> "PROCESSING" EX 120`.
  > 2. If the key already exists, concurrent or duplicate requests are rejected or wait for the initial transaction result.
  > 3. Once payment succeeds, the server caches the confirmed ticket number under that key for 24 hours. Any retry safely returns the exact same ticket without charging the credit card twice."*

---

## 🏆 Universal System Design Behavioral & Leadership Questions

### Q1: "How do you handle trade-offs between Consistency and Availability (CAP Theorem) across these systems?"
- **Answer**:
  - *"In **URL Shortener** and **News Feed**, we prioritize **Availability (AP)**: Eventual consistency of 50–200ms across global read replicas is completely acceptable.
  - In **Ticket Booking**, we prioritize **Strict Consistency (CP)**: We must sacrifice availability before allowing inconsistent double bookings.
  - In **Chat App**, we tune consistency: **AP for delivery** (offline queuing), but **CP for conversation ordering** (messages must never appear out of sequence)."*

### Q2: "What is your observability and telemetry strategy for these distributed systems?"
- **Answer**:
  - *"I instrument using the **USE Method** (Utilization, Saturation, Errors) for infrastructure and the **RED Method** (Rate, Errors, Duration) for services:
    1. **Metrics**: Prometheus + Grafana tracking P95/P99 latency, rate limiter rejection rates, and Redis memory saturation.
    2. **Tracing**: OpenTelemetry distributed tracing with correlation IDs propagated across HTTP/WebSocket headers to trace request bottlenecks across microservices.
    3. **Alerting**: Alert on Error budget burn rates (e.g. 429 spike indicating DDoS, or DB connection pool saturation > 85%)."*
