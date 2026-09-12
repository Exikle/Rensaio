using RensaioBackend.Data;
using RensaioBackend.Models.ContributionDatabase;
using RensaioBackend.Services.Contributions.Snapshot;
using RensaioBackend.Services.Settings;
using Microsoft.EntityFrameworkCore;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RensaioBackend.Services.Contributions;

/// <summary>
/// Uploads the local contribution database's pending changes (Version 0 = add/update,
/// -1 = delete) to the Cloudflare contribution worker (/upload), then marks them
/// uploaded by setting Version = -2.
/// </summary>
public class ContributionUploadService
{
    private const int VersionAddOrUpdate = 0;
    private const int VersionLogicalDelete = -1;
    private const int VersionUploaded = -2;

    private readonly ContributionDbContext _contributorDb;
    private readonly SettingsService _settingsService;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IContributionUploadQueue _queue;
    private readonly ILogger<ContributionUploadService> _logger;

    public ContributionUploadService(
        ContributionDbContext contributorDb,
        SettingsService settingsService,
        IHttpClientFactory httpClientFactory,
        IContributionUploadQueue queue,
        ILogger<ContributionUploadService> logger)
    {
        _contributorDb = contributorDb;
        _settingsService = settingsService;
        _httpClientFactory = httpClientFactory;
        _queue = queue;
        _logger = logger;
    }

    /// <summary>
    /// Fast path used by the controller — enqueues an upload request and returns
    /// immediately. The actual upload runs in the background hosted service.
    /// </summary>
    public bool EnqueueUpload()
    {
        return _queue.Enqueue();
    }

