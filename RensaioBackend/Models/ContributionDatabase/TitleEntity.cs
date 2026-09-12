using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace RensaioBackend.Models.ContributionDatabase
{
    /// <summary>
    /// A canonical title in the local contributor database. Mirrors the Cloudflare
    /// contributor worker's <c>titles</c> table with version tracking for replication.
    /// </summary>
    public class TitleEntity
    {
        /// <summary>
        /// BLOB(16) primary key (stored as a true 16-byte binary Guid).
        /// Derivable: <see cref="DeriveId(string)"/> — the MD5 of the normalized title,
        /// so identical titles share one row and dedup happens purely on the primary key.
        /// </summary>
        ///
        [JsonPropertyName("i")]
        public Guid Id { get; set; }

        /// <summary>Title text. NOT NULL.</summary>
        [JsonPropertyName("t")]
        public string Title { get; set; } = string.Empty;

        /// <summary>Replication version counter.</summary>
        [JsonPropertyName("v")]
        public int Version { get; set; }

        /// <summary>
        /// Computes the stable identifier for a title from its normalized
        /// (trimmed / lower-cased) form: MD5 of the UTF-8 bytes mapped to a Guid.
        /// </summary>
        public static Guid DeriveId(string title)
        {
            string identity = Normalize(title);
            var hash = MD5.HashData(Encoding.UTF8.GetBytes(identity));
            return new Guid(hash);
        }

        /// <summary>
        /// Normalizes a title for identity purposes: trims surrounding whitespace
        /// and lower-cases it. Empty / whitespace-only input collapses to the empty string.
        /// </summary>
        public static string Normalize(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;
            return value.Trim().ToLowerInvariant();
        }
    }
}