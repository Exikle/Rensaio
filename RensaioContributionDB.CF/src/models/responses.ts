/**
 * Error response body used across all endpoints.
 */
export interface ErrorResponse {
  error: string;
}

/**
 * Response for GET /contributor.
 */
export interface ContributorResponse {
  active: boolean;
  admin: boolean;
  ban_reason: string | null;
}

/**
 * A single entity-level error from an upload batch.
 * `list` is the entity list key ('t'|'m'|'s'|'i'|'d'), `index` is the row index.
 */
export interface UploadError {
  list: string;
  index: number;
  message: string;
}

/**
 * Response for POST /upload.
 */
export interface UploadResponse {
  processed: number;
  errors: UploadError[];
}

/**
 * Summary of records scrubbed when banning a contributor.
 */
export interface BanScrubSummary {
  sources: number;
  series: number;
  metadata: number;
}

/**
 * Response for POST /admin/ban.
 */
export interface BanResponse {
  banned: boolean;
  contributor_id: string;
  ban_reason: string;
  scrubbed: BanScrubSummary;
}

/**
 * Response for the daily export job.
 */
export interface ExportResponse {
  exported: boolean;
  files: string[];
  version: number;
  compression: number;  // 0 = zstd, 1 = brotli
  sha256: string;       // base64 SHA-256 of the decoded metadata.bin bytes
  skippedNoChanges: boolean; // true when nothing was exported (no pending changes)
  scrubbed: {
    sources: number;
    metadata: number;
    titles: number;
  };
}

/**
 * Response for GET /replication.
 */
export interface ReplicationResponse {
  version: number;
}
