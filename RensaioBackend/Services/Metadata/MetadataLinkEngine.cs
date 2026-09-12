using RensaioBackend.Data;
using RensaioBackend.Models;
using RensaioBackend.Models.Database;
using RensaioBackend.Models.Dto;
using RensaioBackend.Models.Enums;
using RensaioBackend.Services.Jobs;
using RensaioBackend.Services.Series;
using RensaioBackend.Services.Scrobbling;
using RensaioBackend.Services.Scrobbling.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace RensaioBackend.Services.Metadata;

/// <summary>
/// Enum describing the resulting status of a provider link from the metadata engine.
/// </summary>
public enum SeriesLinkStatus
{
    Linked,
    Suggested,
    Failed
}

/// <summary>
/// A single provider link produced by the cross-provider metadata engine.
/// </summary>
public class MetaDataLinkEntry
{
    public ExternalSeriesProvider Provider { get; set; }
    public string ExternalSeriesId { get; set; } = string.Empty;
    public string? Title { get; set; }
    public double Confidence { get; set; }
    public SeriesLinkStatus Status { get; set; }
    public List<string> LinkedSitesIds { get; set; } = [];
    public List<string> AlternativeTitles { get; set; } = [];

    /// <summary>Cover image URL from the provider detail call (persisted to SeriesMappingEntity.SeriesCoverUrl).</summary>
    public string? CoverUrl { get; set; }

    /// <summary>Provider-native JSON payload from the detail call (persisted to SeriesMappingEntity.MetaData).</summary>
    public string? MetaData { get; set; }
}

/// <summary>
/// Result of running the metadata link engine for one series.
/// </summary>
public class MetadataLinkResult
{
    public Guid SeriesId { get; set; }
    public string? SeriesTitle { get; set; }
    public List<MetaDataLinkEntry> Links { get; set; } = [];
    public List<MetaDataLinkEntry> Suggestions { get; set; } = [];
    public int ProvidersSearched { get; set; }
    public long ElapsedMs { get; set; }
}

/// <summary>
/// Cross-provider series link engine (Rensaio-DB persistence adapter).
///
/// Given a local series (title + existing sources), delegates the search/score/propagation
/// algorithm to <see cref="MetadataMatchCore"/> (shared with the Contribution Mappings flow) and
/// persists the linked results into global SeriesMappings (respecting role-based overwrite rules),
/// materializes Unmatched rows, syncs rensaio.json and propagates into the contribution database.
/// </summary>
public class MetadataLinkEngine
{
    private const string LinkAllJobId = "metadata-link-all";

    private readonly AppDbContext _db;
    private readonly ExternalSeriesProviderFactory _factory;
    private readonly MetadataMatchCore _matchCore;
    private readonly ExternalMappingSupport _support;
    private readonly TitleMatcher _titleMatcher;
    private readonly SeriesStateService _seriesStateService;
    private readonly JobHubReportService _hubReport;
    private readonly Contributions.ContributionPropagationService _contributionSync;
    private readonly ILogger<MetadataLinkEngine> _logger;

    public MetadataLinkEngine(
        AppDbContext db,
        ExternalSeriesProviderFactory factory,
        MetadataMatchCore matchCore,
        ExternalMappingSupport support,
        TitleMatcher titleMatcher,
        SeriesStateService seriesStateService,
        JobHubReportService hubReport,
        Contributions.ContributionPropagationService contributionSync,
        ILogger<MetadataLinkEngine> logger)
    {
        _db = db;
        _factory = factory;
        _matchCore = matchCore;
        _support = support;
        _titleMatcher = titleMatcher;
        _seriesStateService = seriesStateService;
        _hubReport = hubReport;
        _contributionSync = contributionSync;
        _logger = logger;
    }

