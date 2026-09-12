using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace RensaioBackend.Models.ContributionDatabase
{
    /// <summary>
    /// A source entry in the local contributor database. <c>Id</c> is the
    /// contributor-provided identifier (TEXT), not a UUID, mirroring the Cloudflare
    /// worker's <c>sources</c> table. The canonical source metadata is stored on the
    /// related <see cref="ContributionSourceEntity"/> via <see cref="SourceId"/>.
    /// </summary>
    public class ContributionSeriesEntity
    {
        /// <summary>Contributor-provided identifier (MD5 -> Bytes).</summary>
        /// 
        [JsonPropertyName("i")]
        public Guid Id { get; set; }

        /// <summary>BLOB(16) FK → Mappings.Id.</summary>
        [JsonPropertyName("m")]
        public Guid MappingId { get; set; }

        /// <summary>BLOB(16) FK → ContributionSourceEntity.Id.</summary>
        [JsonPropertyName("s")]
        public Guid SourceId { get; set; }

        [JsonPropertyName("d")]
        /// <summary>Serialized source data (TEXT).</summary>
        public ContributionRecordV1 Data { get; set; }

        /// <summary>Replication version counter.</summary>
        [JsonPropertyName("v")]
        public int Version { get; set; }

        public virtual MappingEntity? Mapping { get; set; }

        public virtual ContributionSourceEntity? Source { get; set; }

        /// <summary>
        /// Computes the stable identifier for a source from its <c>package:sourceId:url</c>
        /// identity (MD5 of <c>package:sourceId</c> mapped to a Guid).
        /// </summary>
        public static Guid DeriveId(string package, long sourceId, string url)
        {
            string identity = package + ":" + sourceId.ToString(CultureInfo.InvariantCulture) + ":" + url.ToLowerInvariant();
            var hash = MD5.HashData(Encoding.UTF8.GetBytes(identity));
            return new Guid(hash);
        }

    }
}

