using System.Collections.Concurrent;

namespace TicketBooking.Core;

public interface ISeatInventoryService
{
    IReadOnlyList<Seat> GetAllSeats();
    Seat? GetSeat(string seatId);
    HoldResult TryHoldSeat(string seatId, string userId, int holdSeconds = 600);
    ConfirmResult ConfirmBooking(string seatId, string userId, string idempotencyKey);
    bool ReleaseSeat(string seatId, string userId);
}

/// <summary>
/// High-concurrency seat inventory engine.
/// Simulates Redis Distributed Locks (Redlock/Lua) + Delayed Expiration Queue.
/// Enforces ACID guarantees and prevents double bookings across massive concurrent spikes.
/// </summary>
public class SeatInventoryService : ISeatInventoryService
{
    private readonly ConcurrentDictionary<string, Seat> _seats = new();
    private readonly ConcurrentDictionary<string, object> _seatLocks = new();
    private readonly ConcurrentDictionary<string, string> _processedIdempotencyKeys = new();

    public SeatInventoryService()
    {
        InitializeVenue();
    }

    private void InitializeVenue()
    {
        string[] rows = ["A", "B", "C", "D", "E"];
        foreach (var row in rows)
        {
            for (int num = 1; num <= 10; num++)
            {
                string id = $"{row}{num}";
                decimal price = row switch
                {
                    "A" => 150.00m, // VIP
                    "B" or "C" => 100.00m, // Premium
                    _ => 75.00m // Standard
                };
                _seats[id] = new Seat(id, row, num, price);
            }
        }
    }

    public IReadOnlyList<Seat> GetAllSeats()
    {
        // Reconcile any expired holds before returning
        DateTime now = DateTime.UtcNow;
        foreach (var seat in _seats.Values)
        {
            if (seat.Status == SeatStatus.Held && seat.HeldUntil.HasValue && seat.HeldUntil.Value < now)
            {
                TryAutoRelease(seat.SeatId);
            }
        }

        return _seats.Values
            .OrderBy(s => s.Row)
            .ThenBy(s => s.Number)
            .ToList();
    }

    public Seat? GetSeat(string seatId)
    {
        _seats.TryGetValue(seatId, out var seat);
        return seat;
    }

    public HoldResult TryHoldSeat(string seatId, string userId, int holdSeconds = 600)
    {
        if (!_seats.ContainsKey(seatId))
        {
            return new HoldResult(false, $"Seat '{seatId}' does not exist.", null);
        }

        var lockObj = _seatLocks.GetOrAdd(seatId, _ => new object());

        lock (lockObj)
        {
            var seat = _seats[seatId];
            DateTime now = DateTime.UtcNow;

            // Check if existing hold has expired
            if (seat.Status == SeatStatus.Held && seat.HeldUntil.HasValue && seat.HeldUntil.Value < now)
            {
                seat = seat with { Status = SeatStatus.Available, HeldByUserId = null, HeldUntil = null };
                _seats[seatId] = seat;
            }

            if (seat.Status != SeatStatus.Available)
            {
                string reason = seat.Status == SeatStatus.Booked
                    ? "Seat is already booked and sold out."
                    : "Seat is currently being held by another buyer.";
                return new HoldResult(false, reason, seat);
            }

            // Grant 10-minute hold
            DateTime heldUntil = now.AddSeconds(holdSeconds);
            var updatedSeat = seat with
            {
                Status = SeatStatus.Held,
                HeldByUserId = userId,
                HeldUntil = heldUntil,
                Version = seat.Version + 1
            };

            _seats[seatId] = updatedSeat;

            // Asynchronously schedule auto-release after hold duration (Delayed Queue simulation)
            _ = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(holdSeconds));
                TryAutoRelease(seatId);
            });

            return new HoldResult(true, "Seat successfully held for checkout.", updatedSeat, TimeSpan.FromSeconds(holdSeconds));
        }
    }

    public ConfirmResult ConfirmBooking(string seatId, string userId, string idempotencyKey)
    {
        // 1. Idempotency Check: Prevent duplicate credit card charges on double-clicks
        if (_processedIdempotencyKeys.TryGetValue(idempotencyKey, out var existingTicket))
        {
            return new ConfirmResult(true, "Booking already confirmed (idempotent replay).", existingTicket);
        }

        if (!_seats.ContainsKey(seatId))
        {
            return new ConfirmResult(false, "Seat does not exist.");
        }

        var lockObj = _seatLocks.GetOrAdd(seatId, _ => new object());

        lock (lockObj)
        {
            var seat = _seats[seatId];
            DateTime now = DateTime.UtcNow;

            // Check if hold expired
            if (seat.Status == SeatStatus.Held && seat.HeldUntil.HasValue && seat.HeldUntil.Value < now)
            {
                TryAutoRelease(seatId);
                return new ConfirmResult(false, "Reservation hold has expired. Seat returned to pool.");
            }

            if (seat.Status != SeatStatus.Held)
            {
                return new ConfirmResult(false, $"Cannot confirm: Seat status is {seat.Status}.");
            }

            if (seat.HeldByUserId != userId)
            {
                return new ConfirmResult(false, "Seat reservation belongs to another user.");
            }

            // 2. Commit Booking (ACID transaction simulation)
            string ticketNumber = $"TKT-{seatId}-{Guid.NewGuid().ToString("N")[..8].ToUpperInvariant()}";
            var bookedSeat = seat with
            {
                Status = SeatStatus.Booked,
                HeldUntil = null,
                Version = seat.Version + 1
            };

            _seats[seatId] = bookedSeat;
            _processedIdempotencyKeys[idempotencyKey] = ticketNumber;

            return new ConfirmResult(true, "Payment successful. Booking confirmed!", ticketNumber);
        }
    }

    public bool ReleaseSeat(string seatId, string userId)
    {
        if (!_seats.ContainsKey(seatId)) return false;

        var lockObj = _seatLocks.GetOrAdd(seatId, _ => new object());
        lock (lockObj)
        {
            var seat = _seats[seatId];
            if (seat.Status == SeatStatus.Held && seat.HeldByUserId == userId)
            {
                _seats[seatId] = seat with
                {
                    Status = SeatStatus.Available,
                    HeldByUserId = null,
                    HeldUntil = null,
                    Version = seat.Version + 1
                };
                return true;
            }
            return false;
        }
    }

    private void TryAutoRelease(string seatId)
    {
        var lockObj = _seatLocks.GetOrAdd(seatId, _ => new object());
        lock (lockObj)
        {
            if (_seats.TryGetValue(seatId, out var seat) &&
                seat.Status == SeatStatus.Held &&
                seat.HeldUntil.HasValue &&
                seat.HeldUntil.Value <= DateTime.UtcNow)
            {
                _seats[seatId] = seat with
                {
                    Status = SeatStatus.Available,
                    HeldByUserId = null,
                    HeldUntil = null,
                    Version = seat.Version + 1
                };
            }
        }
    }
}
