using RensaioBackend.Models.Dto;
using RensaioBackend.Models.Enums;

namespace RensaioBackend.Services.Auth.Oidc;

/// <summary>
/// Effective OpenID Connect settings.
///
/// The basics (Enabled, Issuer, ClientId, ClientSecret, ButtonLabel) are editable
/// from the Settings page and stored in the database. Everything else comes from
/// the "Oidc" section of appsettings.json or environment variables (Oidc__Key).
/// Any basic value present in configuration overrides the stored one, so a Docker
/// deployment can be driven entirely by environment variables.
/// </summary>
public class OidcOptions
{
    public const string SectionName = "Oidc";

    public bool Enabled { get; set; }
    public string Issuer { get; set; } = string.Empty;
    public string ClientId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;
    public string ButtonLabel { get; set; } = "Single Sign-On";

    /// <summary>Space-separated scopes requested at the provider.</summary>
    public string Scopes { get; set; } = "openid profile email groups";

    /// <summary>Claim used as the Rensaio username for matching and auto-registration.</summary>
    public string UsernameClaim { get; set; } = "preferred_username";

    /// <summary>Claim holding the user's group list. Empty disables group mapping.</summary>
    public string GroupsClaim { get; set; } = "groups";

    /// <summary>Members of this group are set to Admin on every login. Empty disables.</summary>
    public string AdminGroup { get; set; } = string.Empty;

    /// <summary>Members of this group are set to Manager on every login. Empty disables.</summary>
    public string ManagerGroup { get; set; } = string.Empty;

    /// <summary>Create a Rensaio user on first login when no matching user exists.</summary>
    public bool AutoRegister { get; set; }

    /// <summary>Level given to auto-registered users, and to mapped users in no mapped group.</summary>
    public UserLevel DefaultLevel { get; set; } = UserLevel.User;

    /// <summary>Hide the username/password form on the login page.</summary>
    public bool HidePasswordLogin { get; set; }

    /// <summary>Send the browser straight to the provider instead of showing the login page.</summary>
    public bool AutoRedirect { get; set; }

    /// <summary>Explicit callback URL. Defaults to {ExternalDomain}/api/auth/oidc/callback.</summary>
    public string RedirectUri { get; set; } = string.Empty;

    /// <summary>Mirror the provider's 'picture' claim onto the user's avatar on every login.</summary>
    public bool SyncAvatar { get; set; } = true;

    public bool HasGroupMapping =>
        !string.IsNullOrWhiteSpace(GroupsClaim)
        && (!string.IsNullOrWhiteSpace(AdminGroup) || !string.IsNullOrWhiteSpace(ManagerGroup));

    public bool IsConfigured =>
        Enabled && !string.IsNullOrWhiteSpace(Issuer) && !string.IsNullOrWhiteSpace(ClientId);

    /// <summary>
    /// Merges stored settings with configuration. Configuration wins for any key it defines.
    /// </summary>
    public static OidcOptions Resolve(IConfiguration configuration, EditableSettingsDto settings)
    {
        var options = new OidcOptions
        {
            Enabled = settings.OidcEnabled,
            Issuer = settings.OidcIssuer,
            ClientId = settings.OidcClientId,
            ClientSecret = settings.OidcClientSecret,
            ButtonLabel = settings.OidcButtonLabel,
        };

        // Bind advanced options plus any basic overrides from the "Oidc" section.
        // Bind() only touches keys that exist, so stored values survive when unset.
        configuration.GetSection(SectionName).Bind(options);

        options.Issuer = options.Issuer.Trim().TrimEnd('/');
        options.ClientId = options.ClientId.Trim();
        if (string.IsNullOrWhiteSpace(options.ButtonLabel))
            options.ButtonLabel = "Single Sign-On";
        return options;
    }

    /// <summary>
    /// True when any basic value is supplied through configuration, meaning the
    /// Settings page fields are informational only.
    /// </summary>
    public static bool IsManagedByConfig(IConfiguration configuration)
    {
        var section = configuration.GetSection(SectionName);
        return section.GetValue<bool?>("Enabled") != null
            || !string.IsNullOrWhiteSpace(section["Issuer"])
            || !string.IsNullOrWhiteSpace(section["ClientId"])
            || !string.IsNullOrWhiteSpace(section["ClientSecret"]);
    }
}
