using System.Text.Json;

namespace RateLimiter.Core;

public class RateLimiterMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IRateLimiter _limiter;
    private readonly ILogger<RateLimiterMiddleware> _logger;

    public RateLimiterMiddleware(RequestDelegate next, IRateLimiter limiter, ILogger<RateLimiterMiddleware> logger)
    {
        _next = next;
        _limiter = limiter;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // Exempt UI dashboard and unlimited routes from rate limiting
        if (context.Request.Path == "/" || 
            context.Request.Path == "/index.html" || 
            context.Request.Path.StartsWithSegments("/api/unlimited"))
        {
            await _next(context);
            return;
        }

        // 1. Identify Client (Prefer API Key, fallback to IP Address)
        string clientKey;
        if (context.Request.Headers.TryGetValue("X-API-Key", out var apiKey) && !string.IsNullOrWhiteSpace(apiKey))
        {
            clientKey = $"apikey:{apiKey.ToString().Trim()}";
        }
        else
        {
            var ip = context.Connection.RemoteIpAddress?.ToString() ?? "127.0.0.1";
            clientKey = $"ip:{ip}";
        }

        // 2. Evaluate Limit
        var result = _limiter.TryAcquire(clientKey);

        // 3. Attach Standard IETF Rate Limiting Headers
        context.Response.Headers["RateLimit-Limit"] = result.Limit.ToString();
        context.Response.Headers["RateLimit-Remaining"] = result.Remaining.ToString();

        if (result.IsAllowed)
        {
            await _next(context);
        }
        else
        {
            // 4. Return HTTP 429 Too Many Requests
            int retrySeconds = Math.Max(1, (int)Math.Ceiling(result.RetryAfter.TotalSeconds));
            context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            context.Response.Headers["Retry-After"] = retrySeconds.ToString();
            context.Response.ContentType = "application/json";

            var errorResponse = new
            {
                status = 429,
                error = "Too Many Requests",
                message = $"API rate limit exceeded. Please retry after {retrySeconds} seconds.",
                clientIdentifier = clientKey,
                retryAfterSeconds = retrySeconds,
                limit = result.Limit,
                remaining = 0
            };

            await context.Response.WriteAsync(JsonSerializer.Serialize(errorResponse));
        }
    }
}
