using RensaioBackend.Models.Abstractions;
using System.Text.Json.Serialization;

namespace RensaioBackend.Models.Dto;

/// <summary>
/// One member source within a contribution mapping group (source scope). The group header on the
/// Contribution Mappings page lists these merged sources; provider rows reuse
/// <see cref="ExternalMappingsSeriesProviderDto"/> so the two pages render identically.
/// </summary>
public sealed class ContributionMappingSourceDto : IThumb
{
    [JsonPropertyName("sourceId")]
    public Guid SourceId { get; set; }

    /// <summary>Canonical source id (package:sourceId) — stable across contribution DBs.</summary>
    [JsonPropertyName("sourceKey")]
    public string SourceKey { get; set; } = string.Empty;

    [JsonPropertyName("package")]
    public string Package { get; set; } = string.Empty;

    [JsonPropertyName("sourceName")]
    public string SourceName { get; set; } = string.Empty;

    [JsonPropertyName("sourceLanguage")]
    public string SourceLanguage { get; set; } = string.Empty;

    /// <summary>Title of this source entry (from the mapping's TitleEntity), may differ from the display title.</summary>
    [JsonPropertyName("title")]
    public string? Title { get; set; }

    /// <summary>Cover from the source record (Data.ThumbnailUrl) — the group shows the first non-empty one.</summary>
    [JsonPropertyName("thumbnailUrl")]
    public string? ThumbnailUrl { get; set; }

    /// <summary>Alias for the thumbnail-cache rewrite machine; never serialized (see thumbnailUrl).</summary>
    [JsonIgnore]
    public string? ThumbnailAlias
    {
        get => ThumbnailUrl;
        set => ThumbnailUrl = value;
    }
}

/// <summary>
/// One grouped mapping of the Contribution Mappings page (source scope). Pagination is over
/// mappings: <c>MappingId</c> / <c>DisplayTitle</c> / <c>CoverUrl</c> identify the group;
/// <c>Titles</c> holds every normalized title linked to the mapping (the automerge key);
/// <c>Sources</c> holds the member source entries (shared-title automerge);
/// <c>Providers</c> holds one row per metadata provider (same shape as the External Mappings page).
/// </summary>
public sealed class ContributionMappingGroupDto : IThumb
{
    [JsonPropertyName("mappingId")]
    public Guid MappingId { get; set; }

    /// <summary>First non-empty title of the mapping (primary display name).</summary>
    [JsonPropertyName("displayTitle")]
    public string? DisplayTitle { get; set; }

    /// <summary>First non-empty source cover among the group's sources.</summary>
    [JsonPropertyName("coverUrl")]
    public string? CoverUrl { get; set; }

    /// <summary>Alias for the thumbnail-cache rewrite machine; never serialized (see coverUrl).</summary>
    [JsonIgnore]
    public string? ThumbnailUrl
    {
        get => CoverUrl;
        set => CoverUrl = value;
    }

    [JsonPropertyName("titles")]
    public List<string> Titles { get; set; } = [];

    [JsonPropertyName("sources")]
    public List<ContributionMappingSourceDto> Sources { get; set; } = [];

    /// <summary>Provider rows — same shape as the External Mappings page so the UI rendering is identical.</summary>
    [JsonPropertyName("providers")]
    public List<ExternalMappingsSeriesProviderDto> Providers { get; set; } = [];
}

/// <summary>Paginated response for the Contribution Mappings list.</summary>
public sealed class ContributionMappingsPageDto
{
    [JsonPropertyName("page")]
    public int Page { get; set; }

    [JsonPropertyName("pageSize")]
    public int PageSize { get; set; }

    [JsonPropertyName("total")]
    public long Total { get; set; }

    /// <summary>Per-provider icon + series URL template, keyed by provider name (same as External Mappings).</summary>
    [JsonPropertyName("providerMeta")]
    public Dictionary<string, ExternalProviderMetaDto> ProviderMeta { get; set; } = [];

    /// <summary>Source-scope: grouped by mapping (pagination counts mappings, not provider rows).</summary>
    [JsonPropertyName("groups")]
    public List<ContributionMappingGroupDto> Groups { get; set; } = [];
}

/// <summary>Request body for a manual contribution-mapping link confirm.</summary>
public sealed class ContributionMappingLinkDto
{
    [JsonPropertyName("externalSeriesId")]
    public string ExternalSeriesId { get; set; } = string.Empty;

    [JsonPropertyName("externalSeriesTitle")]
    public string? ExternalSeriesTitle { get; set; }
}