    /// <summary>
    /// Links ALL local series across all metadata providers, publishing live progress on
    /// ProgressHub (jobType = MetadataLink). Returns the summary when done.
    /// </summary>
    public async Task<MetaDataLinkAllResultDto> LinkAllAsync(CancellationToken token = default)
    {
        var summary = new MetaDataLinkAllResultDto();
        var seriesIds = await _db.Series.Select(s => s.Id).ToListAsync(token);
        var total = seriesIds.Count;

        // Only series with pending work are scanned: at least one enabled provider row is
        // Unmatched, Blocked-with-id (a DIFFERENT id may still be matched), or carries an expired
        // TemporaryIgnored. Settled series (active links, forever-ignored, hard-blocked empty
        // rows, non-expired temporary-ignores) are skipped entirely.
        var allMappings = await _db.SeriesMappings
            .Where(m => m.SeriesId != null)
            .ToListAsync(token);
        var enabledProviders = await GetEnabledProviderSetAsync(token).ConfigureAwait(false);
        var threshold = DateTime.UtcNow.AddMonths(-1);

        if (_hubReport != null)
        {
            await _hubReport.ReportProgressAsync(new ProgressState
            {
                Id = LinkAllJobId,
                JobType = JobType.MetadataLink,
                ProgressStatus = ProgressStatus.Started,
                Percentage = 0,
                Message = $"Linking 0/{total} series"
            });
        }

        foreach (var id in seriesIds)
        {
            if (token.IsCancellationRequested) break;
            if (!SeriesNeedsAttention(id, allMappings, enabledProviders, threshold))
            {
                summary.SkippedSeries++;
                continue;
            }
            var result = await LinkSeriesAsync(id, token, enabledProviders).ConfigureAwait(false);
            summary.ProcessedSeries++;
            summary.LinkedProviderEntries += result.Links.Count;
            summary.SuggestedEntries += result.Suggestions.Count;

            if (_hubReport != null)
            {
                var pct = total > 0 ? (summary.ProcessedSeries * 100 / total) : 100;
                await _hubReport.ReportProgressAsync(new ProgressState
                {
                    Id = LinkAllJobId,
                    JobType = JobType.MetadataLink,
                    ProgressStatus = ProgressStatus.InProgress,
                    Percentage = pct,
                    Message = $"Linking {summary.ProcessedSeries}/{total} series · {summary.LinkedProviderEntries} linked · {summary.SkippedSeries} skipped"
                });
            }
        }

        if (_hubReport != null)
        {
            await _hubReport.ReportProgressAsync(new ProgressState
            {
                Id = LinkAllJobId,
                JobType = JobType.MetadataLink,
                ProgressStatus = ProgressStatus.Completed,
                Percentage = 100,
                Message = $"Linked {summary.LinkedProviderEntries} entries across {summary.ProcessedSeries} series"
            });
        }

        return summary;
    }

    /// <summary>
    /// Links a single local series across all metadata providers and persists the result.
    /// </summary>
    public async Task<MetadataLinkResult> LinkSeriesAsync(Guid seriesId, CancellationToken token = default,
        HashSet<ExternalSeriesProvider>? enabledProviders = null)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var series = await _db.Series
            .Include(s => s.Sources)
            .FirstOrDefaultAsync(s => s.Id == seriesId, token);
        if (series == null)
            return new MetadataLinkResult { SeriesId = seriesId };

        var result = new MetadataLinkResult
        {
            SeriesId = seriesId,
            SeriesTitle = series.Title
        };

        // Per-series progress events (id = "metadata-link-{seriesId}") so the External Mappings
        // page can show a live spinner + completion for the row being scanned. These are distinct
        // from the global scan's aggregate progress (id = LinkAllJobId).
        if (_hubReport != null)
        {
            await _hubReport.ReportProgressAsync(new ProgressState
            {
                Id = $"metadata-link-{seriesId}",
                JobType = JobType.MetadataLink,
                ProgressStatus = ProgressStatus.Started,
                Percentage = 0,
                Message = $"Scanning '{series.Title}'…"
            }).ConfigureAwait(false);
        }

        // 1. Load the existing status-aware mappings and derive block/skip rules.
        //  - Blocked rows: id-level RULE — that provider must never link to that exact id, but may
        //    automatch a DIFFERENT id later. Record the blocked ids; do not seed them as nodes/edges.
        //  - ForeverIgnored: provider-level skip — don't seed, don't search, don't re-link.
        //  - TemporaryIgnored: provider-level skip UNLESS expired (LinkedDate + 1 month <= now):
        //    the ignore is cleared to Unmatched so the provider is searched again this pass.
        //  - AutoMatched / UserConfirmed / Unmatched-with-id: seed normally as the link graph roots.
        var existingMappings = await _db.SeriesMappings
            .Where(m => m.SeriesId == seriesId)
            .ToListAsync(token);

