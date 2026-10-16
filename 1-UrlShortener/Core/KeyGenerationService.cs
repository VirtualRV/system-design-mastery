namespace UrlShortener.Core;

/// <summary>
/// Key Generation Service (KGS) implementing Range-Based Allocation.
/// Leases blocks of numeric ranges (e.g., 100,000 integers) from a central coordinator.
/// Worker generates keys locally using Interlocked.Increment and Base62 encoding in O(1) time
/// with ZERO database round-trips and ZERO collision probing.
/// </summary>
public interface IKeyGenerationService
{
    string GenerateKey();
}

public class RangeKeyGenerationService : IKeyGenerationService
{
    private readonly IRangeCoordinator _coordinator;
    private readonly int _rangeSize;
    private long _currentId;
    private long _maxId;
    private readonly object _renewLock = new();

    public RangeKeyGenerationService(IRangeCoordinator coordinator, int rangeSize = 50_000)
    {
        _coordinator = coordinator;
        _rangeSize = rangeSize;
        AcquireNewRange();
    }

    public string GenerateKey()
    {
        while (true)
        {
            long nextId = Interlocked.Increment(ref _currentId);

            if (nextId <= _maxId)
            {
                return Base62.Encode(nextId);
            }

            // Current block exhausted -> thread-safely lease next range
            lock (_renewLock)
            {
                // Double check if another thread already leased a new block
                if (_currentId > _maxId)
                {
                    AcquireNewRange();
                }
            }
        }
    }

    private void AcquireNewRange()
    {
        var (start, end) = _coordinator.LeaseRange(_rangeSize);
        _currentId = start - 1;
        _maxId = end;
    }
}

/// <summary>
/// Simulates a central coordinator like ZooKeeper, Etcd, or a distributed Redis counter.
/// </summary>
public interface IRangeCoordinator
{
    (long Start, long End) LeaseRange(int count);
}

public class LocalRangeCoordinator : IRangeCoordinator
{
    private long _globalSequence = 10_000_000; // Start at 10M to ensure 5+ char Base62

    public (long Start, long End) LeaseRange(int count)
    {
        long start = Interlocked.Add(ref _globalSequence, count) - count + 1;
        long end = start + count - 1;
        return (start, end);
    }
}
