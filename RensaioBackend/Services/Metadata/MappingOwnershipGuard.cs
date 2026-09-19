using Microsoft.EntityFrameworkCore;
using RensaioBackend.Data;
using RensaioBackend.Models.Database;
using RensaioBackend.Models.Enums;

namespace RensaioBackend.Services.Metadata;

/// <summary>
/// Shared conflict-prevention guard used by every path that would persist a new
/// AutoMatched <c>(provider, externalId)</c> link (the metadata link engine and the
/// scrobbler auto-match). It answers one question: does another local series already
/// own that external id with an active claim (AutoMatched / UserConfirmed)?
///
/// If yes, linking again would create the wrong self-reinforcing linkage described in
/// the mapping-conflict repair plan (series A wrongly claims provider series B, then B
/// links back to A's ids). Callers must refuse the link and persist a Blocked row
/// (id-scoped) instead — the repair pass (<see cref="MappingConflictRepairService"/>)
/// settles deterministically WHO owns an id; this guard is the cheap per-write safety
/// net that keeps a wrong claim from ever being written again.
///
/// Static on purpose: both <see cref="MetadataLinkEngine"/> and
/// <see cref="RensaioBackend.Services.Scrobbling.SeriesMatchingService"/> need it without
/// introducing a DI cycle with the repair service (which depends on the link engine).
/// </summary>
public static class MappingOwnershipGuard
{
    /// <summary>
    /// Returns the mapping row that currently owns <paramref name="externalId"/> on
    /// <paramref name="provider"/> for a DIFFERENT series, or <c>null</c> when the id is
    /// unclaimed (or only claimed by <paramref name="excludeSeriesId"/> itself).
    /// Only active claims (AutoMatched / UserConfirmed with a non-empty id) are considered —
    /// Blocked / Unmatched / Ignored rows are refusals, not ownership.
    /// </summary>
    public static async Task<SeriesMappingEntity?> FindOwnerAsync(
        AppDbContext db,
        ExternalSeriesProvider provider,
        string externalId,
        Guid? excludeSeriesId = null,
        CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(externalId)) return null;

        var claims = await db.SeriesMappings
            .AsNoTracking()
            .Where(m => m.SeriesId != null
                && m.Provider == provider
                && m.ExternalSeriesId == externalId
                && (excludeSeriesId == null || m.SeriesId != excludeSeriesId)
                && (m.MappingStatus == SeriesMappingStatus.AutoMatched
                    || m.MappingStatus == SeriesMappingStatus.UserConfirmed))
            .ToListAsync(token);

        if (claims.Count == 0) return null;

        // Strongest owner: UserConfirmed beats AutoMatched, then higher UserRole, then
        // the earliest claim (LinkedDate), then the smallest series id (deterministic).
        return claims
            .OrderByDescending(m => m.MappingStatus == SeriesMappingStatus.UserConfirmed)
            .ThenByDescending(m => m.UserRole)
            .ThenBy(m => m.LinkedDate)
            .ThenBy(m => m.SeriesId)
            .First();
    }

    /// <summary>
    /// True when <paramref name="owner"/> is a strictly stronger / settled decision than a
    /// fresh AutoMatched candidate (User role, LinkedDate = now): the owner is UserConfirmed
    /// or was claimed earlier. Used by the link paths to decide whether to refuse the link.
    /// </summary>
    public static bool IsStrongerThanNewAutoMatch(SeriesMappingEntity owner)
        => owner.MappingStatus == SeriesMappingStatus.UserConfirmed
            || owner.UserRole > UserLevel.User
            || owner.LinkedDate <= DateTime.UtcNow;
}