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
 * Atomically bump the Replication Version Number by 1 and return the new value.
 * Creates the singleton row if missing (id = 1).
 */
export async function bumpReplicationVersion(db: D1Database): Promise<number> {
  const now = new Date().toISOString();

  // Ensure the singleton exists.
  await db
    .prepare('INSERT OR IGNORE INTO replication (id, version, bumped_at) VALUES (?, 0, ?)')
    .bind(REPLICATION_ROW_ID, now)
    .run();

  // Atomic increment.
  await db
    .prepare(
      'UPDATE replication SET version = version + 1, bumped_at = ? WHERE id = ?'
    )
    .bind(now, REPLICATION_ROW_ID)
    .run();

  return getReplicationVersion(db);
}