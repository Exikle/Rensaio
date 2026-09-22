using RensaioBackend.Services.Contributions;
using RensaioBackend.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace RensaioBackend.Services.Background
{
    /// <summary>
    /// Background service that performs the daily (and startup) contribution import:
    /// fetch `metadata.bin` from GitHub → decrypt/decompress/decode → apply to the local
    /// Contribution DB (source-of-truth semantics: delete uploaded, preserve pending,
    /// overwrite clean, add new, stamp clean, VACUUM) → propagate Auto/User matches into
    /// rensaio.db SeriesMappings.
    ///
    /// Runs for ALL users (no Contribution settings required — the export is the public
    /// read-only community dataset; only UPLOADING the user's own changes is opt-in via settings):
    ///   - once shortly after startup
    ///   - daily at 06:00 UTC (mirrors the worker's cron trigger that produces metadata.bin)
    ///
    /// Holds the shared <see cref="ContributionDbGate"/> exclusive lock for the whole
    /// copy+apply so no upload/read interleaves mid-import.
    /// </summary>
    public sealed class ContributionImportBackgroundService : IWorkerService
    {
        private static readonly TimeSpan DailyInterval = TimeSpan.FromHours(24);
        private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(20);

        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<ContributionImportBackgroundService> _logger;

        public ContributionImportBackgroundService(
            IServiceScopeFactory scopeFactory,
            ILogger<ContributionImportBackgroundService> logger)
        {
            _scopeFactory = scopeFactory;
            _logger = logger;
        }

        public async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Contribution import background service started.");

            try
            {
                // Startup import — universal: consumes the public metadata.bin export even when
                // the user hasn't enabled contributions (only uploads opt in via settings).
                await RunImportAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Contribution import at startup failed");
            }

            // Daily loop — 06:00 UTC by aligning the first tick.
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var now = DateTime.UtcNow;
                    var next = now.Date.AddHours(6); // 06:00 UTC today
                    if (next <= now) next = next.AddDays(1);

                    var delay = next - now;
                    _logger.LogInformation("Next contribution import scheduled at {Next:O}", next);
                    await Task.Delay(delay, stoppingToken).ConfigureAwait(false);

                    await RunImportAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Scheduled contribution import failed");
                    // Retry tomorrow (fallback timer so a bad day doesn't kill the loop).
                    try { await Task.Delay(DailyInterval, stoppingToken).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                }
            }
        }

        private async Task RunImportAsync(CancellationToken token)
        {
            using var scope = _scopeFactory.CreateScope();
            var import = scope.ServiceProvider.GetRequiredService<ContributionImportService>();
            var toRensaio = scope.ServiceProvider.GetRequiredService<ContributionToRensaioSyncService>();
            var gate = scope.ServiceProvider.GetRequiredService<ContributionDbGate>();

            using (await gate.LockAsync(token).ConfigureAwait(false))
            {
                _logger.LogInformation("Contribution import starting…");
                var result = await import.ImportAsync(token).ConfigureAwait(false);
                if (!result.Success)
                {
                    _logger.LogWarning("Contribution import failed: {Error}", result.Error);
                    return;
                }

                if (result.WasSkipped)
                {
                    _logger.LogInformation("Contribution import skipped: metadata.bin unchanged.");
                    return;
                }

                _logger.LogInformation(
                    "Contribution import applied: {Titles} titles, {Sources} sources, {Series} series, " +
                    "{Metadata} metadata, {Deleted} uploaded rows swept, server version {Version}",
                    result.Titles, result.Sources, result.Series, result.Metadata,
                    result.DeletedUploaded, result.ServerVersion);

                // Step 3 — propagate Auto/User into rensaio.db SeriesMappings.
                await toRensaio.SyncAsync(token).ConfigureAwait(false);
            }
        }
    }
}