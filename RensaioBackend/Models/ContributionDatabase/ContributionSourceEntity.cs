using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace RensaioBackend.Models.ContributionDatabase
{
    /// <summary>
    /// A canonical source definition in the local contributor database. Holds the
    /// immutable source identity (package + numeric source id) and the display metadata
    /// that is shared by every <see cref="ContributionSeriesEntity"/> row pointing at it.
    /// Mirrors the Cloudflare worker's <c>sources</c> table.
    /// </summary>
    public sealed class ContributionSourceEntity
    {
        /// <summary>BLOB(16) primary key (stored as a true 16-byte binary Guid).</summary>
        [JsonPropertyName("i")]
        public Guid Id { get; set; }
        
        [JsonPropertyName("p")]
        public string Package { get; init; } = string.Empty;

        [JsonPropertyName("s")]
        public long SourceId { get; init; }

        [JsonPropertyName("n")]
        public string SourceName { get; init; } = string.Empty;

        [JsonPropertyName("l")]
        public string SourceLanguage { get; init; } = string.Empty;

        [JsonPropertyName("b")]
        public DateTime? LastBatchExecutionUTC { get; init; }

        /// <summary>Replication version counter.</summary>
        [JsonPropertyName("v")]
        public int Version { get; set; }

        /// <summary>
        /// Computes the stable identifier for a source from its <c>package:sourceId</c>
        /// identity (MD5 of <c>package:sourceId</c> mapped to a Guid).
        /// </summary>
        public static Guid DeriveId(string package, long sourceId)
        {
            string identity = package + ":" + sourceId.ToString(CultureInfo.InvariantCulture);
            var hash = MD5.HashData(Encoding.UTF8.GetBytes(identity));
            return new Guid(hash);
        }
    }
}
