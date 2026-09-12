using System.Text.Json.Serialization;

namespace RensaioBackend.Models.ContributionDatabase
{
    /// <summary>
    /// Join row linking a <see cref="MappingEntity"/> to a <see cref="TitleEntity"/>.
    /// Composite primary key (MappingId, TitleId).
    /// </summary>
    public class MappingTitleEntity
    {
        /// <summary>BLOB(16) FK → Mappings.Id.</summary>
        [JsonPropertyName("m")]
        public Guid MappingId { get; set; }

        /// <summary>BLOB(16) FK → Titles.Id.</summary>
        [JsonPropertyName("t")]
        public Guid TitleId { get; set; }

        /// <summary>Replication version counter.</summary>
        [JsonPropertyName("v")]
        public int Version { get; set; }

        public virtual MappingEntity? Mapping { get; set; }
        public virtual TitleEntity? Title { get; set; }
    }
}