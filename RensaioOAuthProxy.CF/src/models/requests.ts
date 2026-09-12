/**
 * Request DTOs — maps 1:1 to the original ASP.NET Core DTOs.
 *
 * Original files:
 *   - TokenRetrieveRequestDto.cs
 *   - TokenRefreshRequestDto.cs
 */

/**
 * Maps 1:1 to TokenRetrieveRequestDto.cs
 */
export interface TokenRetrieveRequest {
  state: string;
}

/**
 * Maps 1:1 to TokenRefreshRequestDto.cs
 */
export interface TokenRefreshRequest {
  refreshToken: string;
}

/**
 * Body for the implicit-flow token capture endpoint
 * (POST /api/oauth/:provider/callback).
 *
 * The capture page extracts the access token from the URL fragment and
 * reports it here for persistence. No code, no client_secret — the token was
 * issued directly by the provider's implicit grant.
 */
export interface ImplicitTokenCallbackRequest {
  /** Opaque state used to correlate with the session created at /url. */
  state: string;
  /** The access token received in the redirect URL fragment. */
  accessToken: string;
  /** ISO 8601 expiry computed by the capture page from expires_in. */
  expiresAt: string;
}