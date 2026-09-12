import { Hono } from 'hono';
import type { Env } from '../types';
import { validateActiveContributor } from '../services/contributor-service';
import { loadAllRows } from '../services/export-service';
import { getReplicationVersion } from '../services/replication-service';
import { SNAPSHOT_SCHEMA_VERSION } from '../types';
import type { ErrorResponse } from '../models/responses';

/**
 * Snapshot routes.
 * Base path: /snapshot (set in index.ts)
 *
 * GET /snapshot?contributor={UUID} → full active ContributionSnapshotV1 (JSON).
 * Restricted to ACTIVE contributors (same gate as /upload) so the encrypted
 * GitHub export is not the only way to fetch the cloud dataset.
 *
 * The response uses the exact /upload wire shape (e/u/v/t/m/s/i/d), so the
 * backend downloader can apply it with the same "resolve mapping by title
 * overlap, dedup metadata by (mapping, provider, key)" semantics. Cloud
 * mapping/metadata ids are present but treated as transient references by the
 * downloader — it re-keys them to local ids.
 */
const snapshotRoutes = new Hono<{ Bindings: Env }>();

snapshotRoutes.get('/', async (c) => {
  const contributorId = c.req.query('contributor');

  if (!contributorId) {
    return c.json<ErrorResponse>({ error: 'Missing "contributor" query parameter' }, 400);
  }

  const validation = await validateActiveContributor(c.env.DB, contributorId);
  if (!validation.ok) {
    if (validation.reason === 'not_found') {
      return c.json<ErrorResponse>({ error: 'Contributor not found' }, 404);
    }
    return c.json<ErrorResponse>(
      { error: `Contributor is banned: ${validation.ban_reason ?? 'no reason provided'}` },
      403
    );
  }

  try {
    const rows = await loadAllRows(c.env.DB);
    const version = await getReplicationVersion(c.env.DB);

    return c.json({
      e: SNAPSHOT_SCHEMA_VERSION,
      u: new Date().toISOString(),
      v: version,
      t: rows.titles,
      m: rows.mappingTitles,
      s: rows.sources,
      i: rows.series,
      d: rows.metadata,
    });
  } catch (err) {
    console.error('Snapshot load failed:', err);
    return c.json<ErrorResponse>(
      { error: `Snapshot failed: ${err instanceof Error ? err.message : 'Unknown error'}` },
      500
    );
  }
});

export default snapshotRoutes;