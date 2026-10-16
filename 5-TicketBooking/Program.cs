using Microsoft.AspNetCore.Mvc;
using TicketBooking.Core;
using TicketBooking.Tests;

// CLI Test Runner Flag
if (args.Contains("--test"))
{
    int exitCode = await TicketBookingTestSuite.RunAllAsync();
    Environment.Exit(exitCode);
}

var builder = WebApplication.CreateBuilder(args);

// Register Core Inventory Singleton
builder.Services.AddSingleton<ISeatInventoryService, SeatInventoryService>();

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

// 1. Get All Seats (Stadium Layout)
app.MapGet("/api/seats", (ISeatInventoryService inventory) =>
{
    var seats = inventory.GetAllSeats();
    return Results.Ok(seats);
});

// 2. Hold Seat (10-Minute Lock)
app.MapPost("/api/hold", ([FromBody] HoldSeatRequest request, ISeatInventoryService inventory) =>
{
    var result = inventory.TryHoldSeat(request.SeatId, request.UserId, request.HoldDurationSeconds);
    if (result.Success)
    {
        return Results.Ok(new
        {
            message = result.Message,
            seat = result.Seat,
            holdDurationSeconds = result.HoldDuration?.TotalSeconds ?? request.HoldDurationSeconds
        });
    }
    return Results.Conflict(new { error = result.Message });
});

// 3. Confirm Booking & Payment (with Idempotency Key)
app.MapPost("/api/confirm", ([FromBody] ConfirmBookingRequest request, ISeatInventoryService inventory) =>
{
    var result = inventory.ConfirmBooking(request.SeatId, request.UserId, request.IdempotencyKey);
    if (result.Success)
    {
        return Results.Ok(new
        {
            message = result.Message,
            ticketNumber = result.TicketNumber
        });
    }
    return Results.BadRequest(new { error = result.Message });
});

// 4. Cancel / Release Hold Explicitly
app.MapPost("/api/release", ([FromQuery] string seatId, [FromQuery] string userId, ISeatInventoryService inventory) =>
{
    bool released = inventory.ReleaseSeat(seatId, userId);
    return released ? Results.Ok(new { message = "Seat released." }) : Results.NotFound();
});

app.Run();
