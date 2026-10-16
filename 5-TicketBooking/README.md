# High-Concurrency Ticket Booking System (.NET 10)

An implementation of a high-concurrency ticket booking engine (Ticketmaster / BookMyShow) enforcing **zero double-bookings**, **10-minute temporary holds**, **distributed in-memory locks**, and **interactive stadium seat mapping**.

---

## Architectural Highlights

1. **3 Concurrency Gates Architecture ([SeatInventoryService.cs](file:///C:/Users/Vikas/.gemini/antigravity/scratch/TicketBooking/Core/SeatInventoryService.cs))**:
   - **Gate 1**: Virtual Waiting Room / Rate Limiting at the edge.
   - **Gate 2**: In-Memory Distributed Lock (simulating Redis Redlock/Lua) filtering 99.9% of flash sale traffic in $< 1\text{ms}$.
   - **Gate 3**: Relational DB ACID transaction with optimistic concurrency versioning for the final confirmed booking.

2. **10-Minute Reservation Hold & Delayed Auto-Release**:
   - Selecting a seat locks it for 600 seconds.
   - Background delayed task automatically returns the seat to `Available` if payment is not completed before the deadline without running continuous expensive database polling queries.

3. **Idempotency Protection**:
   - Clients supply an `Idempotency-Key` on payment.
   - Prevents duplicate credit card charges and duplicate ticket generation if the user double-clicks "Pay".

4. **Interactive Stadium Visualizer UI ([wwwroot/index.html](file:///C:/Users/Vikas/.gemini/antigravity/scratch/TicketBooking/wwwroot/index.html))**:
   - Live visual venue map showing 50 seats across Rows A–E.
   - Real-time 10-minute countdown timer.
   - **"Simulate 50 Concurrent Bots" button** to test distributed locking live in the browser.

---

## How to Run

### 1. Run the Architecture Benchmark & Test Suite
```powershell
dotnet run -- --test
```

### 2. Start the Interactive Web UI
```powershell
dotnet run
```
Open your browser at the displayed local URL (e.g. `http://localhost:5000`) to view and interact with the stadium seat map!
