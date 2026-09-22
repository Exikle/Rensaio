using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using RensaioBackend.Data;
using RensaioBackend.Models.Database;
using RensaioBackend.Models.Enums;
using RensaioBackend.Services.Settings;
using RensaioBackend.Services.Users;

namespace RensaioBackend.Services.Auth.Oidc;

/// <summary>
/// Thrown when an OIDC login cannot complete. <see cref="Code"/> is a short, stable
/// identifier the login page maps to a message (e.g. "no_account", "inactive").
/// </summary>
public class OidcLoginException : Exception
{
    public string Code { get; }
    public OidcLoginException(string code, string message) : base(message) => Code = code;
}

/// <summary>
/// OpenID Connect relying party: authorization code flow with PKCE.
///
/// Rensaio has no ASP.NET authentication pipeline (sessions are self-issued JWTs
/// validated by <see cref="AuthMiddleware"/>), so this service does the handshake
/// directly: builds the authorization URL, exchanges the code, validates the ID token
/// against the provider's published keys, then maps the identity onto a user row.
/// </summary>
public class OidcService
{
    public const string HttpClientName = "Oidc";
    private const string StateCachePrefix = "oidc:state:";
    private const string ExchangeCachePrefix = "oidc:exchange:";
    private static readonly TimeSpan StateLifetime = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan ExchangeLifetime = TimeSpan.FromMinutes(1);

    private readonly AppDbContext _db;
    private readonly IConfiguration _configuration;
    private readonly SettingsService _settingsService;
    private readonly OidcDiscoveryCache _discovery;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IMemoryCache _cache;
    private readonly UserCommandService _userCommandService;
    private readonly ILogger<OidcService> _logger;

    public OidcService(
        AppDbContext db,
        IConfiguration configuration,
        SettingsService settingsService,
        OidcDiscoveryCache discovery,
        IHttpClientFactory httpClientFactory,
        IMemoryCache cache,
        UserCommandService userCommandService,
        ILogger<OidcService> logger)
    {
        _db = db;
        _configuration = configuration;
        _settingsService = settingsService;
        _discovery = discovery;
        _httpClientFactory = httpClientFactory;
        _cache = cache;
        _userCommandService = userCommandService;
        _logger = logger;
    }

    /// <summary>Resolves the effective options (stored settings merged with configuration).</summary>
    public async Task<OidcOptions> GetOptionsAsync(CancellationToken token = default)
    {
        var settings = await _settingsService.GetSettingsAsync(token).ConfigureAwait(false);
        var options = OidcOptions.Resolve(_configuration, settings);
        if (options.BindError != null)
            _logger.LogWarning("The 'Oidc' configuration section could not be read and was ignored: {Error}", options.BindError);
        return options;
    }

    /// <summary>
    /// Fetches the discovery document and checks it really belongs to the configured issuer
    /// (OpenID Connect Discovery §4.3), so a misconfigured or hijacked URL cannot mint tokens for us.
    /// </summary>
    private async Task<OpenIdConnectConfiguration> GetDiscoveryAsync(OidcOptions options, CancellationToken token)
    {
        var discovery = await _discovery.GetAsync(options.Issuer, token).ConfigureAwait(false);
        if (!string.Equals(discovery.Issuer?.TrimEnd('/'), options.Issuer, StringComparison.Ordinal))
        {
            _logger.LogWarning("OIDC discovery issuer '{Actual}' does not match configured issuer '{Expected}'", discovery.Issuer, options.Issuer);
            throw new OidcLoginException("provider", "The provider's issuer does not match the configured Issuer URL.");
        }
        return discovery;
    }

    /// <summary>
    /// Resolves the callback URL: explicit RedirectUri, then ExternalDomain, then the request host.
    /// </summary>
    public async Task<string> GetRedirectUriAsync(OidcOptions options, HttpRequest request, CancellationToken token = default)
    {
        if (!string.IsNullOrWhiteSpace(options.RedirectUri))
            return options.RedirectUri;

        var settings = await _settingsService.GetSettingsAsync(token).ConfigureAwait(false);
        string baseUrl = !string.IsNullOrWhiteSpace(settings.ExternalDomain)
            ? settings.ExternalDomain.TrimEnd('/')
            : $"{request.Scheme}://{request.Host}";
        return baseUrl + "/api/auth/oidc/callback";
    }

