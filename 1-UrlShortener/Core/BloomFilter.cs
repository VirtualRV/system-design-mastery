using System.Collections;

namespace UrlShortener.Core;

/// <summary>
/// Thread-safe in-memory Bloom Filter to prevent Cache Penetration.
/// If Contains(key) == false -> Key DEFINITELY does not exist (0ms database lookup).
/// If Contains(key) == true  -> Key PROBABLY exists (proceed to Cache -> DB).
/// Uses Kirsch-Mitzenmacher optimization: generates k hash values using two 64-bit hashes.
/// </summary>
public class BloomFilter
{
    private readonly BitArray _bits;
    private readonly int _bitSize;
    private readonly int _hashFunctionCount;
    private readonly ReaderWriterLockSlim _lock = new();

    /// <param name="expectedItems">Expected number of unique items (e.g., 100,000).</param>
    /// <param name="falsePositiveRate">Desired false positive probability (e.g., 0.01 for 1%).</param>
    public BloomFilter(int expectedItems = 500_000, double falsePositiveRate = 0.01)
    {
        if (expectedItems <= 0) throw new ArgumentOutOfRangeException(nameof(expectedItems));
        if (falsePositiveRate <= 0 || falsePositiveRate >= 1) throw new ArgumentOutOfRangeException(nameof(falsePositiveRate));

        // Optimal bit array size: m = - (n * ln(p)) / (ln(2)^2)
        _bitSize = (int)Math.Ceiling(-expectedItems * Math.Log(falsePositiveRate) / Math.Pow(Math.Log(2), 2));
        
        // Optimal number of hash functions: k = (m / n) * ln(2)
        _hashFunctionCount = Math.Max(1, (int)Math.Round((double)_bitSize / expectedItems * Math.Log(2)));

        _bits = new BitArray(_bitSize);
    }

    public void Add(string item)
    {
        var (h1, h2) = ComputeHashes(item);
        _lock.EnterWriteLock();
        try
        {
            for (int i = 0; i < _hashFunctionCount; i++)
            {
                int combinedHash = Math.Abs((int)((h1 + (long)i * h2) % _bitSize));
                _bits.Set(combinedHash, true);
            }
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    public bool MightContain(string item)
    {
        var (h1, h2) = ComputeHashes(item);
        _lock.EnterReadLock();
        try
        {
            for (int i = 0; i < _hashFunctionCount; i++)
            {
                int combinedHash = Math.Abs((int)((h1 + (long)i * h2) % _bitSize));
                if (!_bits.Get(combinedHash))
                    return false; // Definitely not present
            }
            return true; // Probably present
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    private static (long H1, long H2) ComputeHashes(string item)
    {
        // 64-bit FNV-1a hash
        const ulong fnvOffset = 14695981039346656037UL;
        const ulong fnvPrime = 1099511628211UL;

        ulong hash1 = fnvOffset;
        foreach (char c in item)
        {
            hash1 ^= (byte)(c & 0xFF);
            hash1 *= fnvPrime;
            hash1 ^= (byte)(c >> 8);
            hash1 *= fnvPrime;
        }

        // Secondary hash: Murmur-like mixing
        ulong hash2 = hash1 ^ (hash1 >> 33);
        hash2 *= 0xff51afd7ed558ccdUL;
        hash2 ^= (hash2 >> 33);
        hash2 *= 0xc4ceb9fe1a85ec53UL;
        hash2 ^= (hash2 >> 33);

        return ((long)hash1, (long)hash2);
    }
}
