using RensaioBackend.Models.Enums;
using System.Text.Json.Serialization;

namespace RensaioBackend.Models.ContributionDatabase
{
    /// <summary>
    /// Schema v1 record serialized as the <c>Data</c> column of a contribution source row.
    /// Mirrors the wire format consumed by the (future) cloud contribution service.
    /// Source identity/display metadata (Package, SourceId, SourceName, SourceLanguage,
    /// LastBatchExecutionUTC) live on <see cref="ContributionSourceEntity"/> and are not
    /// part of this per-series payload.
    /// </summary>
    public class ContributionRecordV1
    {
        public const int CurrentSchemaVersion = 1;

        [JsonPropertyName("v")]
        public int SchemaVersion { get; set; } = CurrentSchemaVersion;
        [JsonPropertyName("i")]
        public Guid TitleId { get; set; }
        [JsonPropertyName("t")]
        public string? ThumbnailUrl { get; set; }
        [JsonPropertyName("s")]
        public int Status { get; set; }
        [JsonPropertyName("p")]
        public bool SeenInPopular { get; set; }
        [JsonPropertyName("e")]
        public bool SeenInLatest { get; set; }
        [JsonPropertyName("l")]
        public decimal? LastChapter { get; set; }
        [JsonPropertyName("u")]
        public DateTime? LastUpdateUTC { get; set; }

    }
}
