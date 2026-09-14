using RensaioBackend.Models.Enums;
using RensaioBackend.Services.Scrobbling.Abstractions;
using RensaioBackend.Utils;

namespace RensaioBackend.Services.Metadata;

/// <summary>
/// Central registry that maps each metadata/scrobbling provider to its canonical site slug
/// (used in <c>LinkedSitesIds</c>, e.g. "anilist:30002") and normalizes the heterogeneous
/// external-site names different providers use (AniList "MyAnimeList", Kitsu "myanimelist/manga",
/// MangaDex "mal", MangaBaka "my_anime_list", ...) to those canonical slugs.
/// </summary>
public class SeriesMetadataResolver : IDisposable
{
    private Stream? _mangaupdates_legacy_map_stream = null;
    private object _readerLock = new object();
    private bool _disposed = false;
    public SeriesMetadataResolver(IConfiguration config)
    {
        string path = System.IO.Path.Combine(config["runtimeDirectory"]!, "wwwroot");
        if (Directory.Exists(path))
        {
            var mapPath = System.IO.Path.Combine(path, "mangaupdates_legacy_map.bin");
            if (File.Exists(mapPath))
            {
                _mangaupdates_legacy_map_stream = File.OpenRead(mapPath);
            }
        }
    }
    ~SeriesMetadataResolver()
    {
        Dispose();
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            _mangaupdates_legacy_map_stream?.Dispose();
        }
        catch { /* best effort */ }
        _mangaupdates_legacy_map_stream = null;
    }

    /// <summary>
    /// Canonical slug -> provider. Includes all providers (even deferred ones) so
    /// LinkedSitesIds produced by any provider remain resolvable.
    /// </summary>
    private static readonly Dictionary<string, ExternalSeriesProvider> SlugToProvider = new(StringComparer.OrdinalIgnoreCase)
    {
        ["myanimelist"] = ExternalSeriesProvider.MyAnimeList,
        ["anilist"] = ExternalSeriesProvider.AniList,
        ["comicvine"] = ExternalSeriesProvider.ComicVine,
        ["kitsu"] = ExternalSeriesProvider.Kitsu,
        ["mangadex"] = ExternalSeriesProvider.MangaDex,
        ["mangabaka"] = ExternalSeriesProvider.MangaBaka,
        ["bgm"] = ExternalSeriesProvider.Bangumi,
        ["mangaupdates"] = ExternalSeriesProvider.MangaUpdates,
        ["gcd"] = ExternalSeriesProvider.GrandComicsDatabase,
        ["metron"] = ExternalSeriesProvider.Metron,
    };

    /// <summary>
    /// Aliases that providers use to reference a site, normalized to canonical slugs.
    /// Keys are lowercased; values are canonical slugs.
    /// Covers AniList externalLinks/related site names, Kitsu mappings externalSite,
    /// MangaDex links keys, and MangaBaka source keys.
    /// </summary>
    private static readonly Dictionary<string, string> SiteAliasToSlug = new(StringComparer.OrdinalIgnoreCase)
    {
        // AniList external links site values
        ["myanimelist"] = "myanimelist",
        ["mal"] = "myanimelist",
        ["anilist"] = "anilist",
        ["kitsu"] = "kitsu",
        ["mangadex"] = "mangadex",
        ["mangaupdates"] = "mangaupdates",
        ["comicvine"] = "comicvine",
        ["mangabaka"] = "mangabaka",
        ["bgm"] = "bgm",
        ["bangumi"] = "bgm",
        ["gcd"] = "gcd",
        ["grandcomicsdatabase"] = "gcd",
        ["metron"] = "metron",

        // Kitsu mappings externalSite (e.g. "myanimelist/manga", "anilist/anime")
        ["myanimelist/manga"] = "myanimelist",
        ["myanimelist/anime"] = "myanimelist",
        ["myanimelist/"] = "myanimelist",
        ["anilist/manga"] = "anilist",
        ["anilist/anime"] = "anilist",
        ["anilist/"] = "anilist",
        ["mangaupdates/"] = "mangaupdates",
        ["mangaupdates/manga"] = "mangaupdates",

        // MangaDex links keys
        ["al"] = "anilist",
        ["mal"] = "myanimelist",
        ["kt"] = "kitsu",
        ["mu"] = "mangaupdates",
        ["bw"] = "bookwalker",     // reserved/unsupported slug
        ["ap"] = "animeplanet",    // reserved/unsupported slug
        ["amz"] = "amazon",        // reserved/unsupported slug
        ["ebj"] = "ebookjapan",    // reserved/unsupported slug

        // MangaBaka source keys
        ["anilist"] = "anilist",
        ["my_anime_list"] = "myanimelist",
        ["manga_updates"] = "mangaupdates",
        ["kitsu"] = "kitsu",
        ["shikimori"] = "shikimori",      // reserved/unsupported slug
        ["anime_planet"] = "animeplanet", // reserved/unsupported slug
        ["anime_news_network"] = "ann",   // reserved/unsupported slug
    };

    /// <summary>
    /// Returns the canonical site slug for a provider (e.g. <see cref="ExternalSeriesProvider.Bangumi"/> -> "bgm").
    /// </summary>
    public string GetSiteSlug(ExternalSeriesProvider provider)
        => provider switch
        {
            ExternalSeriesProvider.MyAnimeList => "myanimelist",
            ExternalSeriesProvider.AniList => "anilist",
            ExternalSeriesProvider.ComicVine => "comicvine",
            ExternalSeriesProvider.Kitsu => "kitsu",
            ExternalSeriesProvider.MangaDex => "mangadex",
            ExternalSeriesProvider.MangaBaka => "mangabaka",
            ExternalSeriesProvider.Bangumi => "bgm",
            ExternalSeriesProvider.MangaUpdates => "mangaupdates",
            ExternalSeriesProvider.GrandComicsDatabase => "gcd",
            ExternalSeriesProvider.Metron => "metron",
            _ => provider.ToString().ToLowerInvariant()
        };

    /// <summary>
    /// Resolves a canonical slug (case-insensitive) back to a provider enum.
    /// Returns null for unknown slugs.
    /// </summary>
    public ExternalSeriesProvider? GetProviderFromSlug(string slug)
    {
        if (string.IsNullOrWhiteSpace(slug))
            return null;
        return SlugToProvider.TryGetValue(slug.Trim(), out var provider) ? provider : null;
    }

    /// <summary>
    /// Returns true when a provider supports the metadata feature (used to filter the link engine).
    /// </summary>
    public static bool IsMetadataProvider(IExternalSeriesProvider provider)
        => provider.Features.Supports(ProviderFeatures.Metadata);

    /// <summary>
    /// Normalizes an external-site name (as reported by a provider) plus its external ID into a
    /// canonical "site:id" string (e.g. ("myanimelist/manga", "2", Kitsu) -> "myanimelist:2").
    /// Returns null when the site is unknown or the ID is empty.
    /// </summary>
    public string? TryCreateLinkedSiteId(string? externalSiteName, string? externalId, ExternalSeriesProvider sourceProvider)
    {
        if (string.IsNullOrWhiteSpace(externalId))
            return null;

        var slug = NormalizeSiteSlug(externalSiteName, sourceProvider);
        if (slug == null)
            return null;

        return $"{slug}:{externalId.Trim()}";
    }

    /// <summary>
    /// Normalizes an external-site name to a canonical slug, or null when unsupported.
    /// Falls back to trying the slug map directly (e.g. "anilist" already canonical).
    /// </summary>
    public string? NormalizeSiteSlug(string? externalSiteName, ExternalSeriesProvider sourceProvider)
    {
        if (string.IsNullOrWhiteSpace(externalSiteName))
            return null;

        var key = externalSiteName.Trim().ToLowerInvariant();

        // Exact alias match
        if (SiteAliasToSlug.TryGetValue(key, out var slug))
            return IsSupportedSlug(slug) ? slug : null;

        // Fallback: it may already be a canonical slug (e.g. "mangabaka", "mangaupdates")
        if (SlugToProvider.ContainsKey(key))
            return key;

        // No normalization possible
        return null;
    }
    public string? NormalizeId(string canonicalSlug, string? externalId)
    {
        if (string.IsNullOrWhiteSpace(externalId))
            return "";
        if (canonicalSlug == "mangaupdates")
        {
            if (long.TryParse(externalId.Trim(), out long id) && !externalId.Trim().StartsWith("0"))
            {
                if (id > 0 && id <= 200591 && _mangaupdates_legacy_map_stream != null)
                {
                    long position = ((int)id - 1) * 8;
                    lock (_readerLock)
                    {
                        using var reader = new BinaryReader(_mangaupdates_legacy_map_stream, System.Text.Encoding.UTF8, leaveOpen: true);
                        _mangaupdates_legacy_map_stream.Seek(position, SeekOrigin.Begin);
                        id = (long)reader.ReadUInt64();
                    }
                    return Base36Codec.Encode(id);
                }
            }
        }
        return externalId.Trim();
    }

    /// <summary>
    /// Links that reference sites we do not track (animeplanet, bookwalker, amazon,
    /// shikimori, ann, ...) are excluded from LinkedSitesIds to keep the graph clean.
    /// </summary>
    private static bool IsSupportedSlug(string slug)
        => SlugToProvider.ContainsKey(slug);
}