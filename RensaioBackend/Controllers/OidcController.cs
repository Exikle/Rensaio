using Microsoft.AspNetCore.Mvc;
using RensaioBackend.Models.Database;
using RensaioBackend.Models.Dto;
using RensaioBackend.Services.Auth;
using RensaioBackend.Services.Auth.Oidc;
using RensaioBackend.Services.Settings;
using RensaioBackend.Services.Users;

namespace RensaioBackend.Controllers;

/// <summary>
/// OpenID Connect login endpoints. All three are public (see AuthMiddleware) and only
/// work when authentication is enabled and OIDC is configured.
///
/// Browser flow:
///   GET  /api/auth/oidc/login     → 302 to the identity provider
///   GET  /api/auth/oidc/callback  → 302 to /login?sso={code}  (or /login?error={code})
///   POST /api/auth/oidc/exchange  → { token, user }, same shape as /api/auth/login
/// </summary>
[ApiController]
public class OidcController : ControllerBase
{
    private const string StateCookie = "rensaio_oidc_state";

    private readonly OidcService _oidc;
    private readonly JwtTokenService _jwtTokenService;
    private readonly UserQueryService _userQueryService;
    private readonly UserCommandService _userCommandService;
    private readonly SettingsService _settingsService;
    private readonly ILogger<OidcController> _logger;

    public OidcController(
        OidcService oidc,
        JwtTokenService jwtTokenService,
        UserQueryService userQueryService,
        UserCommandService userCommandService,
        SettingsService settingsService,
        ILogger<OidcController> logger)
    {
        _oidc = oidc;
        _jwtTokenService = jwtTokenService;
        _userQueryService = userQueryService;
        _userCommandService = userCommandService;
        _settingsService = settingsService;
        _logger = logger;
    }

    /// <summary>
    /// GET /api/auth/oidc/login - Start the login. Redirects the browser to the provider.
    /// </summary>
    [HttpGet("/api/auth/oidc/login")]
    public async Task<IActionResult> Login([FromQuery] bool rememberMe = false, [FromQuery] string? returnTo = null, CancellationToken token = default)
    {
        var settings = await _settingsService.GetSettingsAsync(token);
        if (!settings.AuthenticationEnabled)
            return BadRequest(new { error = "Authentication is not enabled" });

        OidcOptions options = await _oidc.GetOptionsAsync(token);
        if (!options.IsConfigured)
            return BadRequest(new { error = "Single sign-on is not configured" });

        string redirectUri = await _oidc.GetRedirectUriAsync(options, Request, token);
        string safeReturnTo = IsLocalPath(returnTo) ? returnTo! : "/library";

        try
        {
            var (url, state) = await _oidc.BuildAuthorizationUrlAsync(options, redirectUri, rememberMe, safeReturnTo, token);

            // Bind the state to this browser. Lax (not Strict) because the callback arrives
            // as a top-level navigation from the provider's site.
            Response.Cookies.Append(StateCookie, state, new CookieOptions
            {
                HttpOnly = true,
                Secure = Request.IsHttps,
                SameSite = SameSiteMode.Lax,
                MaxAge = TimeSpan.FromMinutes(10),
                Path = "/api/auth/oidc",
            });

            return Redirect(url);
        }
        catch (OidcLoginException ex)
        {
            _logger.LogWarning("OIDC login could not start: {Message}", ex.Message);
            return Redirect(LoginErrorUrl(ex.Code));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "OIDC discovery failed for {Issuer}", options.Issuer);
            return Redirect(LoginErrorUrl("provider"));
        }
    }

    /// <summary>
    /// GET /api/auth/oidc/callback - Provider redirect target.
    /// </summary>
    [HttpGet("/api/auth/oidc/callback")]
    public async Task<IActionResult> Callback(
        [FromQuery] string? code,
        [FromQuery] string? state,
        [FromQuery] string? error,
        [FromQuery(Name = "error_description")] string? errorDescription,
        CancellationToken token = default)
    {
        string? cookieState = Request.Cookies[StateCookie];
        Response.Cookies.Delete(StateCookie, new CookieOptions { Path = "/api/auth/oidc" });

        if (!string.IsNullOrWhiteSpace(error))
        {
            _logger.LogWarning("OIDC provider returned error {Error}: {Description}", error, errorDescription);
            return Redirect(LoginErrorUrl(error == "access_denied" ? "denied" : "provider"));
        }

        if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(state))
            return Redirect(LoginErrorUrl("state"));

        if (string.IsNullOrWhiteSpace(cookieState) || !string.Equals(cookieState, state, StringComparison.Ordinal))
            return Redirect(LoginErrorUrl("state"));

        var settings = await _settingsService.GetSettingsAsync(token);
        if (!settings.AuthenticationEnabled)
            return BadRequest(new { error = "Authentication is not enabled" });

        OidcOptions options = await _oidc.GetOptionsAsync(token);
        if (!options.IsConfigured)
            return BadRequest(new { error = "Single sign-on is not configured" });

        try
        {
            var result = await _oidc.HandleCallbackAsync(options, code, state, token);
            string exchange = _oidc.CreateExchangeCode(result.User.Id, result.RememberMe);
            string target = $"/login?sso={Uri.EscapeDataString(exchange)}&returnTo={Uri.EscapeDataString(result.ReturnTo)}";
            return Redirect(target);
        }
        catch (OidcLoginException ex)
        {
            _logger.LogWarning("OIDC login failed ({Code}): {Message}", ex.Code, ex.Message);
            return Redirect(LoginErrorUrl(ex.Code));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "OIDC callback failed");
            return Redirect(LoginErrorUrl("provider"));
        }
    }

    /// <summary>
    /// POST /api/auth/oidc/exchange - Trade the one-time code from the callback for a session.
    /// </summary>
    [HttpPost("/api/auth/oidc/exchange")]
    public async Task<ActionResult<LoginResponseDto>> Exchange([FromBody] OidcExchangeRequestDto request, CancellationToken token)
    {
        var entry = _oidc.ConsumeExchangeCode(request.Code);
        if (entry == null)
            return Unauthorized(new { error = "Login code is invalid or expired" });

        UserEntity? user = await _userQueryService.GetByIdAsync(entry.Value.UserId, token);
        if (user == null || !user.IsActive)
            return Unauthorized(new { error = "Account not found or disabled" });

        string accessToken = _jwtTokenService.GenerateAccessToken(user);

        if (entry.Value.RememberMe)
        {
            var (rawRefreshToken, refreshHash) = _jwtTokenService.GenerateRefreshToken();
            DateTime expiresAt = DateTime.UtcNow.AddDays(_jwtTokenService.GetRememberMeExpirationDays());
            await _userCommandService.StoreRefreshTokenAsync(user, refreshHash, expiresAt, token);

            Response.Cookies.Append("refresh_token", rawRefreshToken, new CookieOptions
            {
                HttpOnly = true,
                Secure = true,
                SameSite = SameSiteMode.Strict,
                Expires = expiresAt,
                Path = "/api/auth/refresh"
            });
        }

        return Ok(new LoginResponseDto
        {
            Token = accessToken,
            User = UserDto.FromEntity(user)
        });
    }

    private static string LoginErrorUrl(string code) => "/login?error=" + Uri.EscapeDataString("oidc_" + code);

    /// <summary>Only same-origin absolute paths are accepted as a post-login destination.</summary>
    private static bool IsLocalPath(string? path) =>
        !string.IsNullOrWhiteSpace(path) && path.StartsWith('/') && !path.StartsWith("//") && !path.StartsWith("/\\");
}
