import type { BanResponse, BanScrubSummary } from '../models/responses';
import type { Contributor } from '../db/schema';

/**
 * Ban a contributor and archive all their non-archived data.
 *
 * All operations run in a single D1 batch (atomic):
 *  1. Deactivate the target contributor (active = 0) and set ban_reason.
 *  2. Archive all non-archived series owned by the contributor.
 *  3. Archive all non-archived metadata owned by the contributor.
 *
 * Titles / mappings / sources are a shared registry — never auto-archived.
 *
 * @returns the ban response with a scrub summary, or an error result.
 */
export async function banContributor(
  db: D1Database,
  adminId: string,
  targetId: string,
  banReason: string
): Promise<
  | { ok: true; response: BanResponse }
  | { ok: false; status: 400 | 403 | 404 | 409; error: string }
> {
  // 1. Validate the admin
  const admin = await db
    .prepare('SELECT id, admin, active, ban_reason, last_change FROM contributors WHERE id = ?')
    .bind(adminId)
    .first<Contributor>();

  if (!admin) {
    return { ok: false, status: 404, error: 'Admin contributor not found' };
  }
  if (admin.active !== 1) {
    return { ok: false, status: 403, error: 'Forbidden: admin contributor is inactive' };
  }
  if (admin.admin !== 1) {
    return { ok: false, status: 403, error: 'Forbidden: admin privileges required' };
  }

  // 2. Validate the target
  const target = await db
    .prepare('SELECT id, admin, active, ban_reason, last_change FROM contributors WHERE id = ?')
    .bind(targetId)
    .first<Contributor>();

  if (!target) {
    return { ok: false, status: 404, error: 'Target contributor not found' };
  }
  if (target.active === 0) {
    return { ok: false, status: 409, error: 'Contributor already banned' };
  }
  if (target.admin === 1) {
    return { ok: false, status: 400, error: 'Cannot ban an admin contributor' };
  }

  // 3. Count rows that will be archived (before mutation)
  const seriesCount = await db
    .prepare(
      'SELECT COUNT(*) AS count FROM series WHERE contributor_id = ? AND archived_at IS NULL'
    )
    .bind(targetId)
    .first<{ count: number }>();
  const metadataCount = await db
    .prepare(
      'SELECT COUNT(*) AS count FROM metadata WHERE contributor_id = ? AND archived_at IS NULL'
    )
    .bind(targetId)
    .first<{ count: number }>();

  const now = new Date().toISOString();

  // 4. Execute the ban atomically.
  // Ordering matters: archive contributor-owned series + metadata FIRST, then
  // archive mapping_titles rows whose mapping no longer has any active
  // contributor-owned data, then finally archive those orphaned mappings.
  await db.batch([
    // 4a. Deactivate the contributor.
    db
      .prepare('UPDATE contributors SET active = 0, ban_reason = ?, last_change = ? WHERE id = ?')
      .bind(banReason, now, targetId),
    // 4b. Archive all non-archived series owned by the contributor.
    db
      .prepare(
        'UPDATE series SET archived_at = ? WHERE contributor_id = ? AND archived_at IS NULL'
      )
      .bind(now, targetId),
    // 4c. Archive all non-archived metadata owned by the contributor.
    db
      .prepare(
        'UPDATE metadata SET archived_at = ? WHERE contributor_id = ? AND archived_at IS NULL'
      )
      .bind(now, targetId),
    // 4d. Archive mapping_titles rows whose mapping has no remaining ACTIVE
    //     series or metadata (i.e. it only described the banned contributor's
    //     data and is now orphaned).
    db
      .prepare(
        `UPDATE mapping_titles SET archived_at = ?
         WHERE archived_at IS NULL
           AND mapping_id IN (
             SELECT mt.mapping_id FROM mapping_titles mt
             WHERE mt.mapping_id NOT IN (
               SELECT mapping_id FROM series WHERE archived_at IS NULL
             )
             AND mt.mapping_id NOT IN (
               SELECT mapping_id FROM metadata WHERE archived_at IS NULL
             )
           )`
      )
      .bind(now),
    // 4e. Archive the now-orphaned mappings themselves.
    db
      .prepare(
        `UPDATE mappings SET archived_at = ?
         WHERE archived_at IS NULL
           AND id NOT IN (
             SELECT mapping_id FROM series WHERE archived_at IS NULL
           )
           AND id NOT IN (
             SELECT mapping_id FROM metadata WHERE archived_at IS NULL
           )
           AND id NOT IN (
             SELECT mapping_id FROM mapping_titles WHERE archived_at IS NULL
           )`
      )
      .bind(now),
  ]);

  const scrubbed: BanScrubSummary = {
    sources: seriesCount?.count ?? 0,
    series: seriesCount?.count ?? 0,
    metadata: metadataCount?.count ?? 0,
  };

  return {
    ok: true,
    response: {
      banned: true,
      contributor_id: targetId,
      ban_reason: banReason,
      scrubbed,
    },
  };
}

/**
 * Validate that a UUID is an active admin contributor.
 * Shared by the admin-only maintenance endpoints (/clean, /export).
 */
export async function getActiveAdmin(
  db: D1Database,
  adminId: string
): Promise<
  | { ok: true; admin: Contributor }
  | { ok: false; status: 400 | 403 | 404; error: string }
> {
  if (!adminId) {
    return { ok: false, status: 400, error: 'Missing "admin" query parameter' };
  }

  const admin = await db
    .prepare('SELECT id, admin, active, ban_reason, last_change FROM contributors WHERE id = ?')
    .bind(adminId)
    .first<Contributor>();

  if (!admin) {
    return { ok: false, status: 404, error: 'Admin contributor not found' };
  }
  if (admin.active !== 1) {
    return { ok: false, status: 403, error: 'Forbidden: admin contributor is inactive' };
  }
  if (admin.admin !== 1) {
    return { ok: false, status: 403, error: 'Forbidden: admin privileges required' };
  }

  return { ok: true, admin };
}

/**
 * Hard-delete ALL rows from the data tables (sources, series, mapping_titles,
 * metadata, titles, mappings) in foreign-key order. The `contributors` table
 * is intentionally left intact — it holds the admin auth used by this endpoint.
 *
 * @returns how many rows were deleted per table.
 */
export async function cleanTables(db: D1Database): Promise<{
  sources: number;
  series: number;
  mappingTitles: number;
  metadata: number;
  titles: number;
  mappings: number;
}> {
  const results = await db.batch([
    db.prepare('DELETE FROM series'),
    db.prepare('DELETE FROM mapping_titles'),
    db.prepare('DELETE FROM metadata'),
    db.prepare('DELETE FROM sources'),
    db.prepare('DELETE FROM titles'),
    db.prepare('DELETE FROM mappings'),
  ]);

  return {
    sources: results[3].meta.changes,
    series: results[0].meta.changes,
    mappingTitles: results[1].meta.changes,
    metadata: results[2].meta.changes,
    titles: results[4].meta.changes,
    mappings: results[5].meta.changes,
  };
}
