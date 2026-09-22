using System.Text.Json.Serialization;
using RensaioBackend.Extensions;

namespace RensaioBackend.Models.Dto;

public class SettingsDto : EditableSettingsDto
{
    private string _storageFolder = string.Empty;

    [JsonPropertyName("storageFolder")]
    public string StorageFolder
    {
        get => _storageFolder.SanitizeDirectory();
        set => _storageFolder = value;
    }

    /// <summary>
    /// True when any of the basic OIDC values (enabled, issuer, client id, secret)
    /// is supplied via appsettings.json or environment variables. The UI shows the
    /// fields read-only in that case, since config overrides what is stored here.
    /// Server-computed, never persisted.
    /// </summary>
    [JsonPropertyName("oidcManagedByConfig")]
    public bool OidcManagedByConfig { get; set; }

    /// <summary>
    /// True when a client secret is configured. The secret itself is never sent to
    /// clients; <see cref="SettingsController"/> blanks it. Server-computed, never persisted.
    /// </summary>
    [JsonPropertyName("oidcClientSecretSet")]
    public bool OidcClientSecretSet { get; set; }

}