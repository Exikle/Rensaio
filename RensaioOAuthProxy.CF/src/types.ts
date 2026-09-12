/// <reference types="@cloudflare/workers-types" />

/**
 * Cloudflare Worker environment bindings.
 * Maps 1:1 to appsettings.json + env vars + secrets in the original ASP.NET Core codebase.
 */
export interface Env {
  // D1 database binding
  DB: D1Database;

  // Client IDs (public — set via wrangler.toml [vars])
  PROXY_ANILIST_CLIENT_ID: string;
  PROXY_MYANIMELIST_CLIENT_ID: string;
  PROXY_KITSU_CLIENT_ID: string;
  PROXY_MANGADEX_CLIENT_ID: string;

  // Client Secrets (sensitive — set via wrangler secret put)
  // NOTE: PROXY_ANILIST_CLIENT_SECRET is only needed for the Authorization Code
  // flow. When absent, AniList automatically runs the Implicit flow (no secret).
  PROXY_ANILIST_CLIENT_SECRET: string;
  PROXY_MYANIMELIST_CLIENT_SECRET: string;
  PROXY_KITSU_CLIENT_SECRET: string;
  PROXY_MANGADEX_CLIENT_SECRET: string;

  // Optional per-provider flow override: "code" | "implicit" (AniList only).
  // Omitted → auto-detected: "code" when a client secret is configured, else "implicit".
  PROXY_ANILIST_FLOW?: string;
}

/**
 * Supported OAuth provider identifiers.
 */
export type ProviderName = 'anilist' | 'myanimelist' | 'kitsu' | 'mangadex';

/**
 * OAuth flow variants.
 * - 'code': Authorization Code Grant — uses client_id + client_secret, issues
 *   refresh tokens. Suitable for providers where the client is confidential.
 * - 'implicit': Implicit Grant — uses only client_id, token delivered in the
 *   URL fragment, no code exchange, NO refresh token issued.
 */
export type ProviderFlow = 'code' | 'implicit';

/**
 * Providers that support the Implicit Grant (public clients, no client_secret).
 */
export const IMPLICIT_CAPABLE_PROVIDERS: ReadonlySet<string> = new Set([
  'anilist',
]);

/**
 * Determines the OAuth flow to use for a provider.
 *
 * AniList can run either flow:
 *   - Explicit override via PROXY_ANILIST_FLOW env var ('code' | 'implicit')
 *   - Auto-detect: if a client_secret is configured → code; otherwise → implicit
 *
 * All other providers are always 'code' (they require the client_secret for
 * token exchange / refresh).
 */
export function resolveProviderFlow(provider: string, env: Env): ProviderFlow {
  const lower = provider.toLowerCase();

  if (lower === 'anilist') {
    const override = env.PROXY_ANILIST_FLOW?.toLowerCase();
    if (override === 'code' || override === 'implicit') {
      return override;
    }
    // Auto-detect: secret present → code grant (keeps refresh support),
    // otherwise implicit grant (public client).
    return env.PROXY_ANILIST_CLIENT_SECRET ? 'code' : 'implicit';
  }

  return 'code';
}

/**
 * Result of a token exchange or refresh operation.
 * Maps 1:1 to TokenResult class in ProviderApiService.cs.
 */
export interface TokenResult {
  accessToken: string;
  refreshToken: string | null;
  expiresAt: string; // ISO 8601 datetime
}

/**
 * Normalized provider display names.
 */
export const PROVIDER_DISPLAY_NAMES: Record<string, string> = {
  anilist: 'AniList',
  myanimelist: 'MyAnimeList',
  kitsu: 'Kitsu',
  mangadex: 'MangaDex',
};

/**
 * List of all supported providers for validation.
 */
export const SUPPORTED_PROVIDERS: ReadonlySet<string> = new Set([
  'anilist',
  'myanimelist',
  'kitsu',
  'mangadex',
]);