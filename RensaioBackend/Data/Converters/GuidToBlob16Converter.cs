using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace RensaioBackend.Data.Converters
{
    /// <summary>
    /// EF value converter that stores <see cref="Guid"/> primary/foreign keys as true
    /// 16-byte binary <c>BLOB(16)</c> columns (rather than EF Core SQLite's default
    /// TEXT representation). The 16 bytes are stored in RFC 4122 canonical field order
    /// (the same order the GUID's 32 hex characters appear), matching the wire format
    /// used by the Cloudflare contributor database.
    /// </summary>
    public sealed class GuidToBlob16Converter : ValueConverter<Guid, byte[]>
    {
        public GuidToBlob16Converter()
            : base(
                // Guid -> byte[]
                guid => GuidToBytes(guid),
                // byte[] -> Guid
                bytes => BytesToGuid(bytes))
        {
        }

        /// <summary>
        /// Serializes a <see cref="Guid"/> into its canonical 16-byte representation.
        /// </summary>
        public static byte[] GuidToBytes(Guid guid)
        {
            string hex = guid.ToString("N"); // 32 lowercase hex chars, canonical field order
            var bytes = new byte[16];
            for (int i = 0; i < 16; i++)
            {
                var hi = HexDigit(hex[i * 2]);
                var lo = HexDigit(hex[i * 2 + 1]);
                bytes[i] = (byte)((hi << 4) | lo);
            }
            return bytes;
        }

        /// <summary>
        /// Deserializes a 16-byte <c>BLOB</c> back into a <see cref="Guid"/>.
        /// </summary>
        public static Guid BytesToGuid(byte[] bytes)
        {
            if (bytes == null || bytes.Length != 16)
                throw new ArgumentException("Guid BLOB must be exactly 16 bytes.");

            var sb = new System.Text.StringBuilder(36);
            for (int i = 0; i < 16; i++)
            {
                if (i == 4 || i == 6 || i == 8 || i == 10)
                    sb.Append('-');
                sb.Append(HexChar((bytes[i] >> 4) & 0x0F));
                sb.Append(HexChar(bytes[i] & 0x0F));
            }
            return Guid.Parse(sb.ToString());
        }

        private static int HexDigit(char c)
        {
            if (c >= '0' && c <= '9')
                return c - '0';
            if (c >= 'a' && c <= 'f')
                return c - 'a' + 10;
            if (c >= 'A' && c <= 'F')
                return c - 'A' + 10;
            throw new ArgumentException("Invalid hex digit in GUID: " + c);
        }

        private static char HexChar(int nibble)
        {
            return nibble < 10 ? (char)('0' + nibble) : (char)('a' + nibble - 10);
        }
    }
}