    // ---------------------------------------------------------------------
    // Step 1: authorization request
    // ---------------------------------------------------------------------

    private sealed record PendingLogin(string CodeVerifier, string Nonce, string RedirectUri, bool RememberMe, string ReturnTo);

    /// <summary>
    /// Builds the provider authorization URL and remembers the PKCE verifier and nonce
    /// under a random state for <see cref="StateLifetime"/>.
    /// </summary>
    public async Task<(string Url, string State)> BuildAuthorizationUrlAsync(
        OidcOptions options, string redirectUri, bool rememberMe, string returnTo, CancellationToken token)
    {
        var discovery = await GetDiscoveryAsync(options, token).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(discovery.AuthorizationEndpoint))
            throw new OidcLoginException("provider", "Provider discovery document has no authorization_endpoint.");

        string state = RandomUrlSafe(32);
        string nonce = RandomUrlSafe(32);
        string codeVerifier = RandomUrlSafe(64);
        string codeChallenge = Base64UrlEncoder.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier)));

        _cache.Set(StateCachePrefix + state, new PendingLogin(codeVerifier, nonce, redirectUri, rememberMe, returnTo), StateLifetime);

        var query = new Dictionary<string, string?>
        {
            ["response_type"] = "code",
            ["client_id"] = options.ClientId,
            ["redirect_uri"] = redirectUri,
            ["scope"] = options.Scopes,
            ["state"] = state,
            ["nonce"] = nonce,
            ["code_challenge"] = codeChallenge,
            ["code_challenge_method"] = "S256",
        };

        return (Microsoft.AspNetCore.WebUtilities.QueryHelpers.AddQueryString(discovery.AuthorizationEndpoint, query), state);
    }

    // ---------------------------------------------------------------------
    // Step 2: callback
    // ---------------------------------------------------------------------

    public sealed record CallbackResult(UserEntity User, bool RememberMe, string ReturnTo);

    /// <summary>
    /// Completes the login: validates state, exchanges the code, validates the ID token,
    /// and resolves (or creates) the Rensaio user. Throws <see cref="OidcLoginException"/>
    /// with a stable code on any failure.
    /// </summary>
    public async Task<CallbackResult> HandleCallbackAsync(OidcOptions options, string code, string state, CancellationToken token)
    {
        if (!_cache.TryGetValue(StateCachePrefix + state, out PendingLogin? pending) || pending == null)
            throw new OidcLoginException("state", "Login request expired or was not started here.");
        _cache.Remove(StateCachePrefix + state);

        var discovery = await GetDiscoveryAsync(options, token).ConfigureAwait(false);
        var (idToken, accessToken) = await ExchangeCodeAsync(options, discovery, code, pending, token).ConfigureAwait(false);
        ClaimsPrincipal principal = ValidateIdToken(options, discovery, idToken, pending.Nonce);

        string? subject = principal.FindFirst("sub")?.Value ?? principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrWhiteSpace(subject))
            throw new OidcLoginException("claims", "ID token has no 'sub' claim.");

        string issuer = principal.FindFirst("iss")?.Value ?? options.Issuer;

        // Some providers keep profile/group claims out of the ID token and only serve
        // them from userinfo. Fetch it when something we need is missing.
        bool needsUsername = principal.FindFirst(options.UsernameClaim) == null;
        bool needsGroups = options.HasGroupMapping && principal.FindFirst(options.GroupsClaim) == null;
        bool needsPicture = options.SyncAvatar && principal.FindFirst("picture") == null;
        if ((needsUsername || needsGroups || needsPicture) && !string.IsNullOrWhiteSpace(accessToken))
            principal = await MergeUserInfoAsync(discovery, accessToken, subject, principal, token).ConfigureAwait(false);

        UserEntity user = await ResolveUserAsync(options, issuer, subject, principal, token).ConfigureAwait(false);
        return new CallbackResult(user, pending.RememberMe, pending.ReturnTo);
    }

    /// <summary>
    /// Calls the userinfo endpoint and adds any claims the ID token did not carry.
    /// Failures are non-fatal: the caller falls back to what the ID token had.
    /// </summary>
    private async Task<ClaimsPrincipal> MergeUserInfoAsync(OpenIdConnectConfiguration discovery, string accessToken, string subject, ClaimsPrincipal principal, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(discovery.UserInfoEndpoint))
            return principal;

        try
        {
            var http = _httpClientFactory.CreateClient(HttpClientName);
            using var request = new HttpRequestMessage(HttpMethod.Get, discovery.UserInfoEndpoint);
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
            using var response = await http.SendAsync(request, token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return principal;

            string body = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            using var json = JsonDocument.Parse(body);
            if (json.RootElement.ValueKind != JsonValueKind.Object)
                return principal;

            // The userinfo response must describe the same subject as the ID token.
            if (json.RootElement.TryGetProperty("sub", out var subElement) && subElement.GetString() != subject)
                return principal;

            var identity = new ClaimsIdentity(principal.Identity);
            foreach (var property in json.RootElement.EnumerateObject())
            {
                if (identity.HasClaim(c => c.Type == property.Name))
                    continue;
                if (property.Value.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in property.Value.EnumerateArray())
                        if (item.ValueKind == JsonValueKind.String)
                            identity.AddClaim(new Claim(property.Name, item.GetString()!));
                }
                else if (property.Value.ValueKind is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
                {
                    identity.AddClaim(new Claim(property.Name, property.Value.ToString()));
                }
            }
            return new ClaimsPrincipal(identity);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "OIDC userinfo request failed; continuing with ID token claims");
            return principal;
        }
    }

    private async Task<(string IdToken, string? AccessToken)> ExchangeCodeAsync(OidcOptions options, OpenIdConnectConfiguration discovery, string code, PendingLogin pending, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(discovery.TokenEndpoint))
            throw new OidcLoginException("provider", "Provider discovery document has no token_endpoint.");

        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = pending.RedirectUri,
            ["client_id"] = options.ClientId,
            ["code_verifier"] = pending.CodeVerifier,
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, discovery.TokenEndpoint)
        {
            Content = new FormUrlEncodedContent(form)
        };
        if (!string.IsNullOrWhiteSpace(options.ClientSecret))
        {
            // client_secret_basic is the OIDC default and the one every provider must accept
            // (RFC 6749 §2.3.1: credentials are form-urlencoded before base64).
            string credentials = Uri.EscapeDataString(options.ClientId) + ":" + Uri.EscapeDataString(options.ClientSecret);
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(credentials)));
        }

        var http = _httpClientFactory.CreateClient(HttpClientName);
        using var response = await http.SendAsync(request, token).ConfigureAwait(false);
        string body = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("OIDC token endpoint returned {Status}: {Body}", (int)response.StatusCode, body);
            throw new OidcLoginException("token", "The identity provider rejected the login.");
        }

        using var json = JsonDocument.Parse(body);
        if (!json.RootElement.TryGetProperty("id_token", out var idTokenElement) || idTokenElement.ValueKind != JsonValueKind.String)
            throw new OidcLoginException("token", "Token response has no id_token. Is the 'openid' scope requested?");

        string? accessToken = json.RootElement.TryGetProperty("access_token", out var accessElement) && accessElement.ValueKind == JsonValueKind.String
            ? accessElement.GetString()
            : null;

        return (idTokenElement.GetString()!, accessToken);
    }

    private ClaimsPrincipal ValidateIdToken(OidcOptions options, OpenIdConnectConfiguration discovery, string idToken, string expectedNonce)
    {
        var parameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = discovery.Issuer,
            ValidateAudience = true,
            ValidAudience = options.ClientId,
            ValidateIssuerSigningKey = true,
            IssuerSigningKeys = discovery.SigningKeys,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromMinutes(2),
        };

        ClaimsPrincipal principal;
        try
        {
            var handler = new JwtSecurityTokenHandler { MapInboundClaims = false };
            principal = handler.ValidateToken(idToken, parameters, out _);
        }
        catch (SecurityTokenSignatureKeyNotFoundException)
        {
            // Key rollover: refresh discovery and let the user retry.
            _discovery.Invalidate(options.Issuer);
            throw new OidcLoginException("token", "Signing key not recognised. Please try again.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "OIDC ID token validation failed");
            throw new OidcLoginException("token", "ID token validation failed.");
        }

        string? nonce = principal.FindFirst("nonce")?.Value;
        if (!string.Equals(nonce, expectedNonce, StringComparison.Ordinal))
            throw new OidcLoginException("token", "ID token nonce mismatch.");

        return principal;
    }

    // ---------------------------------------------------------------------
    // Step 3: identity → user
    // ---------------------------------------------------------------------

    private async Task<UserEntity> ResolveUserAsync(OidcOptions options, string issuer, string subject, ClaimsPrincipal principal, CancellationToken token)
    {
        // Only the configured claim identifies the user. No fallback to 'name' or 'email':
        // those are often user-editable at the provider and would allow picking a victim account.
        string? username = principal.FindFirst(options.UsernameClaim)?.Value?.Trim();
        bool hasGroupsClaim = principal.HasClaim(c => c.Type == options.GroupsClaim);
        List<string> groups = principal.FindAll(options.GroupsClaim).Select(c => c.Value).ToList();

        UserExternalLoginEntity? link = await _db.UserExternalLogins
            .Include(l => l.User)
            .FirstOrDefaultAsync(l => l.Issuer == issuer && l.Subject == subject, token)
            .ConfigureAwait(false);

        UserEntity? user = link?.User;
        bool created = false;

        if (user == null)
        {
            if (string.IsNullOrWhiteSpace(username))
                throw new OidcLoginException("claims", $"The identity provider returned no '{options.UsernameClaim}' claim to identify the user.");

            user = await FindUserByNameAsync(username, token).ConfigureAwait(false);

            // The single Owner account is never linked implicitly: it must keep password login.
            if (user is { Level: UserLevel.Owner })
                throw new OidcLoginException("owner", "The owner account cannot sign in through single sign-on.");

            if (user == null)
            {
                if (!options.AutoRegister)
                    throw new OidcLoginException("no_account", $"No Rensaio account matches '{username}'. Ask an administrator to create one.");

                // Same rule as POST /api/users.
                if (username.Length < 3 || username.Length > 32)
                    throw new OidcLoginException("claims", $"Username '{username}' from the identity provider must be between 3 and 32 characters.");

                UserLevel level = options.HasGroupMapping && hasGroupsClaim ? MapLevel(options, groups) : options.SafeDefaultLevel;
                user = await _userCommandService.CreateUserAsync(username, level, token).ConfigureAwait(false);
                created = true;
            }

            link = new UserExternalLoginEntity
            {
                Id = Guid.NewGuid(),
                UserId = user.Id,
                Issuer = issuer,
                Subject = subject,
                CreatedAt = DateTime.UtcNow,
            };
            _db.UserExternalLogins.Add(link);
            _logger.LogInformation("Linked user '{Username}' to OIDC subject {Subject} at {Issuer}", user.Username, subject, issuer);
        }

        if (!user.IsActive)
            throw new OidcLoginException("inactive", "This account is disabled.");

        // Group → level sync on every login. The Owner is never touched and never granted.
        // A login without the groups claim at all is left alone rather than demoted.
        if (!created && options.HasGroupMapping && hasGroupsClaim && user.Level != UserLevel.Owner)
        {
            UserLevel mapped = MapLevel(options, groups);
            if (mapped != user.Level)
            {
                _logger.LogInformation("OIDC group sync: '{Username}' {Old} -> {New}", user.Username, user.Level, mapped);
                user.Level = mapped;
            }
        }

        if (options.SyncAvatar)
            await SyncAvatarAsync(user, principal.FindFirst("picture")?.Value, token).ConfigureAwait(false);

        link!.LastLoginAt = DateTime.UtcNow;
        user.LastLoginAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(token).ConfigureAwait(false);
        return user;
    }

    private const int MaxAvatarBytes = 2 * 1024 * 1024; // same limit as PUT /api/auth/me

    /// <summary>
    /// Downloads the provider's profile picture and stores it as the user's avatar when it
    /// changed. Failures are logged and ignored; a missing picture never blocks a login.
    /// </summary>
    private async Task SyncAvatarAsync(UserEntity user, string? pictureUrl, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(pictureUrl) || !Uri.TryCreate(pictureUrl, UriKind.Absolute, out var uri))
            return;
        if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
            return;

        try
        {
            var http = _httpClientFactory.CreateClient(HttpClientName);
            using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return;

            string? contentType = response.Content.Headers.ContentType?.MediaType;
            if (contentType == null || !contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                return;
            if (response.Content.Headers.ContentLength > MaxAvatarBytes)
                return;

            using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            byte[] chunk = new byte[16 * 1024];
            int read;
            while ((read = await stream.ReadAsync(chunk, token).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > MaxAvatarBytes)
                    return;
                buffer.Write(chunk, 0, read);
            }

            byte[] bytes = buffer.ToArray();
            if (bytes.Length == 0 || (user.AvatarBlob != null && user.AvatarBlob.AsSpan().SequenceEqual(bytes)))
                return;

            user.AvatarBlob = bytes;
            user.AvatarContentType = contentType;
            _logger.LogInformation("OIDC avatar updated for '{Username}' ({Bytes} bytes)", user.Username, bytes.Length);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "OIDC avatar download failed for '{Username}' from {Url}", user.Username, pictureUrl);
        }
    }

    private static UserLevel MapLevel(OidcOptions options, List<string> groups)
    {
        if (!string.IsNullOrWhiteSpace(options.AdminGroup) && groups.Contains(options.AdminGroup, StringComparer.Ordinal))
            return UserLevel.Admin;
        if (!string.IsNullOrWhiteSpace(options.ManagerGroup) && groups.Contains(options.ManagerGroup, StringComparer.Ordinal))
            return UserLevel.Manager;
        return options.SafeDefaultLevel;
    }

    /// <summary>
    /// Finds the user to link on first login: an exact username match first, then a
    /// case-insensitive one only if it is unambiguous (usernames use a BINARY collation,
    /// so 'Admin' and 'admin' may both exist).
    /// </summary>
    private async Task<UserEntity?> FindUserByNameAsync(string username, CancellationToken token)
    {
        UserEntity? exact = await _db.Users.FirstOrDefaultAsync(u => u.Username == username, token).ConfigureAwait(false);
        if (exact != null)
            return exact;

        string lowered = username.ToLowerInvariant();
        var candidates = await _db.Users.Where(u => u.Username.ToLower() == lowered).Take(2).ToListAsync(token).ConfigureAwait(false);
        if (candidates.Count > 1)
            throw new OidcLoginException("no_account", $"More than one Rensaio account matches '{username}'. Ask an administrator to link it.");
        return candidates.SingleOrDefault();
    }

    // ---------------------------------------------------------------------
    // Step 4: hand the session to the browser
    // ---------------------------------------------------------------------

    private sealed record PendingExchange(Guid UserId, bool RememberMe, string Binder);

    /// <summary>
    /// Issues a one-time code the login page exchanges for a session via POST, plus a
    /// random binder the controller stores in an HttpOnly cookie. The exchange only
    /// succeeds from the browser that completed the callback, so a code pasted into
    /// someone else's browser (login CSRF) is useless. Both die after one use or one minute.
    /// </summary>
    public (string Code, string Binder) CreateExchangeCode(Guid userId, bool rememberMe)
    {
        string code = RandomUrlSafe(32);
        string binder = RandomUrlSafe(32);
        _cache.Set(ExchangeCachePrefix + code, new PendingExchange(userId, rememberMe, binder), ExchangeLifetime);
        return (code, binder);
    }

    public (Guid UserId, bool RememberMe)? ConsumeExchangeCode(string? code, string? binder)
    {
        if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(binder))
            return null;
        if (!_cache.TryGetValue(ExchangeCachePrefix + code, out PendingExchange? entry) || entry == null)
            return null;
        // Compare before removing: a stray request without the binder must not be able to
        // burn the code out from under the browser that legitimately holds it.
        if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(entry.Binder), Encoding.UTF8.GetBytes(binder)))
            return null;
        _cache.Remove(ExchangeCachePrefix + code);
        return (entry.UserId, entry.RememberMe);
    }

    /// <summary>Returns the ids of users that have at least one linked external login.</summary>
    public async Task<HashSet<Guid>> GetLinkedUserIdsAsync(CancellationToken token = default)
    {
        var ids = await _db.UserExternalLogins.Select(l => l.UserId).Distinct().ToListAsync(token).ConfigureAwait(false);
        return ids.ToHashSet();
    }

    private static string RandomUrlSafe(int bytes) => Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(bytes));
}
