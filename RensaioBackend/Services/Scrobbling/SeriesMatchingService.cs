using RensaioBackend.Data;
using RensaioBackend.Models;
using RensaioBackend.Services.Images;
using RensaioBackend.Services.Settings;
using RensaioBackend.Extensions;
using RensaioBackend.Models.Database;
using RensaioBackend.Models.Dto;
using RensaioBackend.Models.Enums;
using RensaioBackend.Services.Metadata;
using RensaioBackend.Services.Scrobbling.Abstractions;
using RensaioBackend.Services.Series;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace RensaioBackend.Services.Scrobbling;

/// <summary>
/// Automatic + manual series matching between local series and external providers.
/// Global-only: mapping state lives in <see cref="SeriesMappingEntity"/> (the per-user
/// <c>UserSeriesMappings</c> table has been dropped). userId is accepted for API back-compat
/// and only used to pick the provider token; mapping writes are app-wide.
/// Provides minimal logging on match decisions.
/// </summary>
public class SeriesMatchingService
{
    private readonly AppDbContext _db;
    private readonly ExternalSeriesProviderFactory _providerFactory;
    private readonly TitleMatcher _titleMatcher;
    private readonly SeriesStateService _seriesStateService;
    private readonly SettingsService _settings;
    private readonly ThumbCacheService _thumbCache;
    private readonly ILogger<SeriesMatchingService> _logger;
    private readonly MetadataLinkEngine _linkEngine;
    private readonly Contributions.ContributionPropagationService _contributionSync;
    private const string ImagePrefix = "/api/image/";

    public SeriesMatchingService(
        AppDbContext db,
        ExternalSeriesProviderFactory providerFactory,
        TitleMatcher titleMatcher,
        SeriesStateService seriesStateService,
        SettingsService settings,
        ThumbCacheService thumbCache,
        ILogger<SeriesMatchingService> logger,
        MetadataLinkEngine linkEngine,
        Contributions.ContributionPropagationService contributionSync)
    {
        _db = db;
        _providerFactory = providerFactory;
        _titleMatcher = titleMatcher;
        _seriesStateService = seriesStateService;
        _settings = settings;
        _thumbCache = thumbCache;
        _logger = logger;
        _linkEngine = linkEngine;
        _contributionSync = contributionSync;
    }

