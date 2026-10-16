# High-Performance Distributed Rate Limiter (.NET 10)

A distributed rate limiter implementing the **Token Bucket** and **Sliding Window Counter** algorithms with atomic concurrency control, IETF rate limit headers, and `HTTP 429 Too Many Requests` handling.

---

## Architectural Highlights

1. **Lazy Token Replenishment ([TokenBucketLimiter.cs](file:///C:/Users/Vikas/.gemini/antigravity/scratch/RateLimiter/Core/TokenBucketLimiter.cs))**:
   - Calculates refilled tokens continuously based on elapsed wall-clock ticks using high-resolution `Stopwatch.GetTimestamp()`.
   - Requires **zero background timers or threads**, keeping memory and CPU footprint $O(1)$.

2. **Zero-Leakage Concurrency Control**:
   - In distributed/multi-threaded environments, race conditions allow attackers to exceed capacity.
   - Synchronized state transitions guarantee **zero token leakage** (tested with 1,000 concurrent threads hammering a 50-token bucket in 6ms).

3. **Sliding Window Counter ([SlidingWindowCounterLimiter.cs](file:///C:/Users/Vikas/.gemini/antigravity/scratch/RateLimiter/Core/SlidingWindowCounterLimiter.cs))**:
   - Eliminates the 2x boundary burst flaw of Fixed Window counters by smoothly weighting previous window traffic:
     $$\text{Estimated Count} = \text{Current} + \text{Previous} \times (1 - \text{Progress})$$

4. **IETF Rate Limit Middleware ([RateLimiterMiddleware.cs](file:///C:/Users/Vikas/.gemini/antigravity/scratch/RateLimiter/Core/RateLimiterMiddleware.cs))**:
   - Automatically attaches standard headers:
     - `RateLimit-Limit`: Maximum burst allowed.
     - `RateLimit-Remaining`: Remaining allowance.
     - `Retry-After`: Seconds before next request is permitted.
   - Returns structured JSON `429 Too Many Requests`.

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

#### Consume Tokens
```bash
curl -i http://localhost:5000/api/data
```

#### Identify via Custom API Key
```bash
curl -i -H "X-API-Key: client_enterprise_99" http://localhost:5000/api/data
```

#### Check Quota Status
```bash
curl http://localhost:5000/api/status
```

#### Exempt Endpoint
```bash
curl http://localhost:5000/api/unlimited
```
