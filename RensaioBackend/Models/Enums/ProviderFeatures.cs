namespace RensaioBackend.Models.Enums;

/// <summary>
/// Flag enum describing which features a metadata/scrobbling provider supports.
/// Providers can support both scrobbling (read-state tracking) and metadata (rich series info).
/// </summary>
[Flags]
public enum ProviderFeatures
{
    /// <summary>Provider supports no features (unused).</summary>
    None = 0,

    /// <summary>
    /// Provider supports scrobbling: reading/setting read-state (chapters, pages) for a user.
    /// Examples: AniList, MyAnimeList, Kitsu, MangaDex.
    /// </summary>
    Scrobbling = 1 << 0,

    /// <summary>
    /// Provider supports rich series metadata: search, detail, alternate titles, and
    /// cross-site linked IDs (LinkedSitesIds). All metadata providers set this.
    /// </summary>
    Metadata = 1 << 1,

    // Future features (reserved bits):
    // Tracking = 1 << 2,
    // FavoriteLists = 1 << 3,
}

public static class ProviderFeaturesExtensions
{
    /// <summary>
    /// Returns true when <paramref name="features"/> includes all of the requested <paramref name="feature"/> flags.
    /// </summary>
    public static bool Supports(this ProviderFeatures features, ProviderFeatures feature)
        => (features & feature) == feature;
}