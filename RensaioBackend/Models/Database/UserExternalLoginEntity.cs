using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace RensaioBackend.Models.Database;

/// <summary>
/// Links a Rensaio user to an identity at an external OpenID Connect provider.
/// One row per (Issuer, Subject) pair; a user may have several if the provider changes.
/// </summary>
public class UserExternalLoginEntity
{
    [Key]
    public Guid Id { get; set; }

    [Required]
    public Guid UserId { get; set; }

    /// <summary>The OIDC issuer URL the subject belongs to (the 'iss' claim).</summary>
    [Required]
    public string Issuer { get; set; } = string.Empty;

    /// <summary>The stable user identifier at the provider (the 'sub' claim).</summary>
    [Required]
    public string Subject { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? LastLoginAt { get; set; }

    [ForeignKey(nameof(UserId))]
    public UserEntity? User { get; set; }
}
