import { Hono } from 'hono';
import type { Env } from '../types';
import { banContributor, cleanTables, getActiveAdmin } from '../services/admin-service';
import { runDailyExport } from '../services/export-service';
import type { BanRequest } from '../models/requests';
import type { ErrorResponse } from '../models/responses';

/**
 * Admin routes.
 * Base path: /admin (set in index.ts)
 */
const adminRoutes = new Hono<{ Bindings: Env }>();

// POST /admin/ban?admin={adminUUID}
adminRoutes.post('/ban', async (c) => {
  const adminId = c.req.query('admin');

  if (!adminId) {
    return c.json<ErrorResponse>({ error: 'Missing "admin" query parameter' }, 400);
  }

  let body: BanRequest;
  try {
    body = await c.req.json<BanRequest>();
  } catch {
    return c.json<ErrorResponse>({ error: 'Invalid JSON body' }, 400);
  }

  if (!body || typeof body.contributor_id !== 'string' || body.contributor_id.length === 0) {
    return c.json<ErrorResponse>({ error: 'Request body must contain a "contributor_id"' }, 400);
  }
  if (typeof body.ban_reason !== 'string' || body.ban_reason.length === 0) {
    return c.json<ErrorResponse>({ error: 'Request body must contain a non-empty "ban_reason"' }, 400);
  }

  const result = await banContributor(c.env.DB, adminId, body.contributor_id, body.ban_reason);

  if (!result.ok) {
    return c.json<ErrorResponse>({ error: result.error }, result.status);
  }

  return c.json(result.response);
});

// POST /admin/clean?admin={adminUUID}
adminRoutes.post('/clean', async (c) => {
  const validation = await getActiveAdmin(c.env.DB, c.req.query('admin') ?? '');
  if (!validation.ok) {
    return c.json<ErrorResponse>({ error: validation.error }, validation.status);
  }

  const deleted = await cleanTables(c.env.DB);
  return c.json({ cleaned: true, deleted });
});

// POST /admin/export?admin={adminUUID}
adminRoutes.post('/export', async (c) => {
  const validation = await getActiveAdmin(c.env.DB, c.req.query('admin') ?? '');
  if (!validation.ok) {
    return c.json<ErrorResponse>({ error: validation.error }, validation.status);
  }

  // The export pipeline (D1 row load → protobuf → compress → AES → base64 →
  // GitHub push) is CPU-heavy and can exceed the synchronous fetch handler's
  // CPU budget on large datasets (this is exactly the "Worker exceeded CPU time
  // limit" failure). Run it in the background via ctx.waitUntil — the same
  // mechanism the daily cron uses — and return 202 immediately. The outcome of
  // the waitUntil'd task is only observable in worker logs.
  c.executionCtx.waitUntil(
    runDailyExport(c.env)
      .then((result) => {
        console.log(
          `Manual export ${result.exported ? 'completed' : 'skipped (no pending changes)'}: ` +
            `version=${result.version}, files=${result.files.join(',') || '(none)'}, ` +
            `compression=${result.compression}, sha256=${result.sha256 || '(none)'}`
        );
      })
      .catch((err) => {
        console.error('Manual export failed:', err);
      })
  );

  return c.json(
    {
      triggered: true,
      message:
        'Export scheduled in the background. Check worker logs for outcome (metadata.bin + metadata.bin.sha256 will appear in the GitHub repo when it succeeds).',
    },
    202
  );
});

export default adminRoutes;
