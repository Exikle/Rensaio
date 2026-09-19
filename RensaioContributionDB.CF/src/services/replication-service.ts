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
 * Returns false when the row is missing (never set).
 */
export async function hasPendingChanges(db: D1Database): Promise<boolean> {
  const row = await db
    .prepare('SELECT pending_changes FROM replication WHERE id = ?')
    .bind(REPLICATION_ROW_ID)
    .first<{ pending_changes: number }>();

  return (row?.pending_changes ?? 0) === 1;
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