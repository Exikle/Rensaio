import { Hono } from 'hono';
import type { Env } from '../types';
import { getContributor } from '../services/contributor-service';
import type { ContributorResponse, ErrorResponse } from '../models/responses';

/**
 * Contributor routes — read-only validation.
 * Base path: /contributor (set in index.ts)
 *
 * NOTE: Contributor auto-creation is no longer supported. Contributors are
 * provisioned out-of-band (D1 seed). POST /contributor therefore returns 410.
 */
const contributorRoutes = new Hono<{ Bindings: Env }>();

// GET /contributor?contributor={UUID}
contributorRoutes.get('/', async (c) => {
  const contributorId = c.req.query('contributor');

  if (!contributorId) {
    return c.json<ErrorResponse>({ error: 'Missing "contributor" query parameter' }, 400);
  }

  const contributor = await getContributor(c.env.DB, contributorId);

  if (!contributor) {
    return c.json<ErrorResponse>({ error: 'Contributor not found' }, 404);
  }

  const response: ContributorResponse = {
    active: contributor.active === 1,
    admin: contributor.admin === 1,
    ban_reason: contributor.ban_reason,
  };

  return c.json(response);
});

// POST /contributor — removed (auto-create no longer supported)
contributorRoutes.post('/', (_c) => {
  return _c.json<ErrorResponse>(
    { error: 'Contributor auto-creation is no longer supported' },
    410
  );
});

export default contributorRoutes;
