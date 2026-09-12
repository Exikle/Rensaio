/**
 * Streaming Protocol Buffers encoder — dependency-free, Worker-compatible.
 *
 * Written against `proto/contribution_snapshot_v1.proto` (field numbers stable).
 * The writer accumulates output into bounded chunks so very large snapshots do
 * not materialize one giant byte array; `finish()` concatenates once.
 *
 * Wire type encoding (standard protobuf):
 *   varint  — uint32/uint64/int32/int64 (int32 negative → sign-extended 64-bit)
 *   length-delimited — string / bytes / nested message
 *   fixed64 — double
 */

const DEFAULT_CHUNK_SIZE = 1024 * 1024; // 1 MiB

/**
 * 64-bit varint of an unsigned value, little-endian base-128.
 */
function encodeVarint(value: number): Uint8Array {
  const out: number[] = [];
  let v = value;
  do {
    let byte = v & 0x7f;
    v >>>= 7;
    if (v !== 0) byte |= 0x80;
    out.push(byte);
  } while (v !== 0);
  return Uint8Array.from(out);
}

/**
 * Protobuf int32: negative values are sign-extended to 64 bits then varint
 * encoded (10-byte form), matching reference implementations.
 */
function encodeInt32(value: number): Uint8Array {
  // Sign-extend to 64-bit (JS numbers are IEEE doubles; use low 32 then mask).
  const mask = 0xffffffff;
  let v = value & mask;
  if (value < 0) {
    // two's complement 64-bit representation of a signed 32-bit value:
    // the upper 32 bits are all 1s.
    v = v | 0xffffffff00000000;
  }
  return encodeVarint(v);
}

/**
 * Protobuf uint32.
 */
function encodeUint32(value: number): Uint8Array {
  return encodeVarint(value & 0xffffffff);
}

/**
 * Streaming writer that appends protobuf fields into bounded chunks.
 */
export class ProtoWriter {
  private readonly chunks: Uint8Array[] = [];
  private current: Uint8Array = new Uint8Array(DEFAULT_CHUNK_SIZE);
  private offset = 0;

  private flushIfNeeded(needed: number): void {
    if (this.offset + needed <= this.current.length) return;
    const flushLength = Math.max(this.offset, 1);
    this.chunks.push(this.current.subarray(0, flushLength));
    this.current = new Uint8Array(Math.max(DEFAULT_CHUNK_SIZE, needed));
    this.offset = 0;
  }

  private append(bytes: Uint8Array): void {
    this.flushIfNeeded(bytes.length);
    if (bytes.length + this.offset > this.current.length) {
      // single piece larger than a chunk: flush and push directly
      const left = this.current.subarray(0, this.offset);
      if (left.length > 0) this.chunks.push(left);
      this.chunks.push(bytes);
      this.current = new Uint8Array(DEFAULT_CHUNK_SIZE);
      this.offset = 0;
      return;
    }
    this.current.set(bytes, this.offset);
    this.offset += bytes.length;
  }

  /** varint field. */
  varint(field: number, value: number): void {
    this.append(encodeVarint((field << 3) | 0));
    this.append(encodeVarint(value & 0xffffffff_ffffffff));
  }

  /** int32 (sign-extended) field. */
  int32(field: number, value: number): void {
    this.append(encodeVarint((field << 3) | 0));
    this.append(encodeInt32(value));
  }

  /** uint32 field. */
  uint32(field: number, value: number): void {
    this.append(encodeVarint((field << 3) | 0));
    this.append(encodeUint32(value));
  }

  /** int64 field (low 64 bits). */
  int64(field: number, value: number): void {
    this.append(encodeVarint((field << 3) | 0));
    this.append(encodeVarint(value & 0xffffffff_ffffffff));
  }

  /** bool field. */
  bool(field: number, value: boolean): void {
    this.append(encodeVarint((field << 3) | 0));
    this.append(encodeVarint(value ? 1 : 0));
  }

  /** string field (UTF-8, length-delimited). */
  string(field: number, value: string | null | undefined): void {
    if (value === null || value === undefined) return;
    const bytes = new TextEncoder().encode(value);
    this.append(encodeVarint((field << 3) | 2));
    this.append(encodeVarint(bytes.length));
    this.append(bytes);
  }

  /** Embedded message field (length-delimited). */
  message(field: number, messageBytes: Uint8Array): void {
    this.append(encodeVarint((field << 3) | 2));
    this.append(encodeVarint(messageBytes.length));
    this.append(messageBytes);
  }

  /** Fixed 8-byte double field (little-endian). */
  double(field: number, value: number | null | undefined): void {
    if (value === null || value === undefined) return;
    const buf = new ArrayBuffer(8);
    new DataView(buf).setFloat64(0, value, true);
    this.append(encodeVarint((field << 3) | 1));
    this.append(new Uint8Array(buf));
  }

  /** Concatenate all chunks into a single Uint8Array. */
  finish(): Uint8Array {
    if (this.chunks.length === 0) {
      return this.current.subarray(0, this.offset);
    }
    const total = this.chunks.reduce((sum, c) => sum + c.length, this.offset);
    const out = new Uint8Array(total);
    let pos = 0;
    for (const chunk of this.chunks) {
      out.set(chunk, pos);
      pos += chunk.length;
    }
    out.set(this.current.subarray(0, this.offset), pos);
    return out;
  }
}