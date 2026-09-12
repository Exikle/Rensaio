using RensaioBackend.Models.Database;
using System.Text.Json.Serialization;

namespace RensaioBackend.Models.ContributionDatabase
{
    /// <summary>
    /// A metadata link entry in the local contributor database. Identity is a versioned
    /// (ProviderId, ProviderKey) link within a mapping, mirroring the Cloudflare worker's
    /// <c>metadata</c> table.
    /// </summary>
    public class ContributionMetadataEntity
    {
        /// <summary>BLOB(16) primary key (stored as a true 16-byte binary Guid).</summary>
        [JsonPropertyName("i")]
        public Guid Id { get; set; } = Guid.NewGuid();

        /// <summary>BLOB(16) FK → Mappings.Id.</summary>
        [JsonPropertyName("m")]
        public Guid MappingId { get; set; }

        /// <summary>Numeric provider identifier (e.g. anilist/mal enum).</summary>
        [JsonPropertyName("p")]
        public int ProviderId { get; set; }

        /// <summary>Provider-specific key (e.g. AniList id).</summary>
        [JsonPropertyName("k")]
        public string? ProviderKey { get; set; }

        [JsonPropertyName("s")]
        public SeriesMappingStatus MappingStatus { get; set; } = SeriesMappingStatus.Unmatched;

        [JsonPropertyName("u")]
        public DateTime? LinkedDate { get; set; }

        /// <summary>Replication version counter.</summary>
        [JsonPropertyName("v")]
        public int Version { get; set; }

        public virtual MappingEntity? Mapping { get; set; }
    }
}