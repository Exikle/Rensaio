using Microsoft.AspNetCore.Mvc;
using RensaioBackend.Models.Database;
using RensaioBackend.Models.Dto;
using RensaioBackend.Models.Enums;
using RensaioBackend.Services.Auth;
using RensaioBackend.Services.Contributions;
using RensaioBackend.Services.Images;
using RensaioBackend.Services.Metadata;
using RensaioBackend.Services.Scrobbling.Abstractions;
using RensaioBackend.Services.Settings;
using Microsoft.Extensions.Logging;

namespace RensaioBackend.Controllers;

/// <summary>
/// Contribution Mappings page (source scope). Reads/writes the local contribution database
/// (<c>contributor.db</c>) and cascades edits into the Rensaio DB for matching series.
///
/// Access: Owner only AND the app must have Contribution enabled (runtime gate). Provider
/// resolution/authentication/meta + provider-row projection are shared with the External Mappings
/// page via <see cref="ExternalMappingSupport"/>; the matcher is the shared
/// <see cref="MetadataMatchCore"/>; Rensaio-DB writes go through
/// <see cref="RensaioMappingCascadeService"/>. No backend logic is copy-pasted.
/// </summary>
[ApiController]
[Route("api/contribution-mappings")]
[RequireUserLevel(UserLevel.Owner)]
public class ContributionMappingsController : ControllerBase
{
    private readonly ContributionMappingService _mappingService;
    private readonly RensaioMappingCascadeService _cascade;
    private readonly ExternalMappingSupport _support;
    private readonly SettingsService _settingsService;
    private readonly ThumbCacheService _thumbCache;
    private readonly ILogger<ContributionMappingsController> _logger;

    public ContributionMappingsController(
        ContributionMappingService mappingService,
        RensaioMappingCascadeService cascade,
        ExternalMappingSupport support,
        SettingsService settingsService,
        ThumbCacheService thumbCache,
        ILogger<ContributionMappingsController> logger)
    {
        _mappingService = mappingService;
        _cascade = cascade;
        _support = support;
        _settingsService = settingsService;
        _thumbCache = thumbCache;
        _logger = logger;
    }

    /// <summary>Rejects the request (404) when Contribution is disabled or the
    /// Contributor Id is not verified against the contribution database.</summary>
    private async Task<bool> ContributionDisabledAsync(CancellationToken token)
    {
        var settings = await _settingsService.GetSettingsAsync(token).ConfigureAwait(false);
        return !settings.ContributionEnabled || !settings.ContributionVerified;
    }

    private async Task<List<IExternalSeriesProvider>> EnabledProvidersAsync(CancellationToken token)
    {
        var user = HttpContext.Items["User"] as UserEntity;
        return await _support.GetEnabledProvidersAsync(user, token).ConfigureAwait(false);
    }

    /// <summary>
    /// GET /api/contribution-mappings?filter=all|unmatched&page=&pageSize=&provider=&status=
    /// Source-scope: grouped by mapping (automerge on normalized titles). `unmatched` keeps only
    /// mappings with at least one Unmatched (or expired TemporaryIgnored) provider row.
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<ContributionMappingsPageDto>> List(
        [FromQuery] string filter = "unmatched",
        [FromQuery] int page = 0,
        [FromQuery] int pageSize = 50,
        [FromQuery] ExternalSeriesProvider? provider = null,
        [FromQuery] SeriesMappingStatus? status = null,
        CancellationToken token = default)
    {
        if (await ContributionDisabledAsync(token).ConfigureAwait(false)) return NotFound();

        var providers = await EnabledProvidersAsync(token).ConfigureAwait(false);
        var result = await _mappingService.ListMappingsAsync(filter, page, pageSize, provider, status, providers, token)
            .ConfigureAwait(false);

        // Rewrite source + provider covers through the image cache (same as External Mappings).
        await _thumbCache.PopulateThumbsAsync(result.Groups, "/api/image/", token).ConfigureAwait(false);
        await _thumbCache.PopulateThumbsAsync(result.Groups.SelectMany(g => g.Providers).Where(p => p != null), "/api/image/", token).ConfigureAwait(false);

        return Ok(result);
    }

