import { Hono } from 'hono';
import type { Env } from '../types';
import { SUPPORTED_PROVIDERS, PROVIDER_DISPLAY_NAMES, resolveProviderFlow } from '../types';
import { generateAuthUrl, exchangeCode, refreshToken } from '../services/provider-api';
import { store, retrieve, setTokens, remove } from '../services/token-store';
import { renderCallbackHtml, renderErrorHtml, renderImplicitCaptureHtml } from '../utils/callback-html';
import { generateCodeVerifier } from '../utils/pkce';
import type { ErrorResponse, OAuthUrlResponse, TokenRetrieveResponse, TokenRefreshResponse } from '../models/responses';
import type { TokenRetrieveRequest, TokenRefreshRequest, ImplicitTokenCallbackRequest } from '../models/requests';

/**
 * OAuth routes.
 *
 * Maps 1:1 to OAuthController.cs.
 * Base path: /api/oauth (set in index.ts)
 *
 * Endpoints:
 *   POST /:provider/url       → GetAuthUrl()        (lines 25-47)
 *   GET  /:provider/callback  → Callback()           (lines 49-113, implicit flow)
 *   POST /:provider/callback  → ImplicitCallback()   (NEW: persists implicit token)
 *   POST /:provider/token     → GetToken()           (lines 115-133)
 *   POST /:provider/refresh   → RefreshToken()       (lines 135-159)
 *
 * Dual-flow support (AniList):
 *   - code flow:       GET callback exchanges the code server-side (unchanged)
 *   - implicit flow:   GET callback serves a JS capture page that reads the
 *     access_token from the URL fragment and POSTs it to this server
 *     (POST /:provider/callback) for persistence. No client_secret involved.
 */
const oauthRoutes = new Hono<{ Bindings: Env }>();

// ──────────────────────────────────────────────
// POST /:provider/url
// Generates the authorization URL for a provider.
// Maps 1:1 to OAuthController.GetAuthUrl() lines 25-47.
// ──────────────────────────────────────────────
oauthRoutes.post('/:provider/url', async (c) => {
  const provider = c.req.param('provider').toLowerCase();
  const instanceKey = c.req.header('X-Instance-Key');

  // Validate instance key (matching original: Unauthorized on missing)
  if (!instanceKey) {
    return c.json<ErrorResponse>({ error: 'X-Instance-Key header required' }, 401);
  }

  // Validate provider
  if (!SUPPORTED_PROVIDERS.has(provider)) {
    return c.json<ErrorResponse>({ error: `Unsupported provider: ${provider}` }, 400);
  }

  try {
    // Generate state (matching original: Guid.NewGuid().ToString("N"))
    const state = crypto.randomUUID().replace(/-/g, '');

    // Build redirect URI (matching original: $"{Request.Scheme}://{Request.Host}/api/oauth/{provider}/callback")
    const url = new URL(c.req.url);
    const redirectUri = `${url.protocol}//${url.host}/api/oauth/${provider}/callback`;

    // PKCE: Generate code_verifier for MyAnimeList.
    // MAL requires plain method (challenge = verifier) — S256 fails server-side despite being accepted.
    let codeChallenge: string | undefined;
    let codeVerifier: string | undefined;
    if (provider === 'myanimelist') {
      codeVerifier = generateCodeVerifier();
      codeChallenge = codeVerifier; // plain: challenge === verifier
      console.log(`[PKCE] /url - Generated verifier len=${codeVerifier.length} (plain mode)`);
    }

    // Generate auth URL (pass codeChallenge for MyAnimeList PKCE)
    const authUrl = generateAuthUrl(provider, redirectUri, state, c.env, codeChallenge);

    // Store session in D1 (matching original: _tokenStore.Store(state, instanceKey, provider))
    await store(c.env.DB, state, instanceKey, provider, codeVerifier);

    return c.json<OAuthUrlResponse>({ authUrl, state });
  } catch (err) {
    const message = err instanceof Error ? err.message : 'Failed to generate auth URL';
    return c.json<ErrorResponse>({ error: message }, 400);
  }
});

