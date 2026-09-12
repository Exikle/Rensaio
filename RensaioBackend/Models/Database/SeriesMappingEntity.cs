using RensaioBackend.Models.Enums;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace RensaioBackend.Models.Database;

/// <summary>
/// Stores the global mapping between a local series and its ID on an external scrobbling service.
/// Unlike UserSeriesMappingEntity, this is shared across all users with role-based overwrite protection.
/// </summary>
public class SeriesMappingEntity
{
    [Key]
    public Guid Id { get; set; }

    /// <summary>
    /// The local series ID. Nullable to support mappings without a linked local
    /// series (e.g. a provider decision scoped to an in-memory global title).
    /// </summary>
    public Guid? SeriesId { get; set; }

    /// <summary>
    /// The scrobbling provider (MyAnimeList, AniList, etc.).
    /// </summary>
    [Required]
    public ExternalSeriesProvider Provider { get; set; }

    /// <summary>
    /// The external series ID on the scrobbling service.
    /// </summary>
    [Required]
    public string ExternalSeriesId { get; set; } = string.Empty;


    /// <summary>
    /// Cached external title from the scrobbler.
    /// </summary>
    public string? ExternalSeriesTitle { get; set; }

    /// <summary>
    /// Cover image URL for the series as reported by the provider (last fetched via
    /// <see cref="IExternalSeriesProvider.FetchSeriesMetadataAsync"/> on approval).
    /// </summary>
    public string? SeriesCoverUrl { get; set; }

    /// <summary>
    /// Provider-native metadata stored as JSON (verbatim detail-call payload, e.g. the
    /// MangaBaka/Bangumi/MangaUpdates series object). Respects the provider's original structure.
    /// </summary>
    public string? MetaData { get; set; }

    /// <summary>
    /// Canonical "site:id" pairs of the same series on other sites.
    /// Example: ["anilist:30002", "myanimelist:2", "mangaupdates:njeqwry"].
    /// Stored as a comma-separated TEXT column via EF value conversion.
    /// </summary>
    public List<string> LinkedSitesIds { get; set; } = [];

    /// <summary>
    /// All alternate titles including the main title.
    /// Stored as a JSON TEXT column via EF value conversion.
    /// </summary>
    public List<string> AlternativeTitles { get; set; } = [];

    /// <summary>
    /// The user who last created or updated this mapping.
    /// </summary>
    public Guid? UserUid { get; set; }

    /// <summary>
    /// The user-level (role) at the time of update, used for overwrite priority.
    /// Higher-level users can overwrite mappings set by lower-level users.
    /// </summary>
    public UserLevel UserRole { get; set; }

    /// <summary>
    /// Timestamp of when this mapping was last created or updated.
    /// </summary>
    public DateTime UpdateDate { get; set; }

    /// <summary>
    /// App-wide link/decision state. Defaults to <see cref="SeriesMappingStatus.AutoMatched"/>
    /// when the row represents an active link (has an <see cref="ExternalSeriesId"/>). Use
    /// <see cref="SeriesMappingStatus.Blocked"/> / <see cref="SeriesMappingStatus.TemporaryIgnored"/> /
    /// <see cref="SeriesMappingStatus.ForeverIgnored"/> for refusals. The background scan reads
    /// this global value (never the per-user status).
    /// </summary>
    public SeriesMappingStatus MappingStatus { get; set; } = SeriesMappingStatus.AutoMatched;

    /// <summary>
    /// UTC timestamp when this global link/decision was made or confirmed. Also the scheduling
    /// basis: a <see cref="SeriesMappingStatus.TemporaryIgnored"/> mapping becomes eligible for
    /// re-evaluation when <c>LinkedDate + 1 month <= now</c>. There is deliberately no separate
    /// "ReviewAfter" column — it is always derived from <see cref="LinkedDate"/>.
    /// </summary>
    public DateTime? LinkedDate { get; set; }

    [ForeignKey(nameof(SeriesId))]
    public SeriesEntity? Series { get; set; }
}