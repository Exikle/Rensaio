/**
 * Run the daily export job.
 *
 *  1. Scrub archived records older than the retention window (hard-delete).
 *  2. Archive orphaned titles (no active references in series/metadata/mapping).
 *  3. Bump the Replication Version Number.
 *  4. Stream all active entity rows into the streamable export engine
 *     (protobuf → tag+compress → AES-256 → base64) and push the single
 *     `metadata.bin` file to the configured GitHub repository.
 */
import type { Env } from '../types';
import { ARCHIVE_RETENTION_DAYS } from '../types';
import type {
  ContributionMetadataEntityPayload,
  ContributionSeriesEntityPayload,
  ContributionSourceEntityPayload,
  MappingTitleEntityPayload,
  TitleEntityPayload,
} from '../models/requests';
import { buildMetadataBin, type ExportRowSets } from './export-engine';
import { bumpReplicationVersion } from './replication-service';
import { resolveCompression } from '../utils/compression';

export async function runDailyExport(env: Env): Promise<{
  scrubbed: { sources: number; metadata: number; titles: number };
  orphanTitlesArchived: number;
  files: string[];
  version: number;
  compression: number;
  exported: boolean;
}> {
  const scrubbed = await scrubArchived(env.DB);

  // Titles are never archived by removes/scrubs; archive the ones with no
  // active references so the export stays bounded.
  const orphanTitlesArchived = await archiveOrphanTitles(env.DB);

  // 3. Bump the Replication Version Number (server-side, once per day).
  const version = await bumpReplicationVersion(env.DB);

  // 4. Load all active rows.
  const rows = await loadAllRows(env.DB);

  const generatedUtc = new Date().toISOString();

  // Build the encrypted metadata.bin payload (protobuf → tag+compress → AES).
  const payload = await buildMetadataBin(
    rows,
    version,
    generatedUtc,
    env.AESKEY256IV,
    env.EXPORT_COMPRESSION
  );

  // Push the single file.
  await pushFileToGitHub(env, 'metadata.bin', payload.base64);

  const compression = resolveCompression(env.EXPORT_COMPRESSION);

  return {
    scrubbed,
    orphanTitlesArchived,
    files: ['metadata.bin'],
    version,
    compression,
    exported: true,
  };
}

/**
 * Load every active row (archived_at IS NULL) as export payloads.
 * Exported so the verified-contributor snapshot route can reuse the same
 * canonical row → payload mapping as the daily export.
 */
