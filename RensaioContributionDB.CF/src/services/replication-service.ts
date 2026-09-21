/**
 * Replication Version Number service.
 *
 * The version lives in the singleton `replication` row (id = 1) and is bumped
 * once per day by the daily cron (the server's export step). Contributors can
 * read the current version via GET /replication.
 */
import { REPLICATION_ROW_ID } from '../types';
import type { Replication } from '../db/schema';

/**
 * Read the current Replication Version Number. Returns 0 when the row is
 * missing (migration not applied yet).
 */
export async function getReplicationVersion(db: D1Database): Promise<number> {
  const row = await db
    .prepare('SELECT id, version, bumped_at FROM replication WHERE id = ?')
    .bind(REPLICATION_ROW_ID)
    .first<Replication>();

  return row?.version ?? 0;
}

/**
 * Ensure the singleton replication row (id = 1) exists.
 */
async function ensureReplicationRow(db: D1Database): Promise<void> {
  const now = new Date().toISOString();
  await db
    .prepare('INSERT OR IGNORE INTO replication (id, version, bumped_at) VALUES (?, 0, ?)')
    .bind(REPLICATION_ROW_ID, now)
    .run();
}

/**
 * Whether any contributor change is waiting to be exported.
 *
 * Two sources of truth, OR-ed:
 *
 *  1. The in-band `pending_changes` flag (fast path) — set by uploads and admin
 *     mutations. If it is 1 we export.
 *
 *  2. A data-derived fallback: any ACTIVE row whose `replication_version`
 *     equals the CURRENT replication version was written after the last export
 *     (uploads stamp rows with the version they observe; each export bumps it).
 *     This survives the failure modes of the flag alone:
 *       - migration 0008 defaulted the existing live row to 0 on a NON-EMPTY
 *         database (stuck skip after deploy);
 *       - the flag is written best-effort AFTER the data batch (a lost write
 *         silently dropped the export);
 *       - the flag column may not exist yet if 0008 was never applied (the
 *         previous code threw → the cron caught it → nothing was exported).
 *
 * mapping_titles intentionally has no replication_version column; its rows are
 * always accompanied by titles/series/metadata/sources rows in the same upload,
 * so the four typed tables give full coverage.
 */
export async function hasPendingChanges(db: D1Database): Promise<boolean> {
  // 1) In-band flag — fast path.
  let flagged = false;
  try {
    const row = await db
      .prepare('SELECT pending_changes FROM replication WHERE id = ?')
      .bind(REPLICATION_ROW_ID)
      .first<{ pending_changes: number }>();
    flagged = (row?.pending_changes ?? 0) === 1;
  } catch {
    // Migration 0008 not applied on this D1 — fall through to the data check.
  }
  if (flagged) return true;

  // 2) Data-derived truth. Rows whose `replication_version` is OLDER than the
  //    current version belong to a generation that was never published — the
  //    version was bumped past them during push failures (or the flag was lost),
  //    so they are pending until a successful export re-stamps them (see
  //    export-service's stampExportedRows). `version <= 0` = no baseline → skip.
  const version = await getReplicationVersion(db);
  if (version <= 0) return false;

  for (const table of ['titles', 'sources', 'series', 'metadata']) {
    const res = await db
      .prepare(
        `SELECT COUNT(*) AS n FROM ${table}
         WHERE archived_at IS NULL
           AND (replication_version IS NULL
                OR (replication_version >= 0 AND replication_version < ?))`
      )
      .bind(version)
      .first<{ n: number }>();
    if ((res?.n ?? 0) > 0) return true;
  }

  return false;
}

/**
 * Set the pending-changes flag (called after an upload applies rows or an admin
 * mutation archives data). No-op when already set.
 */
export async function markPendingChanges(db: D1Database): Promise<void> {
  await ensureReplicationRow(db);
  await db
    .prepare('UPDATE replication SET pending_changes = 1, bumped_at = ? WHERE id = ?')
    .bind(new Date().toISOString(), REPLICATION_ROW_ID)
    .run();
}

/**
 * Clear the pending-changes flag (called after an export publishes a new version).
 */
export async function clearPendingChanges(db: D1Database): Promise<void> {
  await ensureReplicationRow(db);
  await db
    .prepare('UPDATE replication SET pending_changes = 0 WHERE id = ?')
    .bind(REPLICATION_ROW_ID)
    .run();
}

/**
 * Atomically bump the Replication Version Number by 1 and return the new value.
 * Creates the singleton row if missing (id = 1). Only called when there are
 * actual changes to export (pending_changes == 1).
 */
export async function bumpReplicationVersion(db: D1Database): Promise<number> {
  const now = new Date().toISOString();

  // Ensure the singleton exists.
  await ensureReplicationRow(db);

  // Atomic increment.
  await db
    .prepare(
      'UPDATE replication SET version = version + 1, bumped_at = ? WHERE id = ?'
    )
    .bind(now, REPLICATION_ROW_ID)
    .run();

  return getReplicationVersion(db);
}