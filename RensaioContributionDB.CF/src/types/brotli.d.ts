/**
 * Minimal type declarations for the `brotli` npm package
 * (Brotli.js — pure JavaScript port).
 */
declare module 'brotli' {
  /** Compress a buffer; returns null on error. */
  export function compress(buffer: Uint8Array | Buffer, opts?: BrotliOptions | boolean): Uint8Array | null;

  /** Decompress a buffer; outSize may be omitted. */
  export function decompress(buffer: Uint8Array | Buffer, outSize?: number): Uint8Array;
}

interface BrotliOptions {
  mode?: 0 | 1 | 2;    // 0 generic, 1 text, 2 font
  quality?: number;    // 0 - 11
  lgwin?: number;      // window size
}