import { Hono } from 'hono';
import type { Env } from '../types';
import { getReplicationVersion } from '../services/replication-service';
import type { ReplicationResponse } from '../models/responses';

/**
 * Replication routes.
 * Base path: /replication (set in index.ts)
 *
 * GET /replication → current Replication Version Number.
 * Public read — contributors diff against this.
 */
const replicationRoutes = new Hono<{ Bindings: Env }>();

replicationRoutes.get('/', async (c) => {
  const version = await getReplicationVersion(c.env.DB);
  const response: ReplicationResponse = { version };
  return c.json(response);
});

export default replicationRoutes;