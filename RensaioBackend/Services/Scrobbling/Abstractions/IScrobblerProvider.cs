using RensaioBackend.Models.Enums;

namespace RensaioBackend.Services.Scrobbling.Abstractions;

// NOTE: The provider interfaces (IExternalSeriesProvider + IScrobblerProvider sub-interface)
// live in IExternalSeriesProvider.cs. This file keeps the shared auth/misc DTOs.

// ── Shared DTOs for auth ──

public class ScrobblerAuthUrlResult
{
    public string AuthUrl { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public string? CodeVerifier { get; set; } // PKCE
}

public class ScrobblerTokenResult
{
    public bool Success { get; set; }
    public string? AccessToken { get; set; }
    public string? RefreshToken { get; set; }
    public DateTime? ExpiresAt { get; set; }
    public string? ErrorMessage { get; set; }
}

/// <summary>
/// Request DTO for direct password-based authentication.
/// </summary>
public class DirectAuthRequest
{
    public string? Username { get; set; }   // email for Kitsu, username for MangaDex
    public string? Password { get; set; }
    public string? ClientId { get; set; }       // MangaDex personal client only
    public string? ClientSecret { get; set; }   // MangaDex personal client only
}

/// <summary>
/// Deserialized payload stored inside the encrypted RefreshToken field for direct auth providers.
/// </summary>
internal class ScrobblerRefreshPayload
{
    public string RefreshToken { get; set; } = string.Empty;
    public string? ClientId { get; set; }
    public string? ClientSecret { get; set; }
}

/// <summary>
/// A single cross-site link, e.g. <c>anilist:30002</c>.
/// </summary>
public class SeriesSiteLink
{
    public ExternalSeriesProvider Site { get; set; }
    public string ExternalId { get; set; } = string.Empty;
    public string? Title { get; set; }
}

/// <summary>
/// Result of <see cref="IExternalSeriesProvider.FetchSeriesMetadataAsync"/>.
/// <see cref="MetaData"/> preserves the provider's native JSON payload verbatim;
/// <see cref="LinkedSitesIds"/> is a list of canonical "site:id" strings (e.g. "anilist:30002");
/// <see cref="AlternativeTitles"/> contains only distinct alternate titles — the primary
/// <see cref="Title"/> is never included (case-insensitive compare).
/// </summary>
public class SeriesMetadataResult
{
    public string ExternalId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;

    /// <summary>Native JSON payload from the provider (verbatim, provider-native structure).</summary>
    public string MetaData { get; set; } = string.Empty;

    /// <summary>Canonical "site:id" pairs of the same series on other sites.</summary>
    public List<string> LinkedSitesIds { get; set; } = [];

    /// <summary>Distinct alternate titles for the series, excluding <see cref="Title"/>.</summary>
    public List<string> AlternativeTitles { get; set; } = [];

    /// <summary>Cover image URL for the series as reported by the provider (may be absolute or templated).</summary>
    public string? CoverUrl { get; set; }
}