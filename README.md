# Distributed System Design Mastery (5 Core Projects in .NET 10)

A comprehensive, production-grade repository containing **5 foundational distributed system archetypes** designed to prepare engineers for Staff/Principal-level System Design interviews and large-scale backend engineering.

Every single project includes:
1. **Architectural Deep-Dive Whitepaper**: Math, capacity sizing, CAP theorem analysis, failure modes, and trade-offs.
2. **Production-Grade .NET 10 Implementation**: Built using ASP.NET Core Minimal APIs, C# 13, and zero-allocation memory primitives.
3. **Automated Concurrency & Race-Condition Benchmark Suite**: Executable via `dotnet run -- --test`.
4. **Interactive Web UI**: Live visual interfaces served right from the browser.

---

## 🏛️ The System Archetype Matrix

| # | System | Core Distributed Challenge | Key Architecture Patterns | Live UI Features |
| :- | :--- | :--- | :--- | :--- |
| **1** | **[URL Shortener](1-UrlShortener)** | Read-Heavy Key-Value Store | Base62 Key Generation Service (KGS), In-Memory Bloom Filter, HTTP 307 semantics, Async click analytics | URL Shortener input, 307 redirect button, click & referrer counter, Bloom filter 404 test |
| **2** | **[Distributed Rate Limiter](2-RateLimiter)** | Low-Latency Distributed Middleware | Token Bucket, Sliding Window Counter, Atomic Concurrency Locks, IETF Headers (`HTTP 429`) | Live Token Bucket gauge with animated token replenishment, burst test button, live header inspector |
| **3** | **[Real-Time Chat App](3-ChatApp)** | Persistent Bi-Directional Messaging | Full-Duplex WebSockets, Session Registry, LSM Wide-Column Storage (Cassandra pattern), Monotonic IDs | Dual-client interactive chat simulator (Alice & Bob), real-time delivery checks (✓, ✓✓, blue ✓✓), presence dots |
| **4** | **[News Feed System](4-NewsFeed)** | High-Fanout Content Aggregation | Hybrid Fanout (Push for regular users, Pull for celebrities), Redis Sorted Sets (`ZSET`), Cursor pagination | Post publisher with regular vs celebrity toggle, Alice's live stream with Push/Pull badges, infinite scroll |
| **5** | **[Ticket Booking System](5-TicketBooking)** | High-Contention Transactional ACID | 3 Concurrency Gates, In-Memory Distributed Lock, 10-Minute Hold Reservation, Idempotency | Interactive 50-seat stadium map (Available, Held, Sold), 10-min countdown timer, "Simulate 50 Bots" button |

---

## 🚀 Quick Start Guide

### Build the entire solution:
```powershell
dotnet build SystemDesignMastery.sln
```

---

## 🧪 Running the Architecture Test Suites

Each project features an automated verification suite that validates core distributed systems constraints (race condition prevention, zero double-booking, zero token leakage, and k-way merges):

```powershell
# 1. URL Shortener (Zero-collision KGS & Bloom filter)
cd 1-UrlShortener && dotnet run -- --test

# 2. Rate Limiter (1,000 threads vs Cap: 50 zero leakage)
cd ../2-RateLimiter && dotnet run -- --test

# 3. Chat Application (1-on-1, group fanout, presence, 166k msgs/sec)
cd ../3-ChatApp && dotnet run -- --test

# 4. News Feed (Hybrid fanout & cursor pagination)
cd ../4-NewsFeed && dotnet run -- --test

# 5. Ticket Booking (1,000 threads competing for 1 seat with 0 double-bookings)
cd ../5-TicketBooking && dotnet run -- --test
```

---

## 🖥️ Running the Interactive Web UIs

To launch any project's interactive web interface, navigate to its folder and start the server:

```powershell
cd 1-UrlShortener && dotnet run
# Open browser at: http://localhost:5149

cd 2-RateLimiter && dotnet run
# Open browser at: http://localhost:5092

cd 3-ChatApp && dotnet run
# Open browser at: http://localhost:5052

cd 4-NewsFeed && dotnet run
# Open browser at: http://localhost:5176

cd 5-TicketBooking && dotnet run
# Open browser at: http://localhost:5000
```

---

## 📚 Deep-Dive Documentation

Detailed Staff-level whitepapers for each project can be found in the [`docs/`](docs/) directory:
- [URL Shortener Architecture Deep-Dive](docs/url_shortener_architecture_masterclass.md)
- [Distributed Rate Limiter Architecture Deep-Dive](docs/rate_limiter_architecture_masterclass.md)
- [Real-Time Chat App Architecture Deep-Dive](docs/chat_app_architecture_masterclass.md)
- [News Feed System Architecture Deep-Dive](docs/news_feed_architecture_masterclass.md)
- [High-Concurrency Ticket Booking Architecture Deep-Dive](docs/ticket_booking_architecture_masterclass.md)
