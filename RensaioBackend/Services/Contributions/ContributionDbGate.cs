using RensaioBackend.Utils;

namespace RensaioBackend.Services.Contributions
{
    /// <summary>
    /// Shared exclusive gate for the Local Contribution DB. The import
    /// (metadata.bin copy + apply + VACUUM) must not interleave with uploads or
    /// other writers, so both paths acquire this lock. Reader-style pages that
    /// build the in-memory index should also take it when correctness matters.
    /// </summary>
    public sealed class ContributionDbGate
    {
        private readonly AsyncLock _lock = new();

        public Task<IDisposable> LockAsync(CancellationToken token = default)
            => _lock.LockAsync(token);
    }
}