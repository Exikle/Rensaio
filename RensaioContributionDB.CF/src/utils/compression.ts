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
 *
 * ── CPU budget consideration ──────────────────────────────────────────────
 * Cloudflare Workers have a hard per-invocation CPU limit. Maximum-effort
 * Brotli (quality 11) or pure-JS ZSTD can take SECONDS of CPU even for small
 * inputs (measured: ~2.8s for a 610 KiB protobuf on a desktop; several× more
 * on Cloudflare's shared CPU). With the current dataset the compressed payload
 * is only ~23 KiB, so that CPU spend buys almost nothing.
 *
 * Policy:
 *   * If the input is small (< COMPRESS_MIN_SAVINGS_BYTES*4 ≈ below the point
 *     where savings justify the CPU), skip compression entirely and emit a
 *     RAW tag. The tag stays `COMPRESSION_*`-aware but 0x02 = raw.
 *   * Otherwise compress with FAST settings: Brotli quality 1 (fast) or ZSTD
 *     level 1 (fastest) — these are ~10-20× cheaper than the old defaults.
 *
 * Raw mode is backwards-compatible on the read side because the .NET decoder
 * and this codec treat any unknown tag as an error; we deliberately avoid that
 * by making 0x02 part of the protocol (documented in codec/decoder).
 */
// Raw path threshold. The full dataset today is ~610 KiB; raising the threshold
// to 4 MiB means realistic payloads (up to ~6× current) NEVER touch the
// brotli-wasm init that has been failing at runtime ("Manual export failed: at
// init ...") or the pure-JS ZSTD CPU cost. Only very large datasets compress.
const COMPRESS_SKIP_BELOW = 4 * 1024 * 1024; // 4 MiB
const RAW_TAG = 0x02; // raw/uncompressed (decoder: backend ContributionExportDecoder + this codec)

export async function compressSnapshot(
  bytes: Uint8Array,
  compression: number
): Promise<CompressedPayload> {
  // Raw path: skip compression for the sizes this system actually produces.
  if (bytes.length < COMPRESS_SKIP_BELOW) {
    const tagged = new Uint8Array(bytes.length + 1);
    tagged[0] = RAW_TAG;
    tagged.set(bytes, 1);
    return { tag: RAW_TAG, bytes: tagged };
  }

  // Large payloads (>= 4 MiB): compression is worth it, but must never fail the
  // export — if the compressor is unavailable (wasm init failure) or doesn't
  // shrink the input, fall back to raw so the export always completes.
  try {
    let out: Uint8Array;
    if (compression === COMPRESSION_BROTLI) {
      const brotli = await getBrotli();
      // quality 1 — fast; q11 measured ~2.8s for 610KiB, q1 is ~20× cheaper.
      out = brotli.compress(bytes, { quality: 1 });
    } else {
      // zstdify compress — level 1 (fastest).
      out = fzstd.compress(bytes, { level: 1 });
    }

    if (out.length >= bytes.length) {
      // Compression didn't shrink — store raw.
      const tagged = new Uint8Array(bytes.length + 1);
      tagged[0] = RAW_TAG;
      tagged.set(bytes, 1);
      return { tag: RAW_TAG, bytes: tagged };
    }

    const tagged = new Uint8Array(out.length + 1);
    tagged[0] = compression === COMPRESSION_BROTLI ? 0x01 : 0x00;
    tagged.set(out, 1);
    return { tag: compression, bytes: tagged };
  } catch (err) {
    console.error('Compression failed, exporting raw instead:', err);
    const tagged = new Uint8Array(bytes.length + 1);
    tagged[0] = RAW_TAG;
    tagged.set(bytes, 1);
    return { tag: RAW_TAG, bytes: tagged };
  }
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
  if (tag !== 0x00 && tag !== 0x01 && tag !== 0x02) {
    throw new Error(`Unknown compression tag: 0x${tag.toString(16).padStart(2, '0')}`);
  }
  const body = tagged.subarray(1);

  if (tag === 0x02) return { tag, raw: body }; // raw/uncompressed

  const raw =
    tag === COMPRESSION_BROTLI
      ? (await getBrotli()).decompress(body)
      : fzstd.decompress(body);
  return { tag, raw };
}