    /// <summary>GET /api/contribution-mappings/mappings/{mappingId} — single mapping group.</summary>
    [HttpGet("mappings/{mappingId:guid}")]
    public async Task<ActionResult<ContributionMappingGroupDto>> GetMapping(Guid mappingId, CancellationToken token)
    {
        if (await ContributionDisabledAsync(token).ConfigureAwait(false)) return NotFound();

        var providers = await EnabledProvidersAsync(token).ConfigureAwait(false);
        var group = await _mappingService.GetMappingAsync(mappingId, providers, token).ConfigureAwait(false);
        if (group == null) return NotFound();

        await _thumbCache.PopulateThumbsAsync(group, "/api/image/", token).ConfigureAwait(false);
        await _thumbCache.PopulateThumbsAsync(group.Providers.Where(p => p != null), "/api/image/", token).ConfigureAwait(false);
        return Ok(group);
    }

    /// <summary>POST /api/contribution-mappings/mappings/{mappingId}/scan — run the shared matcher for one mapping.</summary>
    [HttpPost("mappings/{mappingId:guid}/scan")]
    public async Task<ActionResult> ScanMapping(Guid mappingId, CancellationToken token)
    {
        if (await ContributionDisabledAsync(token).ConfigureAwait(false)) return NotFound();

        var providers = await EnabledProvidersAsync(token).ConfigureAwait(false);
        await _mappingService.ScanMappingAsync(mappingId, providers, token).ConfigureAwait(false);
        return Ok(new { message = "Scan completed" });
    }

    /// <summary>
    /// POST /api/contribution-mappings/mappings/{mappingId}/link — confirm a manual provider link
    /// (body carries the external id/title), then cascade into the Rensaio DB for matching series
    /// (same semantics as External Mappings).
    /// </summary>
    [HttpPost("mappings/{mappingId:guid}/link")]
    public async Task<ActionResult> LinkMapping(Guid mappingId, [FromQuery] ExternalSeriesProvider provider,
        [FromBody] ContributionMappingLinkDto dto, CancellationToken token)
    {
        if (await ContributionDisabledAsync(token).ConfigureAwait(false)) return NotFound();

        await _mappingService.ConfirmAsync(mappingId, provider, dto?.ExternalSeriesId ?? string.Empty,
            dto?.ExternalSeriesTitle, token).ConfigureAwait(false);
        await CascadeAsync(mappingId, provider,
            apply: m => { m.ExternalSeriesId = dto?.ExternalSeriesId ?? string.Empty; m.ExternalSeriesTitle = dto?.ExternalSeriesTitle; m.MappingStatus = SeriesMappingStatus.UserConfirmed; m.LinkedDate = DateTime.UtcNow; },
            create: seriesId => new SeriesMappingEntity
            {
                Id = Guid.NewGuid(),
                SeriesId = seriesId,
                Provider = provider,
                ExternalSeriesId = dto?.ExternalSeriesId ?? string.Empty,
                ExternalSeriesTitle = dto?.ExternalSeriesTitle,
                MappingStatus = SeriesMappingStatus.UserConfirmed,
                LinkedDate = DateTime.UtcNow,
                UpdateDate = DateTime.UtcNow
            },
            token: token).ConfigureAwait(false);
        return Ok(new { message = "Linked" });
    }

    /// <summary>POST /api/contribution-mappings/mappings/{mappingId}/{provider}/block</summary>
    [HttpPost("mappings/{mappingId:guid}/{provider}/block")]
    public async Task<ActionResult> BlockMapping(Guid mappingId, string provider, CancellationToken token)
    {
        if (await ContributionDisabledAsync(token).ConfigureAwait(false)) return NotFound();
        var providerEnum = ExternalMappingSupport.ParseProvider(provider);
        if (providerEnum == null) return BadRequest(new { message = "Invalid provider" });

        await _mappingService.BlockAsync(mappingId, providerEnum.Value, token).ConfigureAwait(false);
        await CascadeAsync(mappingId, providerEnum.Value,
            apply: m => m.MappingStatus = SeriesMappingStatus.Blocked,
            create: null, token: token).ConfigureAwait(false);
        return Ok(new { message = "Blocked" });
    }

