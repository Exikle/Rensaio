using System.Collections.Concurrent;

namespace RensaioBackend.Utils
{
    /// <summary>
    /// Bounded, thread-safe LRU-ish string→decimal map used by scrobbler providers for
    /// de-duplication of read-state uploads. Previously each provider held a static
    /// <c>ConcurrentDictionary</c> that grew monotonically with every unique (user, series)
    /// key ever synced; this wrapper caps the size and evicts oldest entries first.
    /// </summary>
    public sealed class BoundedStringDecimalMap
    {
        /// <summary>
        /// Maximum number of entries retained. Keys are "userExternalId" (user-scoped
        /// external series ids); 10k covers a long-lived library with many users and
        /// keeps the dedup semantics warm for the actively-reading set.
        /// </summary>
        public static readonly int MaxEntries = 20000;

        private readonly ConcurrentDictionary<string, (long Revision, decimal Value)> _map = new();
        private long _revision = 0;

        /// <summary>Returns the value for <paramref name="key"/>, or null when absent.</summary>
        public decimal? TryGet(string key)
        {
            if (_map.TryGetValue(key, out var entry))
            {
                // Touch so actively-synced series stay resident.
                _map[key] = (Interlocked.Increment(ref _revision), entry.Value);
                return entry.Value;
            }
            return null;
        }

        /// <summary>Inserts or updates the value for <paramref name="key"/>, then trims if over the cap.</summary>
        public void Set(string key, decimal value)
        {
            _map[key] = (Interlocked.Increment(ref _revision), value);
            TrimExcess();
        }

        /// <summary>Current number of cached entries (diagnostics).</summary>
        public int Count => _map.Count;

        private void TrimExcess()
        {
            int overflow = _map.Count - MaxEntries;
            if (overflow <= 0)
                return;

            var oldest = _map.ToList()
                .OrderBy(kvp => kvp.Value.Item1)
                .Take(overflow)
                .Select(kvp => kvp.Key)
                .ToList();
            foreach (string key in oldest)
            {
                _map.TryRemove(key, out _);
            }
        }
    }
}