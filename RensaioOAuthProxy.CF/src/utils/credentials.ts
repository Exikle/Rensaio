import type { Env } from '../types';
import { IMPLICIT_CAPABLE_PROVIDERS } from '../types';

/**
 * Determines whether a provider runs the Implicit Grant flow (no client_secret).
 */
export function isImplicitProvider(provider: string): boolean {
  return IMPLICIT_CAPABLE_PROVIDERS.has(provider.toLowerCase());
}

/**
 * Maps 1:1 to GetCredentials() in ProviderApiService.cs.
 *
 * Reads the client_id and client_secret for a given provider from:
 *   - Environment variables / Secrets (Workers Secrets for client_secret)
 *
 * Priority order (matching original):
 *   1. Environment variables (PROXY_{PROVIDER}_CLIENT_ID / _CLIENT_SECRET)
 *   (No appsettings.json fallback — wrangler.toml vars serve that role)
 *
 * Original:
 *   private (string ClientId, string ClientSecret) GetCredentials(string provider)
 */
export function getCredentials(provider: string, env: Env): { clientId: string; clientSecret: string } {
  const normalized = normalizeProvider(provider);
  const prefix = `PROXY_${normalized.toUpperCase()}`;

  const clientIdKey = `${prefix}_CLIENT_ID` as keyof Env;
  const clientSecretKey = `${prefix}_CLIENT_SECRET` as keyof Env;

  const clientId = (env[clientIdKey] as string | undefined) ?? '';
  const clientSecret = (env[clientSecretKey] as string | undefined) ?? '';

  // Implicit-flow providers (e.g. AniList) are public clients — only client_id
  // is required. The client_secret is never used for the implicit grant.
  if (isImplicitProvider(provider)) {
    if (!clientId) {
      throw new Error(
        `No client_id configured for provider: ${normalized}. ` +
        `Set ${clientIdKey} environment variable / var.`
      );
    }
    return { clientId, clientSecret: '' };
  }

  if (!clientId || !clientSecret) {
    throw new Error(
      `No credentials configured for provider: ${normalized}. ` +
      `Set ${clientIdKey} and ${clientSecretKey} environment variables / secrets.`
    );
  }

  return { clientId, clientSecret };
}

/**
 * Normalizes provider name to the canonical format used in env var names.
 *
 * Original logic (ProviderApiService.cs lines 30-37):
 *   "anilist" => "AniList"
 *   "myanimelist" or "mal" => "MyAnimeList"
 *   "kitsu" => "Kitsu"
 *   "mangadex" => "MangaDex"
 */
export function normalizeProvider(provider: string): string {
  const lower = provider.toLowerCase();
  switch (lower) {
    case 'anilist':
      return 'AniList';
    case 'myanimelist':
    case 'mal':
      return 'MyAnimeList';
    case 'kitsu':
      return 'Kitsu';
    case 'mangadex':
      return 'MangaDex';
    default:
      throw new Error(`Unknown provider: ${provider}`);
  }
}