using System.IO.Compression;

namespace RensaioBackend.Services.Contributions.Snapshot
{
    /// <summary>
    /// Decodes a `metadata.bin` payload produced by the Cloudflare worker export:
    ///
    ///   metadata.bin (GitHub raw) = AES-256-CBC( [tag byte][compressed protobuf] )
    ///
    /// The worker pushes base64 to the GitHub Contents API, but GitHub stores the
    /// DECODED binary — so `raw.githubusercontent.com/.../metadata.bin` serves the
    /// encrypted stream directly. There is deliberately NO base64 layer on the client
    /// side: metadata.bin is already binary.
    ///
    /// Layout (mirror of worker's utils/crypto.ts + utils/compression.ts):
    ///   - AESKEY256IV is base64(32B key + 16B IV) = 64 base64 chars.
    ///   - After AES decrypt: byte[0] = compression tag (0x00 = ZSTD, 0x01 = Brotli),
    ///     followed by the compressed protobuf bytes.
    /// </summary>
    internal static class ContributionExportDecoder
    {
        public const byte TagZstd = 0x00;
        public const byte TagBrotli = 0x01;

        /// <summary>
        /// Full pipeline: AES-256-CBC decrypt → strip tag → decompress.
        /// Returns the raw protobuf bytes ready for <see cref="ContributionProtobufCodec"/>.
        /// </summary>
        public static byte[] DecodeMetadataBin(byte[] encryptedPayload, string aeskey256iv)
        {
            byte[] clear = AesDecrypt(encryptedPayload, aeskey256iv);
            if (clear.Length < 1)
                throw new InvalidDataException("Decrypted payload is empty — missing compression tag");

            byte tag = clear[0];
            byte[] body = new byte[clear.Length - 1];
            Array.Copy(clear, 1, body, 0, body.Length);

            return tag switch
            {
                TagZstd => ZstdDecompress(body),
                TagBrotli => BrotliDecompress(body),
                _ => throw new InvalidDataException($"Unknown compression tag 0x{tag:X2}")
            };
        }

        private static byte[] AesDecrypt(byte[] cipher, string aeskey256iv)
        {
            byte[] keyIv = Convert.FromBase64String(aeskey256iv);
            if (keyIv.Length != 48)
                throw new InvalidDataException($"AESKEY256IV must decode to 48 bytes, got {keyIv.Length}");

            byte[] key = new byte[32];
            byte[] iv = new byte[16];
            Array.Copy(keyIv, 0, key, 0, 32);
            Array.Copy(keyIv, 32, iv, 0, 16);

            using var aes = System.Security.Cryptography.Aes.Create();
            aes.Key = key;
            aes.IV = iv;
            aes.Mode = System.Security.Cryptography.CipherMode.CBC;
            aes.Padding = System.Security.Cryptography.PaddingMode.None;

            using var decryptor = aes.CreateDecryptor();
            return decryptor.TransformFinalBlock(cipher, 0, cipher.Length);
        }

        private static byte[] ZstdDecompress(byte[] body)
        {
            // ZstdSharp.Port — pure managed Zstandard.
            using var input = new MemoryStream(body);
            using var output = new MemoryStream();
            using (var decompressor = new ZstdSharp.DecompressionStream(input))
            {
                decompressor.CopyTo(output);
            }
            return output.ToArray();
        }

        private static byte[] BrotliDecompress(byte[] body)
        {
            using var input = new MemoryStream(body);
            using var output = new MemoryStream();
            using var brotli = new BrotliStream(input, CompressionMode.Decompress);
            brotli.CopyTo(output);
            return output.ToArray();
        }
    }
}