    /// <summary>Best-effort contribution propagation after a mapping mutation.</summary>
    private async Task SyncContributionAsync(Guid seriesId, CancellationToken token)
    {
        try
        {
            await _contributionSync.SyncSeriesAsync(seriesId, token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to propagate contribution state for series {SeriesId}", seriesId);
        }
    }

    private async ValueTask<string?> ResolveCoverUrlAsync(string? thumbnailUrl, CancellationToken token = default)
    {
        if (string.IsNullOrEmpty(thumbnailUrl))
            return null;
        var key = await _thumbCache.GetKeyAsync(thumbnailUrl, token).ConfigureAwait(false);
        if (string.IsNullOrEmpty(key))
            return null;
        return ImagePrefix + key;
    }

    /// <summary>Auto-match a single series across the user's enabled providers (global writes).</summary>
    public async Task AutoMatchSeriesAsync(Guid userId, Guid seriesId, CancellationToken token = default)
    {
        var configs = await _db.UserScrobblerConfigs
            .Where(c => c.UserId == userId && c.IsEnabled)
            .ToListAsync(token);
        var series = await _db.Series
            .Include(s => s.Sources)
            .FirstOrDefaultAsync(s => s.Id == seriesId, token);
        if (series == null)
        {
            _logger.LogDebug("Auto-match skipped: series {SeriesId} not found", seriesId);
            return;
        }
        foreach (var config in configs)
        {
            await AutoMatchForProviderAsync(userId, series, config.Provider, token);
        }
        _logger.LogDebug("Auto-match finished for series {SeriesId} across {Count} providers", seriesId, configs.Count);
    }

    /// <summary>Auto-match all unmatched series for a user across a provider (global writes).</summary>
    public async Task<AutoMatchResultDto> AutoMatchAllAsync(Guid userId, ExternalSeriesProvider provider, CancellationToken token = default)
    {
        var result = new AutoMatchResultDto();
        var config = await _db.UserScrobblerConfigs
            .FirstOrDefaultAsync(c => c.UserId == userId && c.Provider == provider, token);
        if (config == null || !config.IsEnabled)
            return result;

        var seriesList = await _db.Series
            .Include(s => s.Sources)
            .ToListAsync(token);
        result.TotalSeries = seriesList.Count;

        foreach (var series in seriesList)
        {
            var existingMapping = await _db.SeriesMappings
                .FirstOrDefaultAsync(m => m.SeriesId == series.Id && m.Provider == provider, token);
            if (existingMapping != null && existingMapping.MappingStatus != SeriesMappingStatus.Unmatched)
            {
                result.LeftUnmatched++;
                continue;
            }

            var matchResult = await TryAutoMatchAsync(userId, series, provider, token);
            if (matchResult != null)
            {
                result.AutoMatched++;
                if (matchResult.MatchScore.HasValue && matchResult.MatchScore.Value < 0.95)
                    result.SuggestedMatches.Add(matchResult);
            }
            else
            {
                result.LeftUnmatched++;
            }
        }
        _logger.LogDebug("Auto-match all complete for provider {Provider}: {AutoMatched} matched, {Left} unmatched",
            provider, result.AutoMatched, result.LeftUnmatched);
        return result;
    }

    /// <summary>Search an external provider for a series (uses the user's token).</summary>
    public async Task<List<ScrobblerSearchResult>> SearchExternalSeriesAsync(
        Guid userId, ExternalSeriesProvider provider, string query, CancellationToken token = default)
    {
        var scrobbler = _providerFactory.GetProvider(provider);
        if (scrobbler == null) return [];
        await scrobbler.EnsureAuthenticatedAsync(userId, token);
        var results = await scrobbler.SearchSeriesAsync(query, token);
        // Rewrite provider cover URLs through the image cache (Cloudflare bypass + etag + cache).
        await _thumbCache.PopulateThumbsAsync(results, token: token);
        return results;
    }

    /// <summary>Confirm a manual match — writes the global SeriesMapping (app-wide).</summary>
    public async Task ConfirmMatchAsync(Guid userId, Guid seriesId, ExternalSeriesProvider provider,
        string externalSeriesId, string? externalTitle = null, CancellationToken token = default)
    {
        var mapping = await _db.SeriesMappings
            .FirstOrDefaultAsync(m => m.SeriesId == seriesId && m.Provider == provider, token);

        // Blocked = id-level rule: you can't match to the blocked id, but you CAN match another id.
        if (mapping?.MappingStatus == SeriesMappingStatus.Blocked &&
            string.Equals(mapping.ExternalSeriesId, externalSeriesId, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Cannot match series {seriesId} to the blocked id '{externalSeriesId}' on {provider}.");
        }
        var now = DateTime.UtcNow;
        if (mapping != null)
        {
            mapping.ExternalSeriesId = externalSeriesId;
            mapping.ExternalSeriesTitle = externalTitle;
            mapping.MappingStatus = SeriesMappingStatus.UserConfirmed;
            mapping.LinkedDate = now;
            mapping.UpdateDate = now;
        }
        else
        {
            _db.SeriesMappings.Add(new SeriesMappingEntity
            {
                Id = Guid.NewGuid(),
                SeriesId = seriesId,
                Provider = provider,
                ExternalSeriesId = externalSeriesId,
                ExternalSeriesTitle = externalTitle,
                UserUid = userId,
                UserRole = UserLevel.User,
                MappingStatus = SeriesMappingStatus.UserConfirmed,
                LinkedDate = now,
                UpdateDate = now
            });
        }
        await _db.SaveChangesAsync(token);

        // Enrich the confirmed mapping with the provider's Series Metadata (cover, alt titles,
        // linked site ids) and automatch the linked providers by re-running the metadata link
        // engine, which reseeds from the just-saved mapping and propagates through LinkedSitesIds.
        await _linkEngine.LinkSeriesAsync(seriesId, token).ConfigureAwait(false);

        // Propagate the newly UserConfirmed mapping into the local contribution
        // database (contributor.db) so it participates in cloud contribution sync —
        // same as auto-match, disable and remove paths.
        await SyncContributionAsync(seriesId, token).ConfigureAwait(false);

        _logger.LogDebug("Confirmed match series {SeriesId} provider {Provider} -> {ExternalId}", seriesId, provider, externalSeriesId);
    }

    /// <summary>Get all series matching statuses (global mappings) for the user's providers.</summary>
    public async Task<List<SeriesMatchStatusDto>> GetUnmatchedSeriesAsync(Guid userId, CancellationToken token = default)
    {
        var configs = await _db.UserScrobblerConfigs
            .Where(c => c.UserId == userId && c.IsEnabled)
            .ToListAsync(token);
        List<SeriesMatchStatusDto> result = [];

        foreach (var config in configs)
        {
            var seriesList = await _db.Series
                .Include(s => s.Sources)
                .ToListAsync(token);
            foreach (var series in seriesList)
            {
                var mapping = await _db.SeriesMappings
                    .FirstOrDefaultAsync(m => m.SeriesId == series.Id && m.Provider == config.Provider, token);
                var altTitles = string.Join(", ", series.Sources?
                    .Where(s => !string.IsNullOrEmpty(s.Title) && !s.Title.Equals(series.Title, StringComparison.OrdinalIgnoreCase))
                    .Select(s => s.Title)
                    .Distinct() ?? []);
                var coverUrl = await ResolveCoverUrlAsync(series.ThumbnailUrl, token);
                result.Add(new SeriesMatchStatusDto
                {
                    SeriesId = series.Id,
                    SeriesTitle = series.Title,
                    SeriesCoverUrl = coverUrl,
                    AlternativeTitles = altTitles,
                    Provider = config.Provider,
                    MappingStatus = mapping?.MappingStatus ?? SeriesMappingStatus.Unmatched,
                    ExternalSeriesId = mapping?.ExternalSeriesId,
                    ExternalSeriesTitle = mapping?.ExternalSeriesTitle,
                    MatchScore = null
                });
            }
        }
        return result;
    }

    /// <summary>Mark a series/provider as forever-ignored (global).</summary>
    public async Task DisableSeriesLinkAsync(Guid userId, Guid seriesId, ExternalSeriesProvider provider, CancellationToken token = default)
    {
        var mapping = await _db.SeriesMappings
            .FirstOrDefaultAsync(m => m.SeriesId == seriesId && m.Provider == provider, token);
        if (mapping != null)
        {
            mapping.MappingStatus = SeriesMappingStatus.ForeverIgnored;
            mapping.UpdateDate = DateTime.UtcNow;
        }
        else
        {
            _db.SeriesMappings.Add(new SeriesMappingEntity
            {
                Id = Guid.NewGuid(),
                SeriesId = seriesId,
                Provider = provider,
                ExternalSeriesId = string.Empty,
                UserUid = userId,
                UserRole = UserLevel.User,
                MappingStatus = SeriesMappingStatus.ForeverIgnored,
                UpdateDate = DateTime.UtcNow
            });
        }
        await _db.SaveChangesAsync(token);
        await SyncContributionAsync(seriesId, token);
        _logger.LogDebug("Disabled link series {SeriesId} provider {Provider}", seriesId, provider);
    }

    /// <summary>Remove a mapping (reset to Unmatched).</summary>
    public async Task RemoveMappingAsync(Guid userId, Guid seriesId, ExternalSeriesProvider provider, CancellationToken token = default)
    {
        var mapping = await _db.SeriesMappings
            .FirstOrDefaultAsync(m => m.SeriesId == seriesId && m.Provider == provider, token);
        if (mapping != null)
        {
            _db.SeriesMappings.Remove(mapping);
            await _db.SaveChangesAsync(token);
            await SyncContributionAsync(seriesId, token);
        }
    }

    /// <summary>Get all match statuses for a user (global mappings across providers).</summary>
    public async Task<List<SeriesMatchStatusDto>> GetMatchStatusesAsync(Guid userId, CancellationToken token = default)
    {
        var allSeries = await _db.Series
            .Include(s => s.Sources)
            .ToListAsync(token);
        var allMappings = await _db.SeriesMappings.ToListAsync(token);
        List<SeriesMatchStatusDto> result = [];
        foreach (var provider in Enum.GetValues<ExternalSeriesProvider>())
        {
            foreach (var series in allSeries)
            {
                var mapping = allMappings.FirstOrDefault(m => m.SeriesId == series.Id && m.Provider == provider);
                var altTitles = string.Join(", ", series.Sources?
                    .Where(s => !string.IsNullOrEmpty(s.Title) && !s.Title.Equals(series.Title, StringComparison.OrdinalIgnoreCase))
                    .Select(s => s.Title)
                    .Distinct() ?? []);
                var externalId = mapping?.ExternalSeriesId;
                string? externalUrl = null;
                if (!string.IsNullOrEmpty(externalId))
                {
                    var prov = _providerFactory.GetProvider(provider);
                    if (prov?.SeriesUrlTemplate != null)
                    {
                        try { externalUrl = string.Format(prov.SeriesUrlTemplate, externalId); }
                        catch { }
                    }
                }
                var coverUrl = await ResolveCoverUrlAsync(series.ThumbnailUrl, token);
                result.Add(new SeriesMatchStatusDto
                {
                    SeriesId = series.Id,
                    SeriesTitle = series.Title,
                    SeriesCoverUrl = coverUrl,
                    AlternativeTitles = altTitles,
                    Provider = provider,
                    MappingStatus = mapping?.MappingStatus ?? SeriesMappingStatus.Unmatched,
                    ExternalSeriesId = externalId,
                    ExternalSeriesTitle = mapping?.ExternalSeriesTitle,
                    ExternalSeriesUrl = externalUrl,
                    MatchScore = null
                });
            }
        }
        return result;
    }

    /// <summary>Get sync status per provider (global mappings).</summary>
    public async Task<List<SyncStatusDto>> GetSyncStatusAsync(Guid userId, CancellationToken token = default)
    {
        var configs = await _db.UserScrobblerConfigs
            .Where(c => c.UserId == userId)
            .ToListAsync(token);
        List<SyncStatusDto> result = [];
        foreach (var config in configs)
        {
            var seriesMatched = await _db.SeriesMappings
                .CountAsync(m => m.Provider == config.Provider
                    && (m.MappingStatus == SeriesMappingStatus.UserConfirmed
                        || m.MappingStatus == SeriesMappingStatus.AutoMatched), token);
            var seriesUnmatched = await _db.SeriesMappings
                .CountAsync(m => m.Provider == config.Provider
                    && m.MappingStatus == SeriesMappingStatus.Unmatched, token);
            var seriesIgnored = await _db.SeriesMappings
                .CountAsync(m => m.Provider == config.Provider
                    && (m.MappingStatus == SeriesMappingStatus.ForeverIgnored
                        || m.MappingStatus == SeriesMappingStatus.TemporaryIgnored), token);
            result.Add(new SyncStatusDto
            {
                Provider = config.Provider,
                LastSyncAt = config.LastSyncAt,
                LastUploadAt = config.LastUploadAt,
                LastDownloadAt = config.LastDownloadAt,
                SeriesMatched = seriesMatched,
                SeriesUnmatched = seriesUnmatched,
                SeriesIgnored = seriesIgnored
            });
        }
        return result;
    }

    // ── Private ──

    private async Task AutoMatchForProviderAsync(Guid userId, SeriesEntity series,
        ExternalSeriesProvider provider, CancellationToken token)
    {
        await TryAutoMatchAsync(userId, series, provider, token);
    }

    private async Task<SeriesMatchStatusDto?> TryAutoMatchAsync(Guid userId, SeriesEntity series,
        ExternalSeriesProvider provider, CancellationToken token)
    {
        var scrobbler = _providerFactory.GetProvider(provider);
        if (scrobbler == null)
        {
            _logger.LogDebug("Auto-match skipped series {SeriesId}: provider {Provider} unavailable", series.Id, provider);
            return null;
        }

        await scrobbler.EnsureAuthenticatedAsync(userId, token);

        // Bug fix: if this series is already linked for this provider (any active/decision
        // status except Unmatched), do NOT re-search — the mapping is already settled.
        var preExisting = await _db.SeriesMappings
            .FirstOrDefaultAsync(m => m.SeriesId == series.Id && m.Provider == provider, token);
        if (preExisting != null && preExisting.MappingStatus != SeriesMappingStatus.Unmatched)
        {
            _logger.LogDebug("Auto-match skipped series {SeriesId} provider {Provider}: already linked (status {Status})",
                series.Id, provider, preExisting.MappingStatus);
            return null;
        }

        var localCandidates = _titleMatcher.BuildTitleCandidates(series);
        var uniqueTitles = localCandidates
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (uniqueTitles.Length == 0) return null;

        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var allCandidates = new List<(string SearchTitle, string Id)>();
        var resultLookup = new Dictionary<string, ScrobblerSearchResult>(StringComparer.OrdinalIgnoreCase);

        foreach (var title in uniqueTitles)
        {
            var searchResults = await scrobbler.SearchSeriesAsync(title, token);
            foreach (var result in searchResults)
            {
                if (string.IsNullOrWhiteSpace(result.ExternalId) || string.IsNullOrWhiteSpace(result.Title)) continue;
                if (seenIds.Add(result.ExternalId))
                {
                    allCandidates.Add((result.Title, result.ExternalId));
                    foreach (var alttitle in result.AlternateTitles)
                        allCandidates.Add((alttitle, result.ExternalId));
                    resultLookup[result.ExternalId] = result;
                }
            }
        }

        if (allCandidates.Count == 0) return null;

        var scored = TitleMatcher.MatchTitles(
            originalTitles: localCandidates,
            candidates: allCandidates,
            minimumScore: 0);
        if (scored.Length == 0) return null;

        var best = scored[0];
        var bestPercentage = best.Percentage;
        var bestExternalId = best.Id;
        var bestExternalTitle = best.SearchTitle;
        var bestResult = resultLookup.GetValueOrDefault(bestExternalId);
        // Rewrite the raw provider cover through the image cache before it is surfaced to the
        // frontend (either in the suggested-match row or after auto-match). Never persisted.
        if (bestResult != null)
            await _thumbCache.PopulateThumbsAsync(bestResult, token: token);

        const int autoMatchThreshold = 95;
        if (bestPercentage < autoMatchThreshold)
        {
            _logger.LogDebug("Auto-match suggested (score {Score}%) series {SeriesId} provider {Provider} -> {ExternalId}",
                bestPercentage, series.Id, provider, bestExternalId);
            var existingMapping = await _db.SeriesMappings
                .FirstOrDefaultAsync(m => m.SeriesId == series.Id && m.Provider == provider, token);
            if (existingMapping == null)
            {
                _db.SeriesMappings.Add(new SeriesMappingEntity
                {
                    Id = Guid.NewGuid(),
                    SeriesId = series.Id,
                    Provider = provider,
                    ExternalSeriesId = bestExternalId,
                    ExternalSeriesTitle = bestExternalTitle,
                    UserUid = userId,
                    UserRole = UserLevel.User,
                    MappingStatus = SeriesMappingStatus.Unmatched,
                    UpdateDate = DateTime.UtcNow
                });
                await _db.SaveChangesAsync(token);
            }
            return new SeriesMatchStatusDto
            {
                SeriesId = series.Id,
                SeriesTitle = series.Title,
                Provider = provider,
                MappingStatus = SeriesMappingStatus.Unmatched,
                ExternalSeriesId = bestExternalId,
                ExternalSeriesTitle = bestExternalTitle,
                ExternalCoverUrl = bestResult?.CoverUrl,
                MatchScore = bestPercentage / 100.0
            };
        }

        var existing = await _db.SeriesMappings
            .FirstOrDefaultAsync(m => m.SeriesId == series.Id && m.Provider == provider, token);
        var now = DateTime.UtcNow;
        if (existing != null)
        {
            // Never downgrade a settled decision: user-confirmed / blocked matches must keep
            // their status — auto-match only fills/updates the external id/title columns.
            if (existing.MappingStatus == SeriesMappingStatus.UserConfirmed ||
                existing.MappingStatus == SeriesMappingStatus.Blocked ||
                existing.MappingStatus == SeriesMappingStatus.ForeverIgnored ||
                existing.MappingStatus == SeriesMappingStatus.TemporaryIgnored)
            {
                _logger.LogDebug("Auto-match kept existing status {Status} for series {SeriesId} provider {Provider}",
                    existing.MappingStatus, series.Id, provider);
                await _seriesStateService.SyncToRensaioJsonAsync(series.Id, token).ConfigureAwait(false);
                return new SeriesMatchStatusDto
                {
                    SeriesId = series.Id,
                    SeriesTitle = series.Title,
                    Provider = provider,
                    MappingStatus = existing.MappingStatus,
                    ExternalSeriesId = existing.ExternalSeriesId,
                    ExternalSeriesTitle = existing.ExternalSeriesTitle,
                    MatchScore = bestPercentage / 100.0
                };
            }

            existing.ExternalSeriesId = bestExternalId;
            existing.ExternalSeriesTitle = bestExternalTitle;
            existing.MappingStatus = SeriesMappingStatus.AutoMatched;
            existing.LinkedDate = now;
            existing.UpdateDate = now;
        }
        else
        {
            _db.SeriesMappings.Add(new SeriesMappingEntity
            {
                Id = Guid.NewGuid(),
                SeriesId = series.Id,
                Provider = provider,
                ExternalSeriesId = bestExternalId,
                ExternalSeriesTitle = bestExternalTitle,
                UserUid = userId,
                UserRole = UserLevel.User,
                MappingStatus = SeriesMappingStatus.AutoMatched,
                LinkedDate = now,
                UpdateDate = now
            });
        }
        await _db.SaveChangesAsync(token);

        // Upsert global metadata via the link engine cache fields from the search result.
        await UpsertSeriesMappingAsync(userId, series.Id, provider, bestExternalId, bestExternalTitle, UserLevel.User, token);
        _logger.LogDebug("Auto-matched series {SeriesId} provider {Provider} -> {ExternalId} (score {Score}%)",
            series.Id, provider, bestExternalId, bestPercentage);

        return new SeriesMatchStatusDto
        {
            SeriesId = series.Id,
            SeriesTitle = series.Title,
            Provider = provider,
            MappingStatus = SeriesMappingStatus.AutoMatched,
            ExternalSeriesId = bestExternalId,
            ExternalSeriesTitle = bestExternalTitle,
            ExternalCoverUrl = bestResult?.CoverUrl,
            MatchScore = bestPercentage / 100.0
        };
    }

    private async Task UpsertSeriesMappingAsync(Guid userId, Guid seriesId, ExternalSeriesProvider provider,
        string externalSeriesId, string? externalTitle, UserLevel userLevel, CancellationToken token)
    {
        var existing = await _db.SeriesMappings
            .FirstOrDefaultAsync(m => m.SeriesId == seriesId && m.Provider == provider, token);
        if (existing != null)
        {
            if (userLevel >= existing.UserRole)
            {
                existing.ExternalSeriesId = externalSeriesId;
                existing.ExternalSeriesTitle = externalTitle;
                existing.UserUid = userId;
                existing.UserRole = userLevel;
                existing.UpdateDate = DateTime.UtcNow;
            }
        }
        else
        {
            _db.SeriesMappings.Add(new SeriesMappingEntity
            {
                Id = Guid.NewGuid(),
                SeriesId = seriesId,
                Provider = provider,
                ExternalSeriesId = externalSeriesId,
                ExternalSeriesTitle = externalTitle,
                UserUid = userId,
                UserRole = userLevel,
                UpdateDate = DateTime.UtcNow
            });
        }
        await _db.SaveChangesAsync(token);
        await _seriesStateService.SyncToRensaioJsonAsync(seriesId, token).ConfigureAwait(false);
        await SyncContributionAsync(seriesId, token);
    }
}