// ──────────────────────────────────────────────
// GET /:provider/callback
// Handles the OAuth provider redirect after user authorization.
// Maps 1:1 to OAuthController.Callback() lines 49-113.
// ──────────────────────────────────────────────
oauthRoutes.get('/:provider/callback', async (c) => {
  const provider = c.req.param('provider').toLowerCase();
  const code = c.req.query('code');
  const state = c.req.query('state');
  const redirectUriQuery = c.req.query('redirectUri');

  if (!SUPPORTED_PROVIDERS.has(provider)) {
    return c.json<ErrorResponse>({ error: `Unsupported provider: ${provider}` }, 400);
  }

  const displayName = PROVIDER_DISPLAY_NAMES[provider] ?? provider;
  const flow = resolveProviderFlow(provider, c.env);

  // ── Implicit flow: serve the capture page ──
  // AniList redirects here with the access token in the URL FRAGMENT
  // (#access_token=...&expires_in=...). The fragment never reaches the server,
  // so no code/state query params are expected. We serve an HTML page whose JS
  // parses the fragment, resolves the state, and POSTs the token back to
  // POST /:provider/callback for persistence.
  if (flow === 'implicit') {
    const html = renderImplicitCaptureHtml(displayName, provider, state ?? '');
    return c.html(html);
  }

  // ── Code flow (unchanged) ──

  // Validate required params (matching original: BadRequest on missing)
  if (!code || !state) {
    return c.json<ErrorResponse>({ error: 'Missing code or state parameter' }, 400);
  }

  // Retrieve session (matching original: _tokenStore.Retrieve(state))
  const session = await retrieve(c.env.DB, state);
  if (!session) {
    return c.json<ErrorResponse>({ error: 'Invalid state — authorization session not found' }, 400);
  }

  try {
    // Build callback URI (matching original: redirectUri ?? $"{Request.Scheme}://{Request.Host}/...")
    const url = new URL(c.req.url);
    const callbackUri = redirectUriQuery ?? `${url.protocol}//${url.host}/api/oauth/${provider}/callback`;

    // PKCE: Retrieve stored code_verifier from session (MyAnimeList)
    const codeVerifier = session.code_verifier ?? undefined;
    // Exchange code for tokens (matching original: _providerApi.ExchangeCodeAsync)
    // Pass code_verifier for MyAnimeList PKCE flow
    const tokenResult = await exchangeCode(provider, code, callbackUri, c.env, codeVerifier);

    // Store tokens in session (matching original: _tokenStore.SetTokens(state, accessToken, refreshToken, expiresAt))
    await setTokens(c.env.DB, state, tokenResult.accessToken, tokenResult.refreshToken, tokenResult.expiresAt);

    // Get display name (matching original: provider.ToLowerInvariant() switch { ... })
    const successDisplayName = PROVIDER_DISPLAY_NAMES[provider] ?? provider;

    // Render success HTML page with postMessage (matching original lines 80-106)
    const html = renderCallbackHtml(successDisplayName, provider, state);
    return c.html(html);
  } catch (err) {
    console.error(`OAuth callback failed for provider ${provider}:`, err);
    const errorMessage = err instanceof Error ? err.message : 'Token exchange failed';
    const errorDisplayName = PROVIDER_DISPLAY_NAMES[provider] ?? provider;
    const errorHtml = renderErrorHtml(errorDisplayName, errorMessage);
    return c.html(errorHtml, 500);
  }
});

