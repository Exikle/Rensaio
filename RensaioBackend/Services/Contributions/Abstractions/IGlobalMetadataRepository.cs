using RensaioBackend.Models.Enums;

namespace RensaioBackend.Services.Contributions.Abstractions;

/// <summary>
/// Read view over the global metadata repository: a collection of metadata associations
/// between titles and providers. Started and hydrated at app startup. Backed by PR #76's
/// snapshot models (titles / metadata / records) on top of app-carried global SeriesMappings.
/// </summary>
public interface IGlobalMetadataRepository
{
    /// <summary>All known global titles.</summary>
    List<GlobalTitleEntity> GetAllTitlesAsync(CancellationToken token = default);

    /// <summary>A single global title by id, or null.</summary>
    Task<GlobalTitleEntity?> GetTitleAsync(string titleId, CancellationToken token = default);

    /// <summary>The metadata/record associations for a title.</summary>
    List<GlobalAssociationEntity> GetAssociationsAsync(string titleId, CancellationToken token = default);

    /// <summary>
    /// Resolve a canonical "site:id" pair (or provider + provider key) back to a global title id.
    /// Returns null when unknown.
    /// </summary>
    string? ResolveTitleIdAsync(ExternalSeriesProvider provider, string providerKey, CancellationToken token = default);
}

/// <summary>A global title: canonical identity independent of any local SeriesEntity.</summary>
public sealed class GlobalTitleEntity
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string? Type { get; set; }
    public List<string> Genre { get; set; } = [];
}

/// <summary>A provider association for a global title (mirrors a metadata / record row).</summary>
public sealed class GlobalAssociationEntity
{
    public string TitleId { get; set; } = string.Empty;
    public ExternalSeriesProvider Provider { get; set; }
    public string ProviderKey { get; set; } = string.Empty;
    public int LinkType { get; set; }
    public string? ProviderData { get; set; }
}