    /// <summary>POST /api/contribution-mappings/mappings/{mappingId}/{provider}/unblock</summary>
    [HttpPost("mappings/{mappingId:guid}/{provider}/unblock")]
    public async Task<ActionResult> UnblockMapping(Guid mappingId, string provider, CancellationToken token)
    {
        if (await ContributionDisabledAsync(token).ConfigureAwait(false)) return NotFound();
        var providerEnum = ExternalMappingSupport.ParseProvider(provider);
        if (providerEnum == null) return BadRequest(new { message = "Invalid provider" });

        await _mappingService.UnblockAsync(mappingId, providerEnum.Value, token).ConfigureAwait(false);
        await CascadeAsync(mappingId, providerEnum.Value,
            apply: m => { m.MappingStatus = SeriesMappingStatus.AutoMatched; m.LinkedDate = DateTime.UtcNow; },
            create: null, token: token).ConfigureAwait(false);
        return Ok(new { message = "Unblocked" });
    }

    /// <summary>POST /api/contribution-mappings/mappings/{mappingId}/{provider}/ignore — body {forever:bool}</summary>
    [HttpPost("mappings/{mappingId:guid}/{provider}/ignore")]
    public async Task<ActionResult> IgnoreMapping(Guid mappingId, string provider,
        [FromBody] ExternalMappingIgnoreDto dto, CancellationToken token)
    {
        if (await ContributionDisabledAsync(token).ConfigureAwait(false)) return NotFound();
        var providerEnum = ExternalMappingSupport.ParseProvider(provider);
        if (providerEnum == null) return BadRequest(new { message = "Invalid provider" });

        var forever = dto?.Forever ?? false;
        await _mappingService.IgnoreAsync(mappingId, providerEnum.Value, forever, token).ConfigureAwait(false);
        var status = forever ? SeriesMappingStatus.ForeverIgnored : SeriesMappingStatus.TemporaryIgnored;
        await CascadeAsync(mappingId, providerEnum.Value,
            apply: m => { m.MappingStatus = status; m.LinkedDate = DateTime.UtcNow; },
            create: null, token: token).ConfigureAwait(false);
        return Ok(new { message = "Ignored" });
    }

    /// <summary>DELETE /api/contribution-mappings/mappings/{mappingId}/{provider} — remove a mapping (tombstone).</summary>
    [HttpDelete("mappings/{mappingId:guid}/{provider}")]
    public async Task<ActionResult> UnlinkMapping(Guid mappingId, string provider, CancellationToken token)
    {
        if (await ContributionDisabledAsync(token).ConfigureAwait(false)) return NotFound();
        var providerEnum = ExternalMappingSupport.ParseProvider(provider);
        if (providerEnum == null) return BadRequest(new { message = "Invalid provider" });

        await _mappingService.UnlinkAsync(mappingId, providerEnum.Value, token).ConfigureAwait(false);
        await CascadeAsync(mappingId, providerEnum.Value, create: null, delete: true, token: token).ConfigureAwait(false);
        return Ok(new { message = "Unlinked" });
    }

    /// <summary>
    /// POST /api/contribution-mappings/scan — kick off a full contribution mapping scan across all
    /// mappings in the background (SignalR progress, jobType = MetadataLink).
    /// </summary>
    [HttpPost("scan")]
    public async Task<ActionResult> ScanAll(CancellationToken token)
    {
        if (await ContributionDisabledAsync(token).ConfigureAwait(false)) return NotFound();

        var providers = await EnabledProvidersAsync(token).ConfigureAwait(false);
        _ = Task.Run(() => _mappingService.ScanAllMappingsAsync(providers, CancellationToken.None));
        return Ok(new { message = "Scan started" });
    }

    /// <summary>Best-effort cascade into the Rensaio DB for matching series.</summary>
    private async Task CascadeAsync(Guid mappingId, ExternalSeriesProvider provider,
        Action<SeriesMappingEntity>? apply = null, Func<Guid, SeriesMappingEntity>? create = null,
        bool delete = false, CancellationToken token = default)
    {
        try
        {
            await _cascade.CascadeAsync(mappingId, provider, apply, create, delete, token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to cascade contribution mapping {MappingId} into Rensaio DB", mappingId);
        }
    }
}
