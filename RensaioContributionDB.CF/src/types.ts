/// <reference types="@cloudflare/workers-types" />

/**
 * Cloudflare Worker environment bindings.
 */
export interface Env {
  // D1 database binding
  DB: D1Database;

  // GitHub API token (sensitive — set via: wrangler secret put GITHUB_TOKEN)
  GITHUB_TOKEN: string;

  // Export target repository, e.g. "owner/repo-name"
  EXPORT_GITHUB_REPO: string;

  // Export target path within the repository, e.g. "data/"
  EXPORT_PATH: string;

  // AES-256-CBC key and IV concatenated, base64-encoded (32B key + 16B IV = 64 base64 chars)
  // Used for encryption of the exported metadata.bin payload.
  AESKEY256IV: string;

  // Export compression: "0" = ZSTD, "1" = Brotli.
  EXPORT_COMPRESSION?: string;
}

/**
 * Entity lists of the ContributionSnapshotV1 wire format.
 * Each is keyed by the short JSON property name used by the backend.
 */
export type EntityType = 't' | 'm' | 's' | 'i' | 'd';

/**
 * Supported compression algorithms for the export pipeline (EXPORT_COMPRESSION var).
 * `0` → ZSTD, `1` → Brotli.
 */
export const COMPRESSION_ZSTD = 0;
export const COMPRESSION_BROTLI = 1;

/**
 * All supported entity lists for validation.
 */
export const SUPPORTED_ENTITY_TYPES: ReadonlySet<string> = new Set(['t', 'm', 's', 'i', 'd']);

/**
 * Schema version of the ContributionSnapshotV1 wire format.
 */
export const SNAPSHOT_SCHEMA_VERSION = 1;

/**
 * Replication version semantics for entity rows.
 * `0` → add/update (upsert), `-1` → logical delete (tombstone).
 */
export const REPLICATION_ADD_OR_UPDATE = 0;
export const REPLICATION_DELETE = -1;

/**
 * Number of days after which archived records are hard-deleted by the daily scrub.
 */
export const ARCHIVE_RETENTION_DAYS = 30;

/**
 * Replication singleton row id (id = 1).
 */
export const REPLICATION_ROW_ID = 1;
