/**
 * AES-256-CBC crypto helpers for the export pipeline.
 *
 * The `AESKEY256IV` secret is: base64(32-byte key + 16-byte IV) = 64 base64 chars.
 * The exported `metadata.bin` payload is the tagged+compressed protobuf stream
 * encrypted with these helpers (one-shot AES-CBC per the approved pure-JS design).
 */
import { toUint8Array, type BlobValue } from './binary';

const KEY_LENGTH = 32; // AES-256
const IV_LENGTH = 16;  // CBC IV

/**
 * Parse the concatenated key+IV from the environment secret.
 * Returns the raw key bytes and IV bytes.
 */
export function parseAesKeyIv(encoded: string): { keyBytes: ArrayBuffer; iv: Uint8Array } {
  const raw = atob(encoded);
  if (raw.length !== KEY_LENGTH + IV_LENGTH) {
    throw new Error(
      `aeskey256iv must decode to ${KEY_LENGTH + IV_LENGTH} bytes, got ${raw.length}`
    );
  }

  const keyBytes = new ArrayBuffer(KEY_LENGTH);
  const keyView = new Uint8Array(keyBytes);
  const iv = new Uint8Array(IV_LENGTH);

  for (let i = 0; i < KEY_LENGTH; i += 1) {
    keyView[i] = raw.charCodeAt(i);
  }
  for (let i = 0; i < IV_LENGTH; i += 1) {
    iv[i] = raw.charCodeAt(KEY_LENGTH + i);
  }

  return { keyBytes, iv };
}

/**
 * Import the AES key for Web Crypto operations (encrypt + decrypt).
 */
async function importAesKey(
  keyBytes: ArrayBuffer,
  usages: Array<'encrypt' | 'decrypt'> = ['encrypt']
): Promise<CryptoKey> {
  return crypto.subtle.importKey(
    'raw',
    keyBytes,
    { name: 'AES-CBC' },
    false,               // not extractable
    usages
  );
}

/**
 * AES-256-CBC encrypt raw bytes. Returns the ciphertext (same length,
 * CBC does not add padding in Web Crypto).
 */
export async function encryptBytes(rawBytes: Uint8Array, aeskey256iv: string): Promise<Uint8Array> {
  const { keyBytes, iv } = parseAesKeyIv(aeskey256iv);
  const key = await importAesKey(keyBytes, ['encrypt']);
  const encrypted = await crypto.subtle.encrypt({ name: 'AES-CBC', iv }, key, rawBytes);
  return new Uint8Array(encrypted);
}

/**
 * AES-256-CBC decrypt raw bytes. Returns the plaintext.
 */
export async function decryptBytes(cipherBytes: Uint8Array, aeskey256iv: string): Promise<Uint8Array> {
  const { keyBytes, iv } = parseAesKeyIv(aeskey256iv);
  const key = await importAesKey(keyBytes, ['decrypt']);
  const decrypted = await crypto.subtle.decrypt({ name: 'AES-CBC', iv }, key, cipherBytes);
  return new Uint8Array(decrypted);
}

/**
 * Transform raw binary data for the export payload:
 *   BLOB → AES-256-CBC encrypt → Uint8Array
 */
export async function encryptBlob(rawData: BlobValue, aeskey256iv: string): Promise<Uint8Array> {
  const bytes = toUint8Array(rawData);
  if (bytes === null) {
    throw new Error('Cannot encrypt empty source data');
  }
  return encryptBytes(bytes, aeskey256iv);
}

/**
 * Base64-encode a Uint8Array (UTF-8 safe chunked btoa).
 */
export function bytesToBase64(bytes: Uint8Array): string {
  let binary = '';
  const chunkSize = 0x8000; // 32KB chunks to avoid call-stack limits
  for (let i = 0; i < bytes.length; i += chunkSize) {
    binary += String.fromCharCode(...bytes.subarray(i, i + chunkSize));
  }
  return btoa(binary);
}
