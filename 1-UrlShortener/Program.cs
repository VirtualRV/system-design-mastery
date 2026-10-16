using Microsoft.AspNetCore.Mvc;
using UrlShortener.Core;
using UrlShortener.Tests;

// If invoked with --test flag, run the verification and benchmark suite directly
if (args.Contains("--test"))
{
    int exitCode = await SystemDesignTestSuite.RunAllAsync();
    Environment.Exit(exitCode);
}

var builder = WebApplication.CreateBuilder(args);

// Register Core Components as Singletons
var bloomFilter = new BloomFilter(expectedItems: 100_000, falsePositiveRate: 0.01);
var coordinator = new LocalRangeCoordinator();
var kgs = new RangeKeyGenerationService(coordinator, rangeSize: 10_000);
var dbPath = Path.Combine(builder.Environment.ContentRootPath, "urlshortener.db");
var repository = new CachedSqliteUrlRepository($"Data Source={dbPath};Cache=Shared;", bloomFilter);
var analytics = new ChannelAnalyticsEngine();

builder.Services.AddSingleton(bloomFilter);
builder.Services.AddSingleton<IRangeCoordinator>(coordinator);
builder.Services.AddSingleton<IKeyGenerationService>(kgs);
builder.Services.AddSingleton<IUrlRepository>(repository);
builder.Services.AddSingleton<IAnalyticsEngine>(analytics);

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

// Initialize Database & Warm Up Bloom Filter
using (var scope = app.Services.CreateScope())
{
    var repo = scope.ServiceProvider.GetRequiredService<IUrlRepository>();
    await repo.InitializeAsync();
}

// ---------------- API Endpoints ----------------

// 1. Shorten URL Endpoint
app.MapPost("/api/shorten", async (
    [FromBody] ShortenRequest request,
    [FromServices] IKeyGenerationService keyGen,
    [FromServices] IUrlRepository repo,
    HttpContext context) =>
{
    if (string.IsNullOrWhiteSpace(request.OriginalUrl) ||
        !Uri.TryCreate(request.OriginalUrl, UriKind.Absolute, out var uriResult) ||
        (uriResult.Scheme != Uri.UriSchemeHttp && uriResult.Scheme != Uri.UriSchemeHttps))
    {
        return Results.BadRequest(new { error = "Invalid URL. Must be an absolute HTTP or HTTPS URL." });
    }

    string shortCode;
    if (!string.IsNullOrWhiteSpace(request.CustomAlias))
    {
        shortCode = request.CustomAlias.Trim();
        if (shortCode.Length is < 3 or > 30 || !shortCode.All(char.IsLetterOrDigit))
        {
            return Results.BadRequest(new { error = "Custom alias must be 3-30 alphanumeric characters." });
        }
    }
    else
    {
        shortCode = keyGen.GenerateKey();
    }

    DateTime? expiresAt = request.TtlSeconds.HasValue
        ? DateTime.UtcNow.AddSeconds(request.TtlSeconds.Value)
        : null;

    var record = new ShortenedUrl(
        ShortCode: shortCode,
        OriginalUrl: request.OriginalUrl,
        CreatedAt: DateTime.UtcNow,
        ExpiresAt: expiresAt
    );

    bool saved = await repo.SaveAsync(record);
    if (!saved)
    {
        return Results.Conflict(new { error = $"Short code '{shortCode}' already exists. Please choose another." });
    }

    var baseUrl = $"{context.Request.Scheme}://{context.Request.Host}";
    return Results.Created($"/{shortCode}", new
    {
        shortCode,
        shortUrl = $"{baseUrl}/{shortCode}",
        originalUrl = record.OriginalUrl,
        expiresAt = record.ExpiresAt
    });
});

// 2. Redirection Endpoint (307 Temporary Redirect)
app.MapGet("/{code}", async (
    string code,
    [FromServices] IUrlRepository repo,
    [FromServices] IAnalyticsEngine analyticsEngine,
    HttpContext context) =>
{
    if (string.Equals(code, "favicon.ico", StringComparison.OrdinalIgnoreCase))
    {
        return Results.NotFound();
    }

    var record = await repo.GetByCodeAsync(code);
    if (record == null)
    {
        return Results.NotFound(new { error = "Short URL not found, inactive, or expired." });
    }

    // Asynchronously dispatch click analytics into bounded channel (never blocks redirect)
    var click = new ClickEvent(
        ShortCode: code,
        Timestamp: DateTime.UtcNow,
        Referrer: context.Request.Headers.Referer.ToString(),
        UserAgent: context.Request.Headers.UserAgent.ToString(),
        IpAddress: context.Connection.RemoteIpAddress?.ToString()
    );
    analyticsEngine.EnqueueClick(click);

    // HTTP 307 preserves request method and guarantees browser sends subsequent requests to server for analytics
    return Results.Redirect(record.OriginalUrl, permanent: false, preserveMethod: true);
});

// 3. Analytics Endpoint
app.MapGet("/api/analytics/{code}", (
    string code,
    [FromServices] IAnalyticsEngine analyticsEngine) =>
{
    var stats = analyticsEngine.GetAnalytics(code);
    return Results.Ok(stats);
});

// 4. Deactivate Endpoint
app.MapDelete("/api/{code}", async (
    string code,
    [FromServices] IUrlRepository repo) =>
{
    bool deactivated = await repo.DeactivateAsync(code);
    return deactivated ? Results.Ok(new { message = $"URL '{code}' has been deactivated." }) : Results.NotFound();
});

app.Run();

// Request DTO
public record ShortenRequest(
    string OriginalUrl,
    string? CustomAlias = null,
    int? TtlSeconds = null
);
