import { Hono } from 'hono';
import type { Env } from './types';
import contributorRoutes from './routes/contributor';
import uploadRoutes from './routes/upload';
import snapshotRoutes from './routes/snapshot';
import adminRoutes from './routes/admin';
import keyRoutes from './routes/key';
import replicationRoutes from './routes/replication';
import { runDailyExport } from './services/export-service';

/**
 * Cloudflare Worker entry point.
 *
 * HTTP endpoints:
 *   GET  /contributor?contributor={UUID}  → validate a contributor
 *   POST /upload?contributor={UUID}       → submit a ContributionSnapshotV1 batch
 *   POST /admin/ban?admin={adminUUID}     → ban a contributor (admin only)
 *   POST /admin/clean?admin={adminUUID}   → wipe data tables (admin only)
 *   POST /admin/export?admin={adminUUID}[&force=1] → run the GitHub export now
 *                                        (admin only; force=1 bypasses the
 *                                        pending-changes gate to always bump the
 *                                        version + re-upload metadata.bin)
 *   GET  /key                            → return the AES key+IV (encryption secret)
 *   GET  /replication                    → return the current Replication Version Number
 *
 * Scheduled (cron 06:00 UTC daily — handled via the `scheduled` event):
 *   1. Scrub archived records older than the retention window (hard-delete)
 *   2. Bump the Replication Version Number
 *   3. Export the snapshot as protobuf → compress → AES → metadata.bin to GitHub
 *
 * NOTE: contributor auto-creation is no longer supported — contributors are
 * seeded out-of-band (D1).
 */

const app = new Hono<{ Bindings: Env }>();

// ── Health check ──
app.get('/health', (c) => {
  return c.json({ status: 'ok', service: 'rensaio-contribution-db' });
});

// ── Routes ──
app.route('/contributor', contributorRoutes);
app.route('/upload', uploadRoutes);
app.route('/snapshot', snapshotRoutes);
app.route('/admin', adminRoutes);
app.route('/key', keyRoutes);
app.route('/replication', replicationRoutes);

// ── Catch-all 404 ──
app.notFound((c) => {
  return c.json({ error: 'Not found' }, 404);
});

// ── Global error handler ──
app.onError((err, c) => {
  console.error('Unhandled error:', err);
  return c.json({ error: 'Internal server error' }, 500);
});

/**
 * Cron handler — runs the daily export when Cloudflare fires the
 * `scheduled` event (per the [triggers] crons in wrangler.toml).
 * This is not reachable over HTTP.
 */
async function scheduled(_controller: ScheduledController, env: Env, ctx: ExecutionContext): Promise<void> {
  ctx.waitUntil(
    (async () => {
      try {
        const result = await runDailyExport(env);
        if (!result.exported) {
          console.log(
            `Cron export: no pending changes — version ${result.version} unchanged, nothing pushed (scrubbed ` +
              `${JSON.stringify(result.scrubbed)}, orphan titles archived ${result.orphanTitlesArchived}).`
          );
          return;
        }
        console.log(
          `Cron export: scrubbed ${JSON.stringify(result.scrubbed)}, ` +
            `orphan titles archived ${result.orphanTitlesArchived}, ` +
            `version ${result.version}, ` +
            `compression ${result.compression}, ` +
            `pushed ${result.files.join(', ')}`
        );
      } catch (err) {
        console.error('Cron export failed:', err);
      }
    })()
  );
}

export default {
  fetch: app.fetch,
  scheduled,
};