export async function loadAllRows(db: D1Database): Promise<ExportRowSets> {
  const titles = await db
    .prepare('SELECT id, title, replication_version FROM titles WHERE archived_at IS NULL')
    .all<{ id: string; title: string; replication_version: number | null }>();
  const mappingTitles = await db
    .prepare('SELECT mapping_id, title_id FROM mapping_titles WHERE archived_at IS NULL')
    .all<{ mapping_id: string; title_id: string }>();
  const sources = await db
    .prepare(
      `SELECT id, package, source_id, source_name, source_language, last_batch_utc, replication_version
       FROM sources WHERE archived_at IS NULL`
    )
    .all<{
      id: string;
      package: string;
      source_id: number;
      source_name: string;
      source_language: string;
      last_batch_utc: string | null;
      replication_version: number | null;
    }>();
  const series = await db
    .prepare(
      `SELECT id, mapping_id, source_id, title_id, thumbnail_url, status,
              seen_in_popular, seen_in_latest, last_chapter, last_update_utc,
              replication_version
       FROM series WHERE archived_at IS NULL`
    )
    .all<{
      id: string;
      mapping_id: string;
      source_id: string;
      title_id: string;
      thumbnail_url: string | null;
      status: number;
      seen_in_popular: number;
      seen_in_latest: number;
      last_chapter: number | null;
      last_update_utc: string | null;
      replication_version: number | null;
    }>();
  const metadata = await db
    .prepare(
      `SELECT id, mapping_id, provider_id, provider_key, mapping_status, linked_date, replication_version
       FROM metadata WHERE archived_at IS NULL`
    )
    .all<{
      id: string;
      mapping_id: string;
      provider_id: number;
      provider_key: string | null;
      mapping_status: number;
      linked_date: string | null;
      replication_version: number | null;
    }>();

  return {
    titles: (titles.results ?? []).map<TitleEntityPayload>((r) => ({
      i: r.id,
      t: r.title,
      v: r.replication_version ?? 0,
    })),
    mappingTitles: (mappingTitles.results ?? []).map<MappingTitleEntityPayload>((r) => ({
      m: r.mapping_id,
      t: r.title_id,
      v: 0,
    })),
    sources: (sources.results ?? []).map<ContributionSourceEntityPayload>((r) => ({
      i: r.id,
      p: r.package,
      s: r.source_id,
      n: r.source_name,
      l: r.source_language,
      b: r.last_batch_utc,
      v: r.replication_version ?? 0,
    })),
    series: (series.results ?? []).map<ContributionSeriesEntityPayload>((r) => ({
      i: r.id,
      m: r.mapping_id,
      s: r.source_id,
      d: {
        v: 1, // ContributionRecordV1 schema version
        i: r.title_id,
        t: r.thumbnail_url,
        s: r.status,
        p: r.seen_in_popular === 1,
        e: r.seen_in_latest === 1,
        l: r.last_chapter,
        u: r.last_update_utc,
      },
      v: r.replication_version ?? 0,
    })),
    metadata: (metadata.results ?? []).map<ContributionMetadataEntityPayload>((r) => ({
      i: r.id,
      m: r.mapping_id,
      p: r.provider_id,
      k: r.provider_key,
      s: r.mapping_status,
      u: r.linked_date,
      v: r.replication_version ?? 0,
    })),
  };
}

/**
 * Archive titles that no longer have any active references.
 */
export async function archiveOrphanTitles(db: D1Database): Promise<number> {
  const now = new Date().toISOString();
  const result = await db
    .prepare(
      `UPDATE titles
       SET archived_at = ?
       WHERE archived_at IS NULL
         AND NOT EXISTS (
           SELECT 1 FROM series WHERE series.title_id = titles.id AND series.archived_at IS NULL
         )
         AND NOT EXISTS (
           SELECT 1 FROM mapping_titles WHERE mapping_titles.title_id = titles.id AND mapping_titles.archived_at IS NULL
         )`
    )
    .bind(now)
    .run();

  return result.meta.changes;
}

/**
 * Permanently delete records soft-deleted more than ARCHIVE_RETENTION_DAYS ago.
 * Ordering matters: children (series, mapping_titles, metadata) first, then
 * parents (sources, titles, mappings).
 */
export async function scrubArchived(db: D1Database): Promise<{
  sources: number;
  metadata: number;
  titles: number;
}> {
  await db
    .prepare(
      `DELETE FROM series
       WHERE archived_at IS NOT NULL
         AND archived_at < datetime('now', ?)`
    )
    .bind(`-${ARCHIVE_RETENTION_DAYS} days`)
    .run();

  await db
    .prepare(
      `DELETE FROM mapping_titles
       WHERE archived_at IS NOT NULL
         AND archived_at < datetime('now', ?)`
    )
    .bind(`-${ARCHIVE_RETENTION_DAYS} days`)
    .run();

  const metadata = await db
    .prepare(
      `DELETE FROM metadata
       WHERE archived_at IS NOT NULL
         AND archived_at < datetime('now', ?)`
    )
    .bind(`-${ARCHIVE_RETENTION_DAYS} days`)
    .run();

  const sources = await db
    .prepare(
      `DELETE FROM sources
       WHERE archived_at IS NOT NULL
         AND archived_at < datetime('now', ?)`
    )
    .bind(`-${ARCHIVE_RETENTION_DAYS} days`)
    .run();

  const titles = await db
    .prepare(
      `DELETE FROM titles
       WHERE archived_at IS NOT NULL
         AND archived_at < datetime('now', ?)`
    )
    .bind(`-${ARCHIVE_RETENTION_DAYS} days`)
    .run();

  const orphans = await db
    .prepare(
      `DELETE FROM titles
       WHERE archived_at IS NULL
         AND NOT EXISTS (SELECT 1 FROM series WHERE series.title_id = titles.id)
         AND NOT EXISTS (SELECT 1 FROM mapping_titles WHERE mapping_titles.title_id = titles.id)`
    )
    .run();

  return {
    sources: sources.meta.changes,
    metadata: metadata.meta.changes,
    titles: titles.meta.changes + orphans.meta.changes,
  };
}

