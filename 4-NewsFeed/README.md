# High-Performance News Feed System (.NET 10)

An implementation of a distributed News Feed architecture solving the **Celebrity Fanout Problem** via a **Hybrid Fanout Engine**, **Redis Sorted Sets (ZSET) timeline caching**, and **cursor-based pagination**.

---

## Architectural Highlights

1. **The Celebrity Hybrid Model ([FeedService.cs](file:///C:/Users/Vikas/.gemini/antigravity/scratch/NewsFeed/Core/FeedService.cs))**:
   - **Regular Users (< 25k followers)**: Fanout-on-Write (Push). Posts are eagerly appended to followers' Redis timelines for $< 10\text{ms}$ feed reads.
   - **Celebrity Users (> 25k followers)**: Fanout-on-Read (Pull). Bypasses millions of timeline writes; posts are written once to the celebrity's outbox and dynamically merged on feed read using a K-way merge.

2. **Redis Sorted Set Timeline Simulation ([TimelineStore.cs](file:///C:/Users/Vikas/.gemini/antigravity/scratch/NewsFeed/Core/TimelineStore.cs))**:
   - Stores lightweight `(PostId, Score)` pairs sorted descending by timestamp.
   - Enforces automatic timeline capacity caps (simulating `ZREMRANGEBYRANK`) to protect cluster memory.

3. **Cursor-Based Pagination**:
   - Avoids the SQL `OFFSET/LIMIT` data drift trap.
   - Uses the timestamp score of the last seen item as the cursor (`cursor={last_score}`) ensuring **zero duplicate items and zero missing items** as new posts are published.

4. **Batch Post Hydration ([PostStore.cs](file:///C:/Users/Vikas/.gemini/antigravity/scratch/NewsFeed/Core/PostStore.cs))**:
   - Timeline caches only hold IDs and scores. Full post payloads (text, author, media) are hydrated in a single bulk roundtrip (`GetPostsBatch`).

---

## How to Run

### 1. Run the Architecture Benchmark & Test Suite
```powershell
dotnet run -- --test
```

### 2. Start the HTTP API Server
```powershell
dotnet run
```

### 3. API Examples

#### Publish Post (Regular User)
```bash
curl -X POST http://localhost:5000/api/posts \
  -H "Content-Type: application/json" \
  -d '{"authorId": "bob", "content": "Just deployed the new feed service!"}'
```

#### Publish Post (Celebrity)
```bash
curl -X POST http://localhost:5000/api/posts \
  -H "Content-Type: application/json" \
  -d '{"authorId": "elon", "content": "Starship orbital flight test successful."}'
```

#### Fetch Home Feed (with Cursor)
```bash
curl "http://localhost:5000/api/feed?userId=alice&limit=10"
```
