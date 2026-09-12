/**
 * ── ContributionSnapshotV1 wire format ────────────────────────────────────────
 * Mirrors RensaioBackend.Services.Contributions.Snapshot.ContributionSnapshotV1
 * and the entity classes in Models/ContributionDatabase. Each entity carries a
 * `v` (replication version) field: 0 = add/update (upsert), -1 = delete.
 *
 * The top-level `v` is the collection Replication Version Number the client
 * observed (used for context; the server merges blindly).
 *
 * Guids are serialized as their canonical lowercase-hex text form.
 */

/** TitleEntity — canonical title; id = MD5(normalized title). */
export interface TitleEntityPayload {
  i: string;           // Guid (MD5 of normalized title)
  t: string;           // title text
  v: number;           // 0 | -1
}

/** MappingTitleEntity — join mapping ↔ title. */
export interface MappingTitleEntityPayload {
  m: string;           // MappingId Guid
  t: string;           // TitleId Guid
  v: number;           // 0 | -1
}

/** ContributionRecordV1 — embedded in ContributionSeriesEntity.data. */
export interface ContributionRecordV1Payload {
  v: number;           // schema version (1)
  i: string;           // TitleId Guid
  t: string | null;    // ThumbnailUrl
  s: number;           // Status
  p: boolean;          // SeenInPopular
  e: boolean;          // SeenInLatest
  l: number | null;    // LastChapter (decimal)
  u: string | null;    // LastUpdateUTC
}

/** ContributionSourceEntity — canonical source definition. */
export interface ContributionSourceEntityPayload {
  i: string;           // SourceId Guid (MD5("package:sourceId"))
  p: string;           // Package
  s: number;           // SourceId (long)
  n: string;           // SourceName
  l: string;           // SourceLanguage
  b: string | null;    // LastBatchExecutionUTC
  v: number;           // 0 | -1
}

/** ContributionSeriesEntity — per-series source entry. */
export interface ContributionSeriesEntityPayload {
  i: string;                       // SeriesId Guid
  m: string;                       // MappingId Guid
  s: string;                       // SourceId Guid
  d: ContributionRecordV1Payload;  // embedded record
  v: number;                       // 0 | -1
}

/** ContributionMetadataEntity — metadata provider link. */
export interface ContributionMetadataEntityPayload {
  i: string;           // MetadataId Guid
  m: string;           // MappingId Guid
  p: number;           // ProviderId (ExternalSeriesProvider enum)
  k: string | null;    // ProviderKey
  s: number;           // MappingStatus (SeriesMappingStatus enum)
  u: string | null;    // LinkedDate
  v: number;           // 0 | -1
}

/**
 * Request body for POST /upload.
 * Field names are the one-letter JSON keys used by the backend.
 */
export interface UploadRequest {
  e?: number;   // SchemaVersion — must be 1
  u?: string;   // GeneratedUtc (ISO 8601)
  v?: number;   // collection Replication Version Number (client baseline)
  t?: TitleEntityPayload[];                     // titles
  m?: MappingTitleEntityPayload[];              // mapping_titles
  s?: ContributionSourceEntityPayload[];        // sources (canonical)
  i?: ContributionSeriesEntityPayload[];        // series (per-series entries)
  d?: ContributionMetadataEntityPayload[];      // metadata
}

/**
 * Request body for POST /admin/ban.
 */
export interface BanRequest {
  contributor_id: string;
  ban_reason: string;
}
