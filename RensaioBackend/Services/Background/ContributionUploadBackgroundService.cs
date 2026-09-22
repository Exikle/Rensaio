using RensaioBackend.Services.Contributions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace RensaioBackend.Services.Background;

/// <summary>
/// Background worker that drains the contribution upload queue. Every enqueued
/// request triggers a ContributionSnapshotV1 upload of the local contribution
/// database to the cloud worker, then marks uploaded rows Version -2.
/// </summary>
public sealed class ContributionUploadBackgroundService : IWorkerService
{
    private readonly IContributionUploadQueue _queue;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ContributionUploadBackgroundService> _logger;

    public ContributionUploadBackgroundService(
        IContributionUploadQueue queue,
        IServiceScopeFactory scopeFactory,
        ILogger<ContributionUploadBackgroundService> logger)
    {
        _queue = queue;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Contribution upload background service started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await _queue.WaitToReadAsync(stoppingToken).ConfigureAwait(false))
                {
                    break; // channel completed
                }

                while (_queue.TryRead(out _))
                {
                    await RunUploadAsync(stoppingToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Contribution upload background worker error");
            }
        }
    }

    private async Task RunUploadAsync(CancellationToken token)
    {
        using var scope = _scopeFactory.CreateScope();
        var uploadService = scope.ServiceProvider.GetRequiredService<ContributionUploadService>();
        try
        {
            var result = await uploadService.UploadPendingChangesAsync(false, token).ConfigureAwait(false);
            if (result.Success)
            {
                _logger.LogInformation(
                    "Contribution upload completed: {Total} rows ({Titles} titles, {Mappings} mappings, {Sources} sources, {Series} series, {Metadata} metadata)",
                    result.Total, result.Titles, result.Mappings, result.Sources, result.Series, result.Metadata);
            }
            else
            {
                _logger.LogWarning("Contribution upload failed: {Error}", result.Error);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Contribution upload failed");
        }
    }
}