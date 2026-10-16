namespace TicketBooking.Core;

public enum SeatStatus
{
    Available = 0,
    Held = 1,
    PaymentProcessing = 2,
    Booked = 3
}

public record Seat(
    string SeatId,
    string Row,
    int Number,
    decimal Price,
    SeatStatus Status = SeatStatus.Available,
    string? HeldByUserId = null,
    DateTime? HeldUntil = null,
    long Version = 1
);

public record HoldSeatRequest(
    string SeatId,
    string UserId,
    int HoldDurationSeconds = 600
);

public record ConfirmBookingRequest(
    string SeatId,
    string UserId,
    string IdempotencyKey
);

public record HoldResult(
    bool Success,
    string Message,
    Seat? Seat,
    TimeSpan? HoldDuration = null
);

public record ConfirmResult(
    bool Success,
    string Message,
    string? TicketNumber = null
);
