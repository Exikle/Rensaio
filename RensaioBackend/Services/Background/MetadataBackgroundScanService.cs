using RensaioBackend.Data;
using RensaioBackend.Models.Database;
using RensaioBackend.Models.Enums;
using RensaioBackend.Services.Contributions.Abstractions;
using RensaioBackend.Services.Metadata;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace RensaioBackend.Services.Background;

/// <summary>
/// Background service that reconciles metadata mappings. On each run it links every local series
/// (and, when the in-memory repository has titles, those titles too) that needs attention, using
/// the global <see cref="SeriesMappingEntity.MappingStatus"/> / <see cref="SeriesMappingEntity.LinkedDate"/>:
///   - No global mapping, or one with a stale/empty external id -> link.
///   - Unmatched / Blocked-with-id (a DIFFERENT id may still be matched) -> link.
///   - TemporaryIgnored with LinkedDate + 1 month <= now -> re-link and clear.
///   - ForeverIgnored / hard-blocked (empty-id Blocked) / settled -> skipped (never auto-linked).
/// </summary>
public sealed class MetadataBackgroundScanService : IWorkerService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeSpan _interval;
    private readonly ILogger<MetadataBackgroundScanService> _logger;

    public MetadataBackgroundScanService(IServiceScopeFactory scopeFactory,
        ILogger<MetadataBackgroundScanService> logger)
        : this(scopeFactory, logger, TimeSpan.FromHours(24))
    {
    }

    public MetadataBackgroundScanService(IServiceScopeFactory scopeFactory,
        ILogger<MetadataBackgroundScanService> logger, TimeSpan interval)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _interval = interval;
    }

    public async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Metadata background scan started (interval {Interval})", _interval);
        try
        {
            using var timer = new PeriodicTimer(_interval);
            while (true)
            {
                try { await Task.Delay(_interval, stoppingToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }

                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var engine = scope.ServiceProvider.GetRequiredService<MetadataLinkEngine>();
                var repo = scope.ServiceProvider.GetRequiredService<IGlobalMetadataRepository>();
                await RunOnceAsync(db, engine, repo, stoppingToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _logger.LogInformation("Metadata background scan stopped");
        }
    }

    /// <summary>
    /// Kicks off a full background scan asynchronously in its own service scope. The engine's
    /// <see cref="MetadataLinkEngine.LinkAllAsync"/> publishes SignalR progress (jobType =
    /// MetadataLink). Returns immediately; the scan runs to completion in the background.
    /// </summary>
    public Task TriggerAsync(CancellationToken token = default)
    {
        return Task.Run(async () =>
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var engine = scope.ServiceProvider.GetRequiredService<MetadataLinkEngine>();
                await engine.LinkAllAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Cancelled during shutdown — expected.
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Background metadata scan failed");
            }
        });
    }

    /// <summary>Runs a single scan pass (usable for manual "scan now" too).</summary>
    public async Task RunOnceAsync(AppDbContext db, MetadataLinkEngine engine,
        IGlobalMetadataRepository repo, CancellationToken token = default)
    {
        var seriesIds = await db.Series.Select(s => s.Id).ToListAsync(token).ConfigureAwait(false);
        var mappings = await db.SeriesMappings.ToListAsync(token).ConfigureAwait(false);

        // Only scan providers that need no auth + providers that are authenticated for at least one
        // user. The background worker has no request user, so "enabled" = any authenticated config.
        var enabledProviders = await engine.GetEnabledProviderSetAsync(token).ConfigureAwait(false);
        var threshold = DateTime.UtcNow.AddMonths(-1); // LinkedDate + 1 month <= now  <=>  LinkedDate <= now - 1 month

        var linked = 0;
        var skipped = 0;

        foreach (var seriesId in seriesIds)
        {
            if (token.IsCancellationRequested) break;

            // Only series with pending work are scanned: at least one enabled provider row is
            // Unmatched, Blocked-with-id (a DIFFERENT id may still be matched), or carries an
            // expired TemporaryIgnored (which the engine clears + re-evaluates). Settled series
            // are skipped entirely.
            if (!engine.SeriesNeedsAttention(seriesId, mappings, enabledProviders, threshold))
            {
                skipped++;
                continue;
            }

            try
            {
                var result = await engine.LinkSeriesAsync(seriesId, token, enabledProviders).ConfigureAwait(false);
                linked += result.Links.Count;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Metadata scan failed for series {SeriesId}", seriesId);
            }
        }

        var titles = repo.GetAllTitlesAsync(token);
        _logger.LogInformation("Metadata scan pass complete: {Linked} linked, {Skipped} skipped, {Titles} titles in repo",
            linked, skipped, titles.Count);
    }
}