        var blockedIds = new Dictionary<ExternalSeriesProvider, HashSet<string>>();
        var skippedProviders = new HashSet<ExternalSeriesProvider>();
        var seeds = new List<MetadataLinkSeed>();
        // Expired TemporaryIgnored rows cleared in-memory this pass (persisted with the run).
        var reEvaluatedIgnores = new List<SeriesMappingEntity>();
        var now = DateTime.UtcNow;
        var ignoreThreshold = now.AddMonths(-1); // LinkedDate + 1 month <= now  <=>  LinkedDate <= now - 1 month

        foreach (var mapping in existingMappings)
        {
            if (mapping.MappingStatus == SeriesMappingStatus.Blocked)
            {
                if (!string.IsNullOrWhiteSpace(mapping.ExternalSeriesId))
                {
                    if (!blockedIds.TryGetValue(mapping.Provider, out var set))
                    {
                        set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        blockedIds[mapping.Provider] = set;
                    }
                    set.Add(mapping.ExternalSeriesId);
                }
                continue;
            }

            if (mapping.MappingStatus == SeriesMappingStatus.ForeverIgnored)
            {
                skippedProviders.Add(mapping.Provider);
                continue;
            }

            if (mapping.MappingStatus == SeriesMappingStatus.TemporaryIgnored)
            {
                if (mapping.LinkedDate != null && mapping.LinkedDate <= ignoreThreshold)
                {
                    // The ignore has expired → re-evaluate the provider NOW: clear the ignore so
                    // it is searched again and the stale row can be re-linked / relabeled below.
                    mapping.MappingStatus = SeriesMappingStatus.Unmatched;
                    mapping.ExternalSeriesId = string.Empty;
                    mapping.LinkedDate = null;
                    mapping.UpdateDate = now;
                    reEvaluatedIgnores.Add(mapping);
                    continue;
                }
                skippedProviders.Add(mapping.Provider);
                continue;
            }

            if (string.IsNullOrWhiteSpace(mapping.ExternalSeriesId)) continue;
            seeds.Add(new MetadataLinkSeed
            {
                Provider = mapping.Provider,
                ExternalSeriesId = mapping.ExternalSeriesId,
                ExternalSeriesTitle = mapping.ExternalSeriesTitle,
                LinkedSitesIds = mapping.LinkedSitesIds,
                Confidence = 1.0
            });
        }

        // 2. Determine the metadata providers to query (order matters: hub first) and authenticate.
        var providers = _support.GetMetadataProvidersInOrder();
        if (enabledProviders != null)
            providers = providers.Where(p => enabledProviders.Contains(p.ProviderType)).ToList();
        await _support.EnsureProvidersAuthenticatedAsync(providers, token).ConfigureAwait(false);

        // 3. Run the shared matcher (search + second pass + cross-site propagation).
        var localCandidates = _titleMatcher.BuildTitleCandidates(series);
        var match = await _matchCore.MatchAsync(localCandidates, providers, seeds, blockedIds,
            skippedProviders, enabledProviders, token).ConfigureAwait(false);
        result.ProvidersSearched = match.ProvidersSearched;
        result.Suggestions = match.Suggestions;

        foreach (var node in match.Links)
        {
            if (enabledProviders != null && !enabledProviders.Contains(node.Provider))
                continue; // not enabled → don't enrich/fetch metadata for this provider
            await UpsertMappingAsync(seriesId, node, match.FetchedIds, token);
            result.Links.Add(node);
        }

        // 5.5 Materialize Unmatched rows: for every enabled metadata provider that ended up with no
        // active link (and no Blocked/TemporaryIgnored/ForeverIgnored decision), persist an empty
        // SeriesMappings row (status = Unmatched, external id = ""). This makes a manual mapping
        // possible in the External Mappings page even when the automatch found nothing.
        var nodes = match.Links.ToDictionary(l => l.Provider, l => l);
        await MaterializeUnmatchedAsync(seriesId, existingMappings, providers, nodes, token).ConfigureAwait(false);

        // 5.6 Persist expired TemporaryIgnored clears (relevant when no match was found and no
        // upsert ran in this pass — the row must not stay Ignored past its review date).
        if (reEvaluatedIgnores.Count > 0)
        {
            try { await _db.SaveChangesAsync(token).ConfigureAwait(false); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to persist re-evaluated TemporaryIgnored mappings for series {SeriesId}", seriesId);
            }
        }