// ──────────────────────────────────────────────
// POST /:provider/callback
// Persists an access token captured from the implicit-flow redirect fragment.
// Called by the capture page (renderImplicitCaptureHtml) served at the GET
// callback. AniList implicit grant: client_id only, no exchange, NO refresh token.
// ──────────────────────────────────────────────
oauthRoutes.post('/:provider/callback', async (c) => {
  const provider = c.req.param('provider').toLowerCase();

  if (!SUPPORTED_PROVIDERS.has(provider)) {
    return c.json<ErrorResponse>({ error: `Unsupported provider: ${provider}` }, 400);
  }

  // Only implicit-flow providers use this endpoint. Code-flow providers must
  // go through the token exchange (GET callback / POST token), never this.
  if (resolveProviderFlow(provider, c.env) !== 'implicit') {
    return c.json<ErrorResponse>({ error: 'Implicit callback is only supported for implicit-flow providers' }, 400);
  }

  const body = await c.req.json<ImplicitTokenCallbackRequest>();

  if (!body.state || !body.accessToken) {
    return c.json<ErrorResponse>({ error: 'State and accessToken are required' }, 400);
  }

  // Validate the session exists for this state (prevents arbitrary token stuffing).
  const session = await retrieve(c.env.DB, body.state);
  if (!session) {
    return c.json<ErrorResponse>({ error: 'Invalid state — authorization session not found' }, 400);
  }

  if (session.provider !== provider) {
    return c.json<ErrorResponse>({ error: 'State does not match provider' }, 400);
  }

  // Store access token; NO refresh token exists in the implicit flow.
  // expiresAt comes from the capture page (computed from expires_in).
  await setTokens(c.env.DB, body.state, body.accessToken, null, body.expiresAt ?? null);

  return c.json({ connected: true });
});

// ──────────────────────────────────────────────
// POST /:provider/token
// Retrieves tokens by state (one-time, then deletes).
// Maps 1:1 to OAuthController.GetToken() lines 115-133.
// ──────────────────────────────────────────────
oauthRoutes.post('/:provider/token', async (c) => {
  const provider = c.req.param('provider').toLowerCase();

  if (!SUPPORTED_PROVIDERS.has(provider)) {
    return c.json<ErrorResponse>({ error: `Unsupported provider: ${provider}` }, 400);
  }

  const body = await c.req.json<TokenRetrieveRequest>();

  // Validate state (matching original: BadRequest on missing)
  if (!body.state) {
    return c.json<ErrorResponse>({ error: 'State is required' }, 400);
  }

  // Remove session (matching original: _tokenStore.Remove(request.State))
  const entry = await remove(c.env.DB, body.state);
  if (!entry) {
    return c.json<ErrorResponse>({ error: 'No tokens found for this state' }, 404);
  }

  return c.json<TokenRetrieveResponse>({
    accessToken: entry.access_token ?? '',
    refreshToken: entry.refresh_token,
    expiresAt: entry.expires_at,
  });
});

// ──────────────────────────────────────────────
// POST /:provider/refresh
// Refreshes an expired access token.
// Maps 1:1 to OAuthController.RefreshToken() lines 135-159.
// ──────────────────────────────────────────────
oauthRoutes.post('/:provider/refresh', async (c) => {
  const provider = c.req.param('provider').toLowerCase();
  const instanceKey = c.req.header('X-Instance-Key');

  // Validate instance key (matching original: Unauthorized on missing)
  if (!instanceKey) {
    return c.json<ErrorResponse>({ error: 'X-Instance-Key header required' }, 401);
  }

  if (!SUPPORTED_PROVIDERS.has(provider)) {
    return c.json<ErrorResponse>({ error: `Unsupported provider: ${provider}` }, 400);
  }

  const body = await c.req.json<TokenRefreshRequest>();

  try {
    // Refresh the token (matching original: _providerApi.RefreshTokenAsync(provider, request.RefreshToken))
    const tokenResult = await refreshToken(provider, body.refreshToken, c.env);

    return c.json<TokenRefreshResponse>({
      accessToken: tokenResult.accessToken,
      refreshToken: tokenResult.refreshToken,
      expiresAt: tokenResult.expiresAt,
    });
  } catch (err) {
    console.error(`Token refresh failed for provider ${provider}:`, err);
    return c.json<ErrorResponse>({ error: 'Token refresh failed' }, 500);
  }
});

export default oauthRoutes;