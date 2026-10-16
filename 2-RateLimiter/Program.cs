using RateLimiter.Core;
using RateLimiter.Tests;

// CLI Test Runner Flag
if (args.Contains("--test"))
{
    int exitCode = await RateLimiterTestSuite.RunAllAsync();
    Environment.Exit(exitCode);
}

var builder = WebApplication.CreateBuilder(args);

// Register Rate Limiter: Capacity of 5 tokens, replenishing at 1 token/sec
builder.Services.AddSingleton<IRateLimiter>(_ => new TokenBucketLimiter(capacity: 5, refillPerSecond: 1.0));

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

// Register Custom Rate Limiting Middleware
app.UseMiddleware<RateLimiterMiddleware>();

// 1. Protected API Endpoint (Enforces Rate Limiting)
app.MapGet("/api/data", (HttpContext context) =>
{
    return Results.Ok(new
    {
        message = "Successfully accessed protected resource!",
        timestamp = DateTime.UtcNow,
        serverTime = DateTime.UtcNow.ToString("o")
    });
});

// 2. Client Tier / Quota Status
app.MapGet("/api/status", (HttpContext context, IRateLimiter limiter) =>
{
    string clientKey = context.Request.Headers.TryGetValue("X-API-Key", out var key)
        ? $"apikey:{key}"
        : $"ip:{context.Connection.RemoteIpAddress ?? System.Net.IPAddress.Loopback}";

    return Results.Ok(new
    {
        client = clientKey,
        configuredBurstCapacity = 5,
        refillRate = "1 token every 1.0 second",
        status = "Active"
    });
});

// 3. Exempt / Unlimited Endpoint (Bypasses Rate Limiter Middleware)
app.MapGet("/api/unlimited", () =>
{
    return Results.Ok(new
    {
        message = "This endpoint is exempt from rate limiting (e.g. Health checks, Ping).",
        status = "Healthy"
    });
});

app.Run();
