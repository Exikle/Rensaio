import type { Env } from '../types';
import type { Contributor } from '../db/schema';

/**
 * Look up a contributor by UUID.
 * Returns the row, or null if it does not exist.
 */
export async function getContributor(db: D1Database, id: string): Promise<Contributor | null> {
  const result = await db
    .prepare('SELECT id, admin, active, ban_reason, last_change FROM contributors WHERE id = ?')
    .bind(id)
    .first<Contributor>();

  return result ?? null;
}

/**
 * Validate that a contributor exists and is active.
 *
 * Returns:
 *  - { ok: true } when the contributor is active
 *  - { ok: false, reason: 'not_found' } when the UUID does not exist
 *  - { ok: false, reason: 'banned', ban_reason } when the contributor is inactive
 */
export async function validateActiveContributor(
  db: D1Database,
  id: string
): Promise<{ ok: true; contributor: Contributor } | { ok: false; reason: 'not_found' | 'banned'; ban_reason?: string | null }> {
  const contributor = await getContributor(db, id);

  if (!contributor) {
    return { ok: false, reason: 'not_found' };
  }

  if (contributor.active !== 1) {
    return { ok: false, reason: 'banned', ban_reason: contributor.ban_reason };
  }

  return { ok: true, contributor };
}

/**
 * Validate that a contributor is an active admin.
 */
export async function validateActiveAdmin(
  db: D1Database,
  id: string
): Promise<{ ok: true; contributor: Contributor } | { ok: false; reason: 'not_found' | 'not_admin' | 'banned' }> {
  const contributor = await getContributor(db, id);

  if (!contributor) {
    return { ok: false, reason: 'not_found' };
  }

  if (contributor.active !== 1) {
    return { ok: false, reason: 'banned' };
  }

  if (contributor.admin !== 1) {
    return { ok: false, reason: 'not_admin' };
  }

  return { ok: true, contributor };
}

/**
 * Convenience wrapper for callers holding the full Env binding.
 */
export async function getContributorEnv(env: Env, id: string): Promise<Contributor | null> {
  return getContributor(env.DB, id);
}

/**
 * Convenience wrapper for callers holding the full Env binding.
 */
export async function validateActiveContributorEnv(
  env: Env,
  id: string
): Promise<{ ok: true; contributor: Contributor } | { ok: false; reason: 'not_found' | 'banned'; ban_reason?: string | null }> {
  return validateActiveContributor(env.DB, id);
}

/**
 * Convenience wrapper for callers holding the full Env binding.
 */
export async function validateActiveAdminEnv(
  env: Env,
  id: string
): Promise<{ ok: true; contributor: Contributor } | { ok: false; reason: 'not_found' | 'not_admin' | 'banned' }> {
  return validateActiveAdmin(env.DB, id);
}
