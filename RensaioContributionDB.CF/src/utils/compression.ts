/**
 * Compression for the export pipeline.
 *
 * Config: EXPORT_COMPRESSION var — "0" = ZSTD (default), "1" = Brotli.
 * Output layout: `[ 1 byte compression tag ][ compressed payload ]`
 *
 * Table:
 *   tag 0x00  → ZSTD   (zstdify — pure JS/TS RFC 8878 implementation)
 *   tag 0x01  → Brotli (brotli-wasm — wasm compiled Rust Brotli)
 *
 * The tag byte is written BEFORE the compressed stream so a decryptor can
 * select the correct decompressor after AES decrypt.
 *
 * Brotli is initialized lazily (wasm) and memoized so the import cost is paid
 * only when compression mode 1 is actually requested.
 */
import * as fzstd from 'zstdify';
import brotliWasmInit from 'brotli-wasm';
import { COMPRESSION_BROTLI, COMPRESSION_ZSTD } from '../types';

export interface CompressedPayload {
  tag: number;          // 0 = zstd, 1 = brotli
  bytes: Uint8Array;    // tag byte + compressed stream
}

type BrotliModule = {
  compress: (input: Uint8Array, options?: { quality?: number }) => Uint8Array;
  decompress: (input: Uint8Array) => Uint8Array;
};

let brotliPromise: Promise<BrotliModule> | null = null;

/**
 * Lazily resolve the Brotli wasm module (memoized).
 */
function getBrotli(): Promise<BrotliModule> {
  if (!brotliPromise) {
    brotliPromise = brotliWasmInit as unknown as Promise<BrotliModule>;
  }
  return brotliPromise;
}

/**
 * Resolve the configured compression id from the ENV var.
 * Defaults to ZSTD (0). Accepts "0"/"1" or "zstd"/"brotli" (case-insensitive).
 */
export function resolveCompression(raw: string | undefined): number {
  if (raw === undefined || raw === null) return COMPRESSION_ZSTD;
  const v = raw.trim().toLowerCase();
  if (v === '1' || v === 'brotli') return COMPRESSION_BROTLI;
  return COMPRESSION_ZSTD;
}

/**
 * Compress a protobuf byte array and prepend the compression tag byte.
 */
export async function compressSnapshot(
  bytes: Uint8Array,
  compression: number
): Promise<CompressedPayload> {
  let out: Uint8Array;
  if (compression === COMPRESSION_BROTLI) {
    const brotli = await getBrotli();
    out = brotli.compress(bytes, { quality: 11 });
  } else {
    // zstdify compress (pure JS) — level 3 balances ratio/speed.
    out = fzstd.compress(bytes, { level: 3 });
  }

  const tagged = new Uint8Array(out.length + 1);
  tagged[0] = compression === COMPRESSION_BROTLI ? 0x01 : 0x00;
  tagged.set(out, 1);
  return { tag: compression, bytes: tagged };
}

/**
 * Strip the 1-byte tag and decompress. Returns the compression id + raw bytes.
 * Used by tests/verification.
 */
export async function decompressSnapshot(tagged: Uint8Array): Promise<{ tag: number; raw: Uint8Array }> {
  if (tagged.length < 1) {
    throw new Error('Tagged payload is empty — missing compression tag');
  }
  const tag = tagged[0];
  if (tag !== 0x00 && tag !== 0x01) {
    throw new Error(`Unknown compression tag: 0x${tag.toString(16).padStart(2, '0')}`);
  }
  const body = tagged.subarray(1);

  const raw =
    tag === COMPRESSION_BROTLI
      ? (await getBrotli()).decompress(body)
      : fzstd.decompress(body);
  return { tag, raw };
}