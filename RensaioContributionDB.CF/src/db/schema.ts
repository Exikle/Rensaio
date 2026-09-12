/// <reference types="@cloudflare/workers-types" />
import type { BlobValue } from '../utils/binary';

/**
 * Represents a row in the `contributors` D1 table (auth only).
 */
export interface Contributor {
  id: string;          // UUID
  admin: number;       // 0/1 — admin privileges
  active: number;      // 0/1 — 0 = banned
  ban_reason: string | null;
  last_change: string; // ISO 8601 UTC datetime
}

/**
 * Represents a row in the `titles` D1 table.
 * Mirrors the backend's `TitleEntity` (id is MD5(normalized title) as text Guid).
 */
export interface Title {
  id: string;                // MD5(normalized title) as text Guid
  title: string;
  replication_version: number | null; // current replication version; -1 delete
  contributor_id: string | null;     // last contributor that touched the row
  archived_at: string | null;
}

/**
 * Represents a row in the `mappings` D1 table (MappingEntity aggregate root).
 */
export interface Mapping {
  id: string;  // UUID/Guid
  archived_at: string | null; // set when all references are archived (ban scrub)
}

/**
 * Represents a row in the `mapping_titles` D1 table (MappingTitleEntity join).
 */
export interface MappingTitle {
  mapping_id: string;  // FK → mappings.id
  title_id: string;    // FK → titles.id
  last_change: string;
  archived_at: string | null;
}

/**
 * Represents a row in the `sources` D1 table (canonical ContributionSourceEntity).
 * `id` is MD5("package:sourceId") as text Guid.
 */
export interface Source {
  id: string;              // MD5("package:sourceId") as text Guid
  package: string;
  source_id: number;
  source_name: string;
  source_language: string;
  last_batch_utc: string | null;
  contributor_id: string | null;       // last contributor that touched the row
  replication_version: number | null;  // current replication version; -1 delete
  archived_at: string | null;
}

/**
 * Represents a row in the `series` D1 table (ContributionSeriesEntity).
 * ContributionRecordV1 is flattened into columns.
 * `id` is MD5("package:sourceId:url") as text Guid.
 */
export interface Series {
  id: string;              // MD5("package:sourceId:url") as text Guid
  mapping_id: string;      // FK → mappings.id
  source_id: string;       // FK → sources.id
  title_id: string;        // FK → titles.id (ContributionRecordV1.TitleId)
  thumbnail_url: string | null;
  status: number;
  seen_in_popular: number; // 0/1
  seen_in_latest: number;  // 0/1
  last_chapter: number | null;
  last_update_utc: string | null;
  contributor_id: string | null;       // stamped server-side from uploader
  replication_version: number | null;  // current replication version; -1 delete
  archived_at: string | null;
}

/**
 * Represents a row in the `metadata` D1 table (ContributionMetadataEntity).
 */
export interface Metadata {
  id: string;              // client-generated UUID
  mapping_id: string;      // FK → mappings.id
  provider_id: number;     // ExternalSeriesProvider enum (0..14)
  provider_key: string | null;
  mapping_status: number;  // SeriesMappingStatus enum (0..5)
  linked_date: string | null;
  contributor_id: string | null;       // stamped server-side from uploader
  replication_version: number | null;  // current replication version; -1 delete
  archived_at: string | null;
}

/**
 * Represents the singleton `replication` D1 row (id = 1).
 */
export interface Replication {
  id: number;       // always 1
  version: number;  // current Replication Version Number (bumped daily)
  bumped_at: string;
}

/**
 * Legacy alias kept for files that previously imported `BlobValue` transitively.
 * Not used by the new entity model.
 */
export type { BlobValue };