/**
 * Push a file to the target GitHub repository via the Contents API.
 * `content` is already base64 (the encrypted metadata.bin stream).
 */
async function pushFileToGitHub(env: Env, fileName: string, base64Content: string): Promise<void> {
  const repo = env.EXPORT_GITHUB_REPO;
  const path = buildExportPath(env.EXPORT_PATH, fileName);
  const api = `https://api.github.com/repos/${repo}/contents/${path}`;
  const headers: Record<string, string> = {
    Authorization: `Bearer ${env.GITHUB_TOKEN}`,
    Accept: 'application/vnd.github+json',
    'User-Agent': 'rensaio-contribution-db',
  };

  // Get the current sha if the file exists (handles >1MB via git trees API).
  const sha = await getFileSha(env, path, headers);

  const body = {
    message: `Daily contribution export: ${fileName}`,
    content: base64Content,
    ...(sha ? { sha } : {}),
  };

  const response = await fetch(api, {
    method: 'PUT',
    headers: { ...headers, 'Content-Type': 'application/json' },
    body: JSON.stringify(body),
  });

  if (!response.ok) {
    const detail = await response.text();
    throw new Error(`GitHub push failed for ${fileName}: ${response.status} ${detail}`);
  }
}

/**
 * Resolve the current blob sha of a file in the export repository.
 * (Unchanged from the original — robust to >1MB blobs via the git trees API.)
 */
async function getFileSha(
  env: Env,
  path: string,
  headers: Record<string, string>
): Promise<string | undefined> {
  const repo = env.EXPORT_GITHUB_REPO;
  const api = `https://api.github.com/repos/${repo}/contents/${path}`;

  const existing = await fetch(api, { headers });
  if (existing.ok) {
    const body = (await existing.json()) as { sha?: string };
    return body.sha;
  }

  if (existing.status !== 404) {
    return undefined;
  }

  // The file may be >1MB (or genuinely absent). Resolve its sha via the git
  // trees API. Absent files simply yield no entry.
  const commit = await fetch(`https://api.github.com/repos/${repo}/commits/HEAD`, { headers });
  if (!commit.ok) {
    return undefined;
  }
  const commitBody = (await commit.json()) as { commit?: { tree?: { sha?: string } } };
  const treeSha = commitBody.commit?.tree?.sha;
  if (!treeSha) {
    return undefined;
  }

  const tree = await fetch(
    `https://api.github.com/repos/${repo}/git/trees/${treeSha}?recursive=1`,
    { headers }
  );
  if (!tree.ok) {
    return undefined;
  }
  const treeBody = (await tree.json()) as { tree?: Array<{ path?: string; sha?: string }> };
  return treeBody.tree?.find((entry) => entry.path === path)?.sha;
}

/**
 * Join the export root and a file name into a repo-relative path.
 */
function buildExportPath(exportPath: string, fileName: string): string {
  const normalized = exportPath.replace(/^\/+|\/+$/g, '');
  return normalized ? `${normalized}/${fileName}` : fileName;
}
