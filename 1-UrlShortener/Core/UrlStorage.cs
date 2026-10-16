using Microsoft.Data.Sqlite;
using System.Collections.Concurrent;

namespace UrlShortener.Core;

public record ShortenedUrl(
    string ShortCode,
    string OriginalUrl,
    DateTime CreatedAt,
    DateTime? ExpiresAt,
    bool IsActive = true
);

public interface IUrlRepository
{
    Task InitializeAsync();
    Task<bool> SaveAsync(ShortenedUrl url);
    Task<ShortenedUrl?> GetByCodeAsync(string shortCode);
    Task<bool> DeactivateAsync(string shortCode);
}

/// <summary>
/// Production-grade SQLite repository with WAL mode, unique indexing,
/// in-memory LRU caching, and single-flight cache stampede protection.
/// </summary>
public class CachedSqliteUrlRepository : IUrlRepository
{
    private readonly string _connectionString;
    private readonly BloomFilter _bloomFilter;
    private readonly ConcurrentDictionary<string, ShortenedUrl> _lruCache = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _singleFlightLocks = new();
    private const int MaxCacheSize = 50_000;

    public CachedSqliteUrlRepository(string connectionString, BloomFilter bloomFilter)
    {
        _connectionString = connectionString;
        _bloomFilter = bloomFilter;
    }

    public async Task InitializeAsync()
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();

        // Enable Write-Ahead Logging (WAL) for high-performance concurrent reads
        var walCmd = conn.CreateCommand();
        walCmd.CommandText = "PRAGMA journal_mode = WAL; PRAGMA synchronous = NORMAL;";
        await walCmd.ExecuteNonQueryAsync();

        var tableCmd = conn.CreateCommand();
        tableCmd.CommandText = """
            CREATE TABLE IF NOT EXISTS urls (
                short_code TEXT PRIMARY KEY,
                original_url TEXT NOT NULL,
                created_at TEXT NOT NULL,
                expires_at TEXT,
                is_active INTEGER NOT NULL DEFAULT 1
            );
            CREATE INDEX IF NOT EXISTS idx_urls_code ON urls(short_code);
            CREATE INDEX IF NOT EXISTS idx_urls_expires ON urls(expires_at) WHERE is_active = 1;
        """;
        await tableCmd.ExecuteNonQueryAsync();

        // Warm up Bloom Filter with existing records
        var loadCmd = conn.CreateCommand();
        loadCmd.CommandText = "SELECT short_code FROM urls WHERE is_active = 1;";
        await using var reader = await loadCmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            _bloomFilter.Add(reader.GetString(0));
        }
    }

    public async Task<bool> SaveAsync(ShortenedUrl url)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();

        var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO urls (short_code, original_url, created_at, expires_at, is_active)
            VALUES ($code, $url, $created, $expires, $active)
            ON CONFLICT(short_code) DO NOTHING;
        """;
        cmd.Parameters.AddWithValue("$code", url.ShortCode);
        cmd.Parameters.AddWithValue("$url", url.OriginalUrl);
        cmd.Parameters.AddWithValue("$created", url.CreatedAt.ToString("o"));
        cmd.Parameters.AddWithValue("$expires", (object?)url.ExpiresAt?.ToString("o") ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$active", url.IsActive ? 1 : 0);

        int rows = await cmd.ExecuteNonQueryAsync();
        if (rows > 0)
        {
            // Register in Bloom filter and LRU cache
            _bloomFilter.Add(url.ShortCode);
            AddOrUpdateCache(url);
            return true;
        }

        return false;
    }

    public async Task<ShortenedUrl?> GetByCodeAsync(string shortCode)
    {
        // 1. Guard check: Bloom Filter prevents Cache Penetration on non-existent keys
        if (!_bloomFilter.MightContain(shortCode))
        {
            return null; // Definitely does not exist, 0ms DB lookup
        }

        // 2. Cache hit check
        if (_lruCache.TryGetValue(shortCode, out var cached))
        {
            if (cached.ExpiresAt.HasValue && cached.ExpiresAt.Value < DateTime.UtcNow)
            {
                _lruCache.TryRemove(shortCode, out _);
                return null; // Expired
            }
            return cached.IsActive ? cached : null;
        }

        // 3. Cache Miss: Single-Flight Mutex prevents Cache Stampede / Thundering Herd
        var lockObj = _singleFlightLocks.GetOrAdd(shortCode, _ => new SemaphoreSlim(1, 1));
        await lockObj.WaitAsync();
        try
        {
            // Double check cache
            if (_lruCache.TryGetValue(shortCode, out cached))
            {
                return cached.IsActive ? cached : null;
            }

            // 4. Query Database
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync();

            var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT short_code, original_url, created_at, expires_at, is_active FROM urls WHERE short_code = $code;";
            cmd.Parameters.AddWithValue("$code", shortCode);

            await using var reader = await cmd.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                var record = new ShortenedUrl(
                    ShortCode: reader.GetString(0),
                    OriginalUrl: reader.GetString(1),
                    CreatedAt: DateTime.Parse(reader.GetString(2)),
                    ExpiresAt: reader.IsDBNull(3) ? null : DateTime.Parse(reader.GetString(3)),
                    IsActive: reader.GetInt32(4) == 1
                );

                AddOrUpdateCache(record);

                if (record.ExpiresAt.HasValue && record.ExpiresAt.Value < DateTime.UtcNow)
                {
                    return null; // Expired
                }

                return record.IsActive ? record : null;
            }

            return null;
        }
        finally
        {
            lockObj.Release();
            if (lockObj.CurrentCount == 1)
            {
                _singleFlightLocks.TryRemove(shortCode, out _);
            }
        }
    }

    public async Task<bool> DeactivateAsync(string shortCode)
    {
        _lruCache.TryRemove(shortCode, out _);

        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();

        var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE urls SET is_active = 0 WHERE short_code = $code;";
        cmd.Parameters.AddWithValue("$code", shortCode);

        int rows = await cmd.ExecuteNonQueryAsync();
        return rows > 0;
    }

    private void AddOrUpdateCache(ShortenedUrl url)
    {
        if (_lruCache.Count > MaxCacheSize)
        {
            // Simple eviction of a few keys if exceeding capacity
            var keysToRemove = _lruCache.Keys.Take(100).ToList();
            foreach (var k in keysToRemove) _lruCache.TryRemove(k, out _);
        }
        _lruCache[url.ShortCode] = url;
    }
}
