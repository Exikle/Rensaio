using RensaioBackend.Models;
using RensaioBackend.Models.ReadState;
using System.Collections.Concurrent;

namespace RensaioBackend.Services.ReadState;

/// <summary>
/// Cache for read state data to avoid reading rensaio.json on every OPDS request.
/// </summary>
public class ReadStateCacheService
{
    /// <summary>
    /// Maximum number of (user, series) entries retained in memory. Bounded so the cache
    /// cannot grow unboundedly with library size — stale entries are evicted oldest-first
    /// and will be re-read from disk on the next access.
    /// </summary>
    private static readonly int MaxCacheEntries = 5000;

    /// <summary>
    /// Cache entries with a monotonic revision used for oldest-first eviction.
    /// </summary>
    private readonly ConcurrentDictionary<string, (long Revision, List<ChapterReadState> States)> _cache = new();
    private long _revision = 0;

    /// <summary>
    /// Gets cached read states for a user and series.
    /// Key format: "readstate:{username}:{seriesStoragePath}"
    /// </summary>
    public List<ChapterReadState>? GetCachedReadStates(string username, string seriesStoragePath)
    {
        string key = BuildKey(username, seriesStoragePath);
        if (_cache.TryGetValue(key, out var entry))
        {
            // Touch the entry so it counts as most-recent on the next eviction.
            _cache[key] = (Interlocked.Increment(ref _revision), entry.States);
            return entry.States;
        }
        return null;
    }

    /// <summary>
    /// Sets cached read states for a user and series.
    /// </summary>
    public void SetCachedReadStates(string username, string seriesStoragePath, List<ChapterReadState> states)
    {
        string key = BuildKey(username, seriesStoragePath);
        _cache[key] = (Interlocked.Increment(ref _revision), states);
        TrimExcess();
    }

    /// <summary>
    /// Invalidates the cache for a specific user+series combination.
    /// </summary>
    public void Invalidate(string username, string seriesStoragePath)
    {
        string key = BuildKey(username, seriesStoragePath);
        _cache.TryRemove(key, out _);
    }

    /// <summary>
    /// Invalidates all cached read states.
    /// </summary>
    public void InvalidateAll()
    {
        _cache.Clear();
    }

    /// <summary>
    /// Evicts oldest entries (lowest revision) until the cache is back under the cap.
    /// Runs on insert; O(N) on overflow only, which is negligible against the disk
    /// I/O saved by keeping the hot set resident.
    /// </summary>
    private void TrimExcess()
    {
        int overflow = _cache.Count - MaxCacheEntries;
        if (overflow <= 0)
            return;

        // Gather candidates sorted by revision (least-recently-used first).
        var byRevision = _cache.ToList()
            .OrderBy(kvp => kvp.Value.Item1)
            .Take(overflow)
            .Select(kvp => kvp.Key)
            .ToList();
        foreach (string key in byRevision)
        {
            _cache.TryRemove(key, out _);
        }
    }

    private static string BuildKey(string username, string seriesStoragePath)
    {
        return $"{username}:{seriesStoragePath}";
    }
}