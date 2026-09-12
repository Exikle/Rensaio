using System.Text.Json.Serialization;
using RensaioBackend.Models.Enums;

namespace RensaioBackend.Models.Dto;

public class MetaDataLinkRequestDto
{
    [JsonPropertyName("seriesId")]
    public Guid SeriesId { get; set; }
}

public class MetaDataRefreshRequestDto
{
    [JsonPropertyName("seriesId")]
    public Guid SeriesId { get; set; }

    [JsonPropertyName("provider")]
    public ExternalSeriesProvider? Provider { get; set; }
}

public class MetaDataLinkAllResultDto
{
    [JsonPropertyName("processedSeries")]
    public int ProcessedSeries { get; set; }

    [JsonPropertyName("skippedSeries")]
    public int SkippedSeries { get; set; }

    [JsonPropertyName("linkedProviderEntries")]
    public int LinkedProviderEntries { get; set; }

    [JsonPropertyName("suggestedEntries")]
    public int SuggestedEntries { get; set; }
}

public class MetaDataSeriesViewDto
{
    [JsonPropertyName("seriesId")]
    public Guid SeriesId { get; set; }

    [JsonPropertyName("seriesTitle")]
    public string? SeriesTitle { get; set; }

    [JsonPropertyName("mappings")]
    public List<MetaDataSeriesMappingViewDto> Mappings { get; set; } = [];
}

public class MetaDataSeriesMappingViewDto
{
    [JsonPropertyName("provider")]
    public ExternalSeriesProvider Provider { get; set; }

    [JsonPropertyName("externalSeriesId")]
    public string ExternalSeriesId { get; set; } = string.Empty;

    [JsonPropertyName("externalSeriesTitle")]
    public string? ExternalSeriesTitle { get; set; }

    [JsonPropertyName("linkedSitesIds")]
    public List<string> LinkedSitesIds { get; set; } = [];

    [JsonPropertyName("alternativeTitles")]
    public List<string> AlternativeTitles { get; set; } = [];

    // MetaData is intentionally omitted from this view DTO (it can be large);
    // it remains available in the SeriesMappings table.
}