    /// <summary>
    /// Builds a ContributionSnapshotV1 from all rows with Version 0 or -1, POSTs it to
    /// the worker, and on success flips those rows to Version -2 (Uploaded).
    /// When <paramref name="force"/> is set, ALL local rows (including already-uploaded
    /// Version -2) are included — used after a cloud wipe to re-upload the full dataset.
    /// </summary>
    /// <returns>Upload result with per-entity counts, or an error message.</returns>
    public async Task<ContributionUploadResult> UploadPendingChangesAsync(
        bool force = false, CancellationToken token = default)
    {
        var settings = await _settingsService.GetSettingsAsync(token).ConfigureAwait(false);
        if (!settings.ContributionEnabled)
        {
            return ContributionUploadResult.Failed("Contribution is disabled in settings.");
        }

        if (!settings.ContributionVerified)
        {
            return ContributionUploadResult.Failed("Contribution contributor id has not been verified against the contribution database.");
        }

        if (string.IsNullOrWhiteSpace(settings.ContributionContributorId))
        {
            return ContributionUploadResult.Failed("Contribution contributor id is not configured.");
        }

        if (string.IsNullOrWhiteSpace(settings.ContributionServerUrl))
        {
            return ContributionUploadResult.Failed("Contribution server url is not configured.");
        }

        // Load pending rows (Version 0 = add/update, -1 = delete). With force,
        // include already-uploaded (Version -2) rows so a cloud wipe is repopulated.
        var pendingVersions = force
            ? new[] { VersionAddOrUpdate, VersionLogicalDelete, VersionUploaded }
            : new[] { VersionAddOrUpdate, VersionLogicalDelete };

        var titles = await _contributorDb.Titles
            .Where(t => pendingVersions.Contains(t.Version))
            .AsNoTracking()
            .ToListAsync(token).ConfigureAwait(false);
        var mappingTitles = await _contributorDb.MappingTitles
            .Where(mt => pendingVersions.Contains(mt.Version))
            .AsNoTracking()
            .ToListAsync(token).ConfigureAwait(false);
        var sources = await _contributorDb.Sources
            .Where(s => pendingVersions.Contains(s.Version))
            .AsNoTracking()
            .ToListAsync(token).ConfigureAwait(false);
        var series = await _contributorDb.Series
            .Where(s => pendingVersions.Contains(s.Version))
            .AsNoTracking()
            .ToListAsync(token).ConfigureAwait(false);
        var metadata = await _contributorDb.Metadata
            .Where(m => pendingVersions.Contains(m.Version))
            .AsNoTracking()
            .ToListAsync(token).ConfigureAwait(false);

        int total =
            titles.Count + mappingTitles.Count + sources.Count +
            series.Count + metadata.Count;

        if (total == 0)
        {
            return ContributionUploadResult.Succeeded(0, 0, 0, 0, 0, 0, 0);
        }

        // Force re-upload: rows already marked Uploaded (Version -2) must NOT be
        // serialized as v:-2 — the worker's validateRow only accepts 0 (upsert)
        // or -1 (delete). Normalize them to 0 (add/update) so the full dataset is
        // re-sent as upserts. Genuine tombstones (-1) are preserved as deletes.
        if (force)
        {
            NormalizeVersionToUpsert(titles, mappingTitles, sources, series, metadata);
        }

        // Build the snapshot payload using the 1-letter JSON field names the worker expects.
        var snapshot = new ContributionSnapshotV1
        {
            SchemaVersion = ContributionSnapshotV1.CurrentSchemaVersion,
            GeneratedUtc = DateTime.UtcNow,
            // The snapshot's top-level Version is informational; the worker merges blindly.
            Version = 0,
            Titles = titles,
            Mappings = mappingTitles,
            Sources = sources,
            Series = series,
            Metadata = metadata,
        };

        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        string endpoint = settings.ContributionServerUrl.TrimEnd('/') +
            "/upload?contributor=" + Uri.EscapeDataString(settings.ContributionContributorId);

        using var client = _httpClientFactory.CreateClient("ContributionUpload");
        try
        {
            using var response = await client.PostAsJsonAsync(endpoint, snapshot, options, token).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                string body = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
                return ContributionUploadResult.Failed(
                    $"Contribution worker rejected upload ({response.StatusCode}): {Truncate(body, 500)}");
            }

            // Parse the worker's JSON response. `errors` contains per-row failures
            // (entity list + index). Only mark rows uploaded when they truly landed.
            string responseBody = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            var workerResponse = DeserializeWorkerResponse(responseBody);
            int processed = workerResponse?.Processed ?? titles.Count;

            if (workerResponse?.Errors is { Count: > 0 })
            {
                var errorDetails = string.Join("; ", workerResponse.Errors.Take(50).Select(e => e.ToString()));
                _logger.LogWarning(
                    "Contribution worker reported {ErrorCount} per-row errors: {Errors}",
                    workerResponse.Errors.Count,
                    errorDetails);
                // The worker may still have persisted a subset. Don't blind-mark;
                // mark uploaded only for lists with zero errors so the failed rows
                // remain pending for a retry.
                var failedLists = workerResponse.Errors.Select(e => e.List).ToHashSet();
                await MarkUploadedForListsAsync(failedLists, token).ConfigureAwait(false);

                return ContributionUploadResult.Succeeded(
                    titles.Count, mappingTitles.Count, sources.Count,
                    series.Count, metadata.Count, total, processed, errorDetails);
            }

            // No per-row errors → everything landed.
            await MarkUploadedAsync(token).ConfigureAwait(false);

            return ContributionUploadResult.Succeeded(
                titles.Count, mappingTitles.Count, sources.Count,
                series.Count, metadata.Count, total, processed);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogError(ex, "Contribution upload HTTP failure");
            return ContributionUploadResult.Failed(
                $"Failed to reach contribution worker: {ex.Message}");
        }
    }

    /// <summary>
    /// Set Version = -2 (Uploaded) on every row that had Version 0 or -1.
    /// The entities are already change-tracked? No — they were loaded AsNoTracking,
    /// so we re-query and update in one atomic SaveChanges.
    /// </summary>
    private async Task MarkUploadedAsync(CancellationToken token)
    {
        // Re-fetch tracked copies and mutate.
        int updated = 0;
        updated += await ExecuteVersionBumpAsync(
            _contributorDb.Titles.Where(t => t.Version == VersionAddOrUpdate || t.Version == VersionLogicalDelete),
            token).ConfigureAwait(false);
        updated += await ExecuteVersionBumpAsync(
            _contributorDb.MappingTitles.Where(mt => mt.Version == VersionAddOrUpdate || mt.Version == VersionLogicalDelete),
            token).ConfigureAwait(false);
        updated += await ExecuteVersionBumpAsync(
            _contributorDb.Sources.Where(s => s.Version == VersionAddOrUpdate || s.Version == VersionLogicalDelete),
            token).ConfigureAwait(false);
        updated += await ExecuteVersionBumpAsync(
            _contributorDb.Series.Where(s => s.Version == VersionAddOrUpdate || s.Version == VersionLogicalDelete),
            token).ConfigureAwait(false);
        updated += await ExecuteVersionBumpAsync(
            _contributorDb.Metadata.Where(m => m.Version == VersionAddOrUpdate || m.Version == VersionLogicalDelete),
            token).ConfigureAwait(false);

        if (updated > 0)
        {
            await _contributorDb.SaveChangesAsync(token).ConfigureAwait(false);
        }
    }

    private async Task<int> ExecuteVersionBumpAsync<T>(IQueryable<T> query, CancellationToken token)
        where T : class
    {
        List<T> rows = await query.ToListAsync(token).ConfigureAwait(false);
        foreach (T row in rows)
        {
            var versionProp = typeof(T).GetProperty(nameof(ContributionSourceEntity.Version));
            versionProp?.SetValue(row, VersionUploaded);
        }

        // Attach may be needed if AsNoTracking state was used; rows from a tracked query are fine.
        // To be safe, attach each.
        foreach (T row in rows)
        {
            _contributorDb.Attach(row);
            _contributorDb.Entry(row).Property(nameof(ContributionSourceEntity.Version)).IsModified = true;
        }
        return rows.Count;
    }

    /// <summary>
    /// Like <see cref="MarkUploadedAsync"/> but only bumps rows whose entity list
    /// did NOT experience per-row errors (so failed rows stay pending for retry).
    /// </summary>
    private async Task MarkUploadedForListsAsync(HashSet<string> failedLists, CancellationToken token)
    {
        bool skip(string list) => failedLists.Contains(list);

        int updated = 0;
        if (!skip("t"))
        {
            updated += await ExecuteVersionBumpAsync(
                _contributorDb.Titles.Where(t => t.Version == VersionAddOrUpdate || t.Version == VersionLogicalDelete),
                token).ConfigureAwait(false);
        }
        if (!skip("m"))
        {
            updated += await ExecuteVersionBumpAsync(
                _contributorDb.MappingTitles.Where(mt => mt.Version == VersionAddOrUpdate || mt.Version == VersionLogicalDelete),
                token).ConfigureAwait(false);
        }
        if (!skip("s"))
        {
            updated += await ExecuteVersionBumpAsync(
                _contributorDb.Sources.Where(s => s.Version == VersionAddOrUpdate || s.Version == VersionLogicalDelete),
                token).ConfigureAwait(false);
        }
        if (!skip("i"))
        {
            updated += await ExecuteVersionBumpAsync(
                _contributorDb.Series.Where(s => s.Version == VersionAddOrUpdate || s.Version == VersionLogicalDelete),
                token).ConfigureAwait(false);
        }
        if (!skip("d"))
        {
            updated += await ExecuteVersionBumpAsync(
                _contributorDb.Metadata.Where(m => m.Version == VersionAddOrUpdate || m.Version == VersionLogicalDelete),
                token).ConfigureAwait(false);
        }

        if (updated > 0)
        {
            await _contributorDb.SaveChangesAsync(token).ConfigureAwait(false);
        }
    }

    private static WorkerUploadResponse? DeserializeWorkerResponse(string body)
    {
        try
        {
            return JsonSerializer.Deserialize<WorkerUploadResponse>(body,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Force-reupload helper: any row already marked Uploaded (Version -2) is
    /// treated as an add/update (0) on the wire. Tombstones (-1) are preserved.
    /// Executed on detached/AsNoTracking lists, so no EF state is disturbed.
    /// </summary>
    private static void NormalizeVersionToUpsert(
        List<TitleEntity> titles,
        List<MappingTitleEntity> mappingTitles,
        List<ContributionSourceEntity> sources,
        List<ContributionSeriesEntity> series,
        List<ContributionMetadataEntity> metadata)
    {
        foreach (var t in titles)
            if (t.Version == VersionUploaded) t.Version = VersionAddOrUpdate;
        foreach (var mt in mappingTitles)
            if (mt.Version == VersionUploaded) mt.Version = VersionAddOrUpdate;
        foreach (var s in sources)
            if (s.Version == VersionUploaded) s.Version = VersionAddOrUpdate;
        foreach (var s in series)
            if (s.Version == VersionUploaded) s.Version = VersionAddOrUpdate;
        foreach (var m in metadata)
            if (m.Version == VersionUploaded) m.Version = VersionAddOrUpdate;
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max];
}

/// <summary>Deserialized response from the contribution worker's /upload endpoint.</summary>
internal sealed class WorkerUploadResponse
{
    [JsonPropertyName("processed")]
    public int Processed { get; set; }

    [JsonPropertyName("errors")]
    public List<WorkerUploadError> Errors { get; set; } = [];
}

internal sealed class WorkerUploadError
{
    [JsonPropertyName("list")]
    public string List { get; set; } = string.Empty;

    [JsonPropertyName("index")]
    public int Index { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    /// <summary>
    /// Renders like [list@index] message (e.g. "[s@2] Invalid replication version (v): -2")
    /// so logs/join() show WHAT happened instead of the type name.
    /// </summary>
    public override string ToString() => $"[{List}@{Index}] {Message}";
}

/// <summary>Result of a contribution upload attempt.</summary>
public sealed class ContributionUploadResult
{
    public bool Success { get; private init; }
    public string? Error { get; private init; }
    public int Titles { get; private init; }
    public int Mappings { get; private init; }
    public int Sources { get; private init; }
    public int Series { get; private init; }
    public int Metadata { get; private init; }
    public int Total { get; private init; }
    public int Processed { get; private init; }

    /// <summary>Human-readable worker per-row errors, e.g. "[s@2] message; [i@3] message".</summary>
    public string? WorkerErrors { get; private init; }

    public static ContributionUploadResult Succeeded(
        int titles, int mappings, int sources, int series, int metadata, int total, int processed,
        string? workerErrors = null) => new()
        {
            Success = true,
            Titles = titles,
            Mappings = mappings,
            Sources = sources,
            Series = series,
            Metadata = metadata,
            Total = total,
            Processed = processed,
            WorkerErrors = workerErrors,
        };

    public static ContributionUploadResult Failed(string error) => new()
    {
        Success = false,
        Error = error,
    };
}