using RensaioBackend.Data;
using RensaioBackend.Models.Database;
using RensaioBackend.Models.Dto;
using RensaioBackend.Models.Enums;
using RensaioBackend.Services.Auth;
using RensaioBackend.Services.Background;
using RensaioBackend.Services.Contributions;
using RensaioBackend.Services.Contributions.Abstractions;
using RensaioBackend.Services.Images;
using RensaioBackend.Services.Metadata;
using RensaioBackend.Services.Scrobbling;
using RensaioBackend.Services.Scrobbling.Abstractions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace RensaioBackend.Controllers;

/// <summary>
/// External Mappings page. Reads/writes the global <see cref="SeriesMappingEntity"/> table
/// (series scope) and the in-memory <see cref="IGlobalMetadataRepository"/> (titles scope).
/// Provider resolution/authentication/meta + mapping-row projection are shared with the
/// Contribution Mappings page via <see cref="ExternalMappingSupport"/>.
/// </summary>
[ApiController]
[Route("api/external-mappings")]
// Owner / Admin / Manager only — the mapping page lives behind the user-avatar menu.
[RequireUserLevel(UserLevel.Manager)]
public class ExternalMappingsController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly MetadataLinkEngine _linkEngine;
    private readonly ExternalMappingSupport _support;
    private readonly IGlobalMetadataRepository _repo;
    private readonly MetadataBackgroundScanService _scanService;
    private readonly ContributionPropagationService _contributionSync;
    private readonly ThumbCacheService _thumbCache;
    private readonly ILogger<ExternalMappingsController> _logger;

    public ExternalMappingsController(AppDbContext db, MetadataLinkEngine linkEngine,
        ExternalMappingSupport support, IGlobalMetadataRepository repo,
        MetadataBackgroundScanService scanService,
        ContributionPropagationService contributionSync,
        ThumbCacheService thumbCache,
        ILogger<ExternalMappingsController> logger)
    {
        _db = db;
        _linkEngine = linkEngine;
        _support = support;
        _repo = repo;
        _scanService = scanService;
        _contributionSync = contributionSync;
        _thumbCache = thumbCache;
        _logger = logger;
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

    /// <summary>
    /// GET /api/external-mappings?filter=all|unmatched&page=&pageSize=&provider=&status=
    /// Series-scope only. `unmatched` (default) returns only local series that have at least one
    /// Unmatched provider row, or a TemporaryIgnored row whose LinkedDate + 1 month has passed
    /// (the ignore has expired — the row is treated as Unmatched again).
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<ExternalMappingsPageDto>> List(
        [FromQuery] string filter = "unmatched",
        [FromQuery] int page = 0,
        [FromQuery] int pageSize = 50,
        [FromQuery] ExternalSeriesProvider? provider = null,
        [FromQuery] SeriesMappingStatus? status = null,
        CancellationToken token = default)
    {
        var result = new ExternalMappingsPageDto
        {
            Page = page,
            PageSize = pageSize,
            Total = 0
        };

        // The global-titles scope has been removed — this page is always series-scoped.
        var isUnmatchedFilter = string.Equals(filter, "unmatched", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(filter);

        // Cutoff for "TemporaryIgnored has expired": LinkedDate + 1 month <= now.
        var nowMinusOneMonth = DateTime.UtcNow.AddMonths(-1);

        // Series scope: grouped by series — one group per local series, each holding one entry per
        // enabled metadata provider. Pagination is over series (total = number of groups, never
        // provider rows). A provider row reuses the global mapping when present (its cover, alt
        // titles, cross-site ids) or is synthesized as Unmatched so the page can always manual-map.
        // Providers behind LinkedSitesIds are covered by the cross-product and autolinked by the
        // engine on confirm. Only public providers (no auth) + authenticated providers show up.
        var user = HttpContext.Items["User"] as UserEntity;
        var providers = await _support.GetEnabledProvidersAsync(user, token);

        // Root-level static per-provider meta: icon + series page URL template (provider name -> info).
        // Keyed by the provider NAME (not the int) so the frontend can look it up via ScrobblerProvider.
        result.ProviderMeta = ExternalMappingSupport.BuildProviderMeta(providers);

        var allMappings = await _db.SeriesMappings
            .Where(m => m.SeriesId != null)
            .ToListAsync(token);

        var seriesWithSources = await _db.Series
            .Include(s => s.Sources)
            .ToListAsync(token);

        // Default: expose all statuses, including persisted Unmatched rows (manual mapping must stay
        // reachable). An explicit status filter narrows to that single status.
        var activeStatuses = new HashSet<SeriesMappingStatus>
        {
            SeriesMappingStatus.Unmatched,
            SeriesMappingStatus.AutoMatched,
            SeriesMappingStatus.UserConfirmed,
            SeriesMappingStatus.Blocked,
            SeriesMappingStatus.TemporaryIgnored,
            SeriesMappingStatus.ForeverIgnored
        };

        var groups = new List<ExternalSeriesGroupDto>();
        foreach (var series in seriesWithSources)
        {
            var group = new ExternalSeriesGroupDto
            {
                SeriesId = series.Id,
                SeriesTitle = series.Title,
                SeriesCoverUrl = series.ThumbnailUrl, // local thumbnail; provider covers live per-row
                Providers = []
            };

            var groupHasUnmatched = false;
            foreach (var p in providers)
            {
                var mapping = allMappings.FirstOrDefault(m => m.SeriesId == series.Id && m.Provider == p.ProviderType);
                var row = mapping != null
                    ? ExternalMappingSupport.MappingToProviderDto(mapping)
                    : new ExternalMappingsSeriesProviderDto
                    {
                        ProviderCoverUrl = null, // no mapping → no provider cover yet (a scan fills it)
                        Provider = p.ProviderType,
                        ExternalSeriesId = string.Empty,
                        ExternalSeriesTitle = null,
                        MappingStatus = SeriesMappingStatus.Unmatched,
                        LinkedDate = null,
                        LinkedSitesIds = [],
                        AlternativeTitles = [] // Unmatched rows carry no provider alt titles yet
                    };

                // Expired TemporaryIgnored → treat as Unmatched again (re-evaluation due).
                // LinkedDate + 1 month <= now  <=>  LinkedDate <= now - 1 month.
                if (row.MappingStatus == SeriesMappingStatus.TemporaryIgnored &&
                    row.LinkedDate != null &&
                    row.LinkedDate <= nowMinusOneMonth)
                {
                    row.MappingStatus = SeriesMappingStatus.Unmatched;
                }

                if (status != null)
                {
                    if (row.MappingStatus != status) continue;
                }
                else if (!activeStatuses.Contains(row.MappingStatus))
                {
                    continue;
                }

                if (row.MappingStatus == SeriesMappingStatus.Unmatched)
                    groupHasUnmatched = true;

                group.Providers.Add(row);
            }

            if (group.Providers.Count == 0) continue;

            // filter=unmatched (default): keep only series that have at least one provider row
            // needing attention (Unmatched, including freshly-expired TemporaryIgnored).
            if (isUnmatchedFilter && !groupHasUnmatched) continue;

            groups.Add(group);
        }

        groups.Sort((a, b) => (a.SeriesTitle ?? string.Empty).CompareTo(b.SeriesTitle ?? string.Empty));

        result.Total = groups.Count;
        result.Series = groups
            .Skip(page * pageSize).Take(pageSize)
            .ToList();

        // Rewrite local + provider covers through the image cache before sending to the
        // frontend (Cloudflare bypass, etag + cache support). Never persisted to the DB —
        // the DTOs are disposable projections built above.
        var pageGroups = result.Series;
        await _thumbCache.PopulateThumbsAsync(pageGroups, "/api/image/", token).ConfigureAwait(false);
        await _thumbCache.PopulateThumbsAsync(pageGroups.SelectMany(a => a.Providers).Where(a => a != null), "/api/image/", token).ConfigureAwait(false);

        return Ok(result);
    }

    /// <summary>GET /api/external-mappings/series/{seriesId} — single-series group (all provider rows).</summary>
    [HttpGet("series/{seriesId:guid}")]
    public async Task<ActionResult<ExternalSeriesGroupDto>> GetSeries(Guid seriesId, CancellationToken token)
    {
        var series = await _db.Series
            .Include(s => s.Sources)
            .FirstOrDefaultAsync(s => s.Id == seriesId, token);
        if (series == null) return NotFound();

        var user = HttpContext.Items["User"] as UserEntity;
        var providers = await _support.GetEnabledProvidersAsync(user, token);
        var mappings = await _db.SeriesMappings
            .Where(m => m.SeriesId == seriesId)
            .ToListAsync(token);

        var group = new ExternalSeriesGroupDto
        {
            SeriesId = series.Id,
            SeriesTitle = series.Title,
            SeriesCoverUrl = series.ThumbnailUrl,
            Providers = []
        };

        foreach (var p in providers)
        {
            var mapping = mappings.FirstOrDefault(m => m.Provider == p.ProviderType);
            group.Providers.Add(mapping != null
                ? ExternalMappingSupport.MappingToProviderDto(mapping)
                : new ExternalMappingsSeriesProviderDto
                {
                    ProviderCoverUrl = null,
                    Provider = p.ProviderType,
                    ExternalSeriesId = string.Empty,
                    ExternalSeriesTitle = null,
                    MappingStatus = SeriesMappingStatus.Unmatched,
                    LinkedDate = null,
                    LinkedSitesIds = [],
                    AlternativeTitles = [] // Unmatched rows carry no provider alt titles yet
                });
        }

        // Rewrite local + provider covers through the image cache (Cloudflare bypass, etag + cache).
        await _thumbCache.PopulateThumbsAsync(group, "/api/image/", token).ConfigureAwait(false);
        await _thumbCache.PopulateThumbsAsync(group.Providers.Where(a => a != null), "/api/image/", token).ConfigureAwait(false);

        return Ok(group);
    }

    /// <summary>POST /api/external-mappings/series/{seriesId}/scan — run the engine for one series.</summary>
    [HttpPost("series/{seriesId:guid}/scan")]
    public async Task<ActionResult> ScanSeries(Guid seriesId, CancellationToken token)
    {
        await _linkEngine.LinkSeriesAsync(seriesId, token).ConfigureAwait(false);
        return Ok(new { message = "Scan completed" });
    }

    /// <summary>
    /// POST /api/external-mappings/series/{seriesId}/link?provider=... — link a series to a provider,
    /// then autolink any non-linked providers resolvable via LinkedSitesIds.
    /// </summary>
    [HttpPost("series/{seriesId:guid}/link")]
    public async Task<ActionResult> LinkSeries(Guid seriesId, [FromQuery] ExternalSeriesProvider provider, CancellationToken token)
    {
        var result = await _linkEngine.LinkSeriesAsync(seriesId, token).ConfigureAwait(false);

        // Propagate the manual (UserConfirmed) links into the local contribution database so
        // they participate in cloud contribution sync — same as block/unlink/ignore paths.
        await SyncContributionAsync(seriesId, token).ConfigureAwait(false);

        return Ok(new { message = $"Linked {result.Links.Count} entries" });
    }

    /// <summary>POST /api/external-mappings/series/{seriesId}/{provider}/block</summary>
    [HttpPost("series/{seriesId:guid}/{provider}/block")]
    public async Task<ActionResult> BlockSeries(Guid seriesId, string provider, CancellationToken token)
    {
        var providerEnum = ExternalMappingSupport.ParseProvider(provider);
        if (providerEnum == null) return BadRequest(new { message = "Invalid provider" });
        ExternalSeriesProvider providerResolved = (ExternalSeriesProvider)providerEnum;
        var mapping = await _db.SeriesMappings
            .FirstOrDefaultAsync(m => m.SeriesId == seriesId && m.Provider == providerResolved, token);
        if (mapping == null)
        {
            mapping = new SeriesMappingEntity
            {
                Id = Guid.NewGuid(),
                SeriesId = seriesId,
                Provider = providerResolved,
                ExternalSeriesId = string.Empty,
                MappingStatus = SeriesMappingStatus.Blocked,
                UpdateDate = DateTime.UtcNow
            };
            _db.SeriesMappings.Add(mapping);
        }
        else
        {
            mapping.MappingStatus = SeriesMappingStatus.Blocked;
            mapping.LinkedDate = mapping.LinkedDate ?? DateTime.UtcNow;
            mapping.UpdateDate = DateTime.UtcNow;
        }
        await _db.SaveChangesAsync(token);
        await SyncContributionAsync(seriesId, token);
        return Ok(new { message = "Blocked" });
    }

    /// <summary>POST /api/external-mappings/series/{seriesId}/{provider}/unblock</summary>
    [HttpPost("series/{seriesId:guid}/{provider}/unblock")]
    public async Task<ActionResult> UnblockSeries(Guid seriesId, string provider, CancellationToken token)
    {
        var providerEnum = ExternalMappingSupport.ParseProvider(provider);
        if (providerEnum == null) return BadRequest(new { message = "Invalid provider" });
        var mapping = await _db.SeriesMappings
            .FirstOrDefaultAsync(m => m.SeriesId == seriesId && m.Provider == providerEnum, token);
        if (mapping != null)
        {
            mapping.MappingStatus = SeriesMappingStatus.AutoMatched;
            mapping.LinkedDate = DateTime.UtcNow;
            mapping.UpdateDate = DateTime.UtcNow;
            await _db.SaveChangesAsync(token);
            await SyncContributionAsync(seriesId, token);
        }
        return Ok(new { message = "Unblocked" });
    }

    /// <summary>POST /api/external-mappings/series/{seriesId}/{provider}/ignore — body {forever:bool}</summary>
    [HttpPost("series/{seriesId:guid}/{provider}/ignore")]
    public async Task<ActionResult> IgnoreSeries(Guid seriesId, string provider,
        [FromBody] ExternalMappingIgnoreDto dto, CancellationToken token)
    {
        var providerEnum = ExternalMappingSupport.ParseProvider(provider);
        if (providerEnum == null) return BadRequest(new { message = "Invalid provider" });
        ExternalSeriesProvider providerResolved = (ExternalSeriesProvider)providerEnum;
        var mapping = await _db.SeriesMappings
            .FirstOrDefaultAsync(m => m.SeriesId == seriesId && m.Provider == providerResolved, token);
        var status = dto?.Forever ?? false
            ? SeriesMappingStatus.ForeverIgnored
            : SeriesMappingStatus.TemporaryIgnored;
        if (mapping == null)
        {
            mapping = new SeriesMappingEntity
            {
                Id = Guid.NewGuid(),
                SeriesId = seriesId,
                Provider = (ExternalSeriesProvider)providerEnum,
                ExternalSeriesId = string.Empty,
                MappingStatus = status,
                LinkedDate = DateTime.UtcNow,
                UpdateDate = DateTime.UtcNow
            };
            _db.SeriesMappings.Add(mapping);
        }
        else
        {
            mapping.MappingStatus = status;
            mapping.LinkedDate = DateTime.UtcNow;
            mapping.UpdateDate = DateTime.UtcNow;
        }
        await _db.SaveChangesAsync(token);
        await SyncContributionAsync(seriesId, token);
        return Ok(new { message = "Ignored" });
    }

    /// <summary>
    /// POST /api/external-mappings/scan — kick off a full metadata scan across all series.
    /// Returns immediately; the scan runs in the background and publishes live progress via
    /// SignalR (ProgressHub, jobType = MetadataLink). The response never blocks on the scan.
    /// </summary>
    [HttpPost("scan")]
    public async Task<ActionResult> ScanAll()
    {
        // Fire-and-forget in the background (own service scope) so the HTTP call returns immediately.
        _scanService.TriggerAsync().ConfigureAwait(false);
        return Ok(new { message = "Scan started" });
    }

    /// <summary>DELETE /api/external-mappings/series/{seriesId}/{provider} — remove a mapping (set Unmatched / delete row).</summary>
    [HttpDelete("series/{seriesId:guid}/{provider}")]
    public async Task<ActionResult> UnlinkSeries(Guid seriesId, string provider, CancellationToken token)
    {
        var providerEnum = ExternalMappingSupport.ParseProvider(provider);
        if (providerEnum == null) return BadRequest(new { message = "Invalid provider" });
        var mapping = await _db.SeriesMappings
            .FirstOrDefaultAsync(m => m.SeriesId == seriesId && m.Provider == providerEnum, token);
        if (mapping != null)
            _db.SeriesMappings.Remove(mapping);
        await _db.SaveChangesAsync(token);
        await SyncContributionAsync(seriesId, token);
        return Ok(new { message = "Unlinked" });
    }

    /// <summary>POST /api/external-mappings/titles/{titleId}/scan — refresh a global title from the repo.</summary>
    [HttpPost("titles/{titleId}/scan")]
    public async Task<ActionResult> ScanTitle(string titleId, CancellationToken token)
    {
        var title = await _repo.GetTitleAsync(titleId, token);
        if (title == null) return NotFound();
        return Ok(new { message = "Title refreshed" });
    }

    /// <summary>
    /// POST /api/external-mappings/titles/{titleId}/promote?seriesId=... — attach a global title's
    /// provider associations to an existing local series (creates the SeriesId-back mapping rows).
    /// </summary>
    [HttpPost("titles/{titleId}/promote")]
    public async Task<ActionResult> PromoteTitle(Guid seriesId, string titleId, CancellationToken token)
    {
        var title = await _repo.GetTitleAsync(titleId, token);
        if (title == null) return NotFound();
        var associations = _repo.GetAssociationsAsync(titleId, token);
        if (associations.Count == 0)
            return Ok(new { message = "No associations to promote" });

        foreach (var a in associations)
        {
            var existing = await _db.SeriesMappings
                .FirstOrDefaultAsync(m => m.SeriesId == seriesId && m.Provider == a.Provider, token);
            if (existing != null)
            {
                existing.ExternalSeriesId = a.ProviderKey;
                existing.MappingStatus = SeriesMappingStatus.AutoMatched;
                existing.LinkedDate = DateTime.UtcNow;
                existing.UpdateDate = DateTime.UtcNow;
            }
            else
            {
                _db.SeriesMappings.Add(new SeriesMappingEntity
                {
                    Id = Guid.NewGuid(),
                    SeriesId = seriesId,
                    Provider = a.Provider,
                    ExternalSeriesId = a.ProviderKey,
                    MappingStatus = SeriesMappingStatus.AutoMatched,
                    LinkedDate = DateTime.UtcNow,
                    UpdateDate = DateTime.UtcNow
                });
            }
        }
        await _db.SaveChangesAsync(token);
        await SyncContributionAsync(seriesId, token);
        return Ok(new { message = $"Promoted {associations.Count} associations" });
    }
}