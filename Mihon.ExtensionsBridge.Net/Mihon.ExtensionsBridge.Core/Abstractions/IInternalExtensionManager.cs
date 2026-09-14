using Mihon.ExtensionsBridge.Models;
using Mihon.ExtensionsBridge.Models.Abstractions;

namespace Mihon.ExtensionsBridge.Core.Abstractions
{
    public interface IInternalExtensionManager : IExtensionManager
    {
        Task<int> ValidateAndRecompileAsync(IEnumerable<RepositoryEntry> entries, CancellationToken token = default);
        Task InitializeAsync(CancellationToken token = default);
        RepositoryGroup? FindExtension(RepositoryGroup grp);
        Task CompareOnlineWithLocalAndAutoUpdateAsync(IEnumerable<TachiyomiRepository> onlineRepos, CancellationToken token = default);
        Task ShutdownAsync(CancellationToken token = default);
        /// <summary>Evicts idle/over-budget extension interops so their native classloaders and JVM heaps can be released.</summary>
        Task SweepIdleInteropsAsync();
    }
}