        // 6. Sync rensaio.json via the existing service (best effort).
        await SyncRensaioJsonAsync(seriesId, token);

        // 7. Propagate the mapping state into the contribution database (best effort).
        //    No-op unless the user enabled contributions and never throws into the caller.
        try
        {
            await _contributionSync.SyncSeriesAsync(seriesId, token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to propagate contribution state for series {SeriesId}", seriesId);
        }

        sw.Stop();
        result.ElapsedMs = sw.ElapsedMilliseconds;

        if (_hubReport != null)
        {
            await _hubReport.ReportProgressAsync(new ProgressState
            {
                Id = $"metadata-link-{seriesId}",
                JobType = JobType.MetadataLink,
                ProgressStatus = ProgressStatus.Completed,
                Percentage = 100,
                Message = $"{series.Title}: {result.Links.Count} linked, {result.Suggestions.Count} suggested, {result.ProvidersSearched} providers searched"
            }).ConfigureAwait(false);
        }

        return result;
    }

    private async Task UpsertMappingAsync(Guid seriesId, MetaDataLinkEntry node,
        HashSet<(ExternalSeriesProvider Site, string Id)> fetchedIds, CancellationToken token)
    {
        try
        {
            var existing = await _db.SeriesMappings
                .FirstOrDefaultAsync(m => m.SeriesId == seriesId && m.Provider == node.Provider, token);

            if (existing != null)
            {
                // Never overwrite a user decision: Blocked / ForeverIgnored / TemporaryIgnored are
                // kept as-is (their ids are already excluded from search + propagation; an expired
                // TemporaryIgnored was cleared to Unmatched upstream and never arrives here).
                if (existing.MappingStatus == SeriesMappingStatus.ForeverIgnored ||
                    existing.MappingStatus == SeriesMappingStatus.TemporaryIgnored)
                    return;

                // Blocked is an id-level rule only: reaching this point means the node carries a
                // DIFFERENT id than the blocked one (search + propagation filter blockedIds), so
                // linking the new id is allowed and always takes priority over the stale block.
                // Only enrich metadata columns on blocked rows / when no external id, or role rules
                // permit overwrite (System-level).
                if (existing.MappingStatus == SeriesMappingStatus.Blocked ||
                    string.IsNullOrWhiteSpace(existing.ExternalSeriesId) ||
                    existing.UserRole <= UserLevel.User)
                {
                    existing.ExternalSeriesId = node.ExternalSeriesId;
                    existing.ExternalSeriesTitle = node.Title;
                    // Persist cover + native metadata captured during the shared matcher's detail
                    // expansion / propagation. Only fills cover when empty; MetaData always refreshed.
                    if (string.IsNullOrWhiteSpace(existing.SeriesCoverUrl) && !string.IsNullOrWhiteSpace(node.CoverUrl))
                        existing.SeriesCoverUrl = node.CoverUrl;
                    if (!string.IsNullOrWhiteSpace(node.MetaData))
                        existing.MetaData = node.MetaData;
                    existing.LinkedSitesIds = MergeLinked(existing.LinkedSitesIds, node.LinkedSitesIds);
                    existing.AlternativeTitles = MergeTitles(existing.AlternativeTitles, node.AlternativeTitles);
                    // Preserve a settled user decision — the link engine never downgrades a
                    // UserConfirmed mapping back to AutoMatched.
                    if (existing.MappingStatus != SeriesMappingStatus.UserConfirmed)
                        existing.MappingStatus = SeriesMappingStatus.AutoMatched;
                    existing.LinkedDate = DateTime.UtcNow;
                    existing.UpdateDate = DateTime.UtcNow;
                }
            }
            else
            {
                _db.SeriesMappings.Add(new SeriesMappingEntity
                {
                    Id = Guid.NewGuid(),
                    SeriesId = seriesId,
                    Provider = node.Provider,
                    ExternalSeriesId = node.ExternalSeriesId,
                    ExternalSeriesTitle = node.Title,
                    SeriesCoverUrl = node.CoverUrl,
                    MetaData = node.MetaData,
                    LinkedSitesIds = node.LinkedSitesIds,
                    AlternativeTitles = node.AlternativeTitles,
                    UserUid = null,
                    UserRole = UserLevel.User,
                    MappingStatus = SeriesMappingStatus.AutoMatched,
                    LinkedDate = DateTime.UtcNow,
                    UpdateDate = DateTime.UtcNow
                });
            }

            await _db.SaveChangesAsync(token);

            // Enrich the mapping with provider metadata already captured on the node during
            // expansion / propagation (cover + native JSON + linked sites + titles).
            // Only falls back to a fresh provider call when the node has no metadata at all.
            try
            {
                // `existing` is null on the insert path — reload the persisted row so we can enrich it.
                var saved = existing ?? await _db.SeriesMappings
                    .FirstOrDefaultAsync(m => m.SeriesId == seriesId && m.Provider == node.Provider, token)
                    .ConfigureAwait(false);
                if (saved == null) return;

                if (string.IsNullOrWhiteSpace(saved.SeriesCoverUrl) && !string.IsNullOrWhiteSpace(node.CoverUrl))
                    saved.SeriesCoverUrl = node.CoverUrl;
                if (!string.IsNullOrWhiteSpace(node.MetaData))
                    saved.MetaData = node.MetaData;
                if (node.LinkedSitesIds.Count > 0)
                    saved.LinkedSitesIds = MergeLinked(saved.LinkedSitesIds, node.LinkedSitesIds);
                if (node.AlternativeTitles.Count > 0)
                    saved.AlternativeTitles = MergeTitles(saved.AlternativeTitles, node.AlternativeTitles);

                // Fallback: if the node somehow lacks the metadata (e.g. search-only path), fetch
                // once and persist — dedup via fetchedIds applies to the provider call only.
                if (string.IsNullOrWhiteSpace(node.CoverUrl) && string.IsNullOrWhiteSpace(node.MetaData)
                    && fetchedIds.Add((node.Provider, saved.ExternalSeriesId)))
                {
                    var provider = _factory.GetProvider(node.Provider);
                    var metadata = provider != null
                        ? await provider.FetchSeriesMetadataAsync(saved.ExternalSeriesId, token).ConfigureAwait(false)
                        : null;
                    if (metadata != null)
                    {
                        if (string.IsNullOrWhiteSpace(saved.SeriesCoverUrl) && !string.IsNullOrWhiteSpace(metadata.CoverUrl))
                            saved.SeriesCoverUrl = metadata.CoverUrl;
                        if (!string.IsNullOrWhiteSpace(metadata.MetaData))
                            saved.MetaData = metadata.MetaData;
                    }
                }

                await _db.SaveChangesAsync(token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to enrich SeriesMapping metadata for series {SeriesId} provider {Provider}", seriesId, node.Provider);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to upsert SeriesMapping for series {SeriesId} provider {Provider}", seriesId, node.Provider);
        }
    }

    /// <summary>
    /// Creates an empty (Unmatched) <see cref="SeriesMappingEntity"/> row for each enabled metadata
    /// provider that ended this pass without a linked node AND without an existing row that already
    /// records a decision (Blocked / TemporaryIgnored / ForeverIgnored / active id row / Unmatched).
    /// This makes every provider visible and manually linkable in the External Mappings page even when
    /// automatching found nothing, while never overriding user decisions.
    /// </summary>
    private async Task MaterializeUnmatchedAsync(Guid seriesId, List<SeriesMappingEntity> existingMappings,
        List<IExternalSeriesProvider> providers, Dictionary<ExternalSeriesProvider, MetaDataLinkEntry> nodes,
        CancellationToken token)
    {
        var now = DateTime.UtcNow;
        var added = 0;

        foreach (var provider in providers)
        {
            var providerType = provider.ProviderType;

            // Had an automatch / propagation already linked it? Nothing to do.
            if (nodes.ContainsKey(providerType)) continue;

            // Existing row for this (series, provider) already records a state (decision ids, blocked,
            // temporary/forever ignored, or even an Unmatched decision) — leave it untouched.
            var existing = existingMappings.FirstOrDefault(m => m.SeriesId == seriesId && m.Provider == providerType);
            if (existing != null) continue;

            // No row at all → materialize an Unmatched row so a manual mapping is possible.
            _db.SeriesMappings.Add(new SeriesMappingEntity
            {
                Id = Guid.NewGuid(),
                SeriesId = seriesId,
                Provider = providerType,
                ExternalSeriesId = string.Empty,
                ExternalSeriesTitle = null,
                SeriesCoverUrl = null,
                MetaData = null,
                LinkedSitesIds = [],
                AlternativeTitles = [],
                UserUid = null,
                UserRole = UserLevel.User,
                MappingStatus = SeriesMappingStatus.Unmatched,
                LinkedDate = null,
                UpdateDate = now
            });
            added++;
        }

        if (added > 0)
        {
            try
            {
                await _db.SaveChangesAsync(token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to materialize Unmatched mappings for series {SeriesId}", seriesId);
            }
        }
    }

    private static List<string> MergeLinked(List<string> existing, List<string> newLinked)
    {
        var set = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);
        foreach (var l in newLinked) set.Add(l);
        return set.ToList();
    }

    private static List<string> MergeTitles(List<string> existing, List<string> newTitles)
    {
        var set = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);
        foreach (var t in newTitles) set.Add(t);
        return set.ToList();
    }

    /// <summary>
    /// Providers that unattended scanning may query: public providers (no auth) plus OAuth-gated
    /// providers that have at least one enabled config carrying an access token. The background
    /// worker has no request user, so "enabled" = any authenticated config; the Global Scan button
    /// reuses the same rule.
    /// </summary>
    public async Task<HashSet<ExternalSeriesProvider>> GetEnabledProviderSetAsync(CancellationToken token = default)
        => await _support.GetEnabledProviderSetAsync(token).ConfigureAwait(false);

    /// <summary>
    /// True when the series has at least one ENABLED provider that needs (re-)evaluation:
    /// no mapping row, an <see cref="SeriesMappingStatus.Unmatched"/> row, a
    /// <see cref="SeriesMappingStatus.Blocked"/> row carrying an id (a DIFFERENT id may still be
    /// matched — the block is id-scoped, not provider-scoped), or an expired
    /// <see cref="SeriesMappingStatus.TemporaryIgnored"/> (LinkedDate + 1 month <= threshold).
    /// Settled states return false: active link with id, ForeverIgnored, hard-blocked (empty-id)
    /// row, and non-expired TemporaryIgnored.
    /// </summary>
    public bool SeriesNeedsAttention(Guid seriesId, List<SeriesMappingEntity> allMappings,
        HashSet<ExternalSeriesProvider> enabledProviders, DateTime threshold)
    {
        if (enabledProviders.Count == 0) return false;

        var seriesMappings = allMappings.Where(m => m.SeriesId == seriesId).ToList();
        if (seriesMappings.Count == 0) return true; // no rows → every enabled provider is unmatched

        var covered = 0;
        foreach (var m in seriesMappings)
        {
            if (!enabledProviders.Contains(m.Provider)) continue; // provider disabled → not our concern
            covered++;
            switch (m.MappingStatus)
            {
                case SeriesMappingStatus.Unmatched:
                    return true;
                case SeriesMappingStatus.AutoMatched:
                case SeriesMappingStatus.UserConfirmed:
                    // Settled only when it actually carries an external id; stale empty-id rows
                    // (e.g. old blocked/ignored cleanup) must be re-scanned and refilled.
                    if (string.IsNullOrWhiteSpace(m.ExternalSeriesId)) return true;
                    break;
                case SeriesMappingStatus.Blocked:
                    // Empty block = hard block (provider completely off); an id block only rules
                    // out that exact id → the provider may still accept a DIFFERENT id.
                    if (!string.IsNullOrWhiteSpace(m.ExternalSeriesId)) return true;
                    break;
                case SeriesMappingStatus.ForeverIgnored:
                    break; // settled
                case SeriesMappingStatus.TemporaryIgnored:
                    if (m.LinkedDate != null && m.LinkedDate <= threshold) return true; // expired → re-evaluate
                    break; // settled until review date
            }
        }

        // An enabled provider with no row for this series is unmatched work.
        return covered < enabledProviders.Count;
    }

    /// <summary>
    /// Ordered metadata providers the engine queries (hub first). Delegates to the shared support
    /// helper so the External Mappings page and the Contribution Mappings page render the same set.
    /// </summary>
    public List<IExternalSeriesProvider> GetMetadataProvidersInOrder()
        => _support.GetMetadataProvidersInOrder();

    private async Task SyncRensaioJsonAsync(Guid seriesId, CancellationToken token)
    {
        try
        {
            await _seriesStateService.SyncToRensaioJsonAsync(seriesId, token);
        }
        catch
        {
            // best-effort
        }
    }
}