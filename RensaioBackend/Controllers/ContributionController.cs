using Microsoft.AspNetCore.Mvc;
using RensaioBackend.Models.Enums;
using RensaioBackend.Services.Auth;
using RensaioBackend.Services.Contributions;

namespace RensaioBackend.Controllers;

/// <summary>
/// Contribution endpoints — exports the local contribution database to the cloud
/// contribution worker as a ContributionSnapshotV1.
/// </summary>
[ApiController]
[Route("api/contributions")]
[RequireUserLevel(UserLevel.Manager)]
public class ContributionController : ControllerBase
{
    private readonly ContributionUploadService _uploadService;
    private readonly ContributionDownloadService _downloadService;

    public ContributionController(
        ContributionUploadService uploadService,
        ContributionDownloadService downloadService)
    {
        _uploadService = uploadService;
        _downloadService = downloadService;
    }

    /// <summary>
    /// Enqueues a ContributionSnapshotV1 upload of the local contribution database
    /// to the cloud worker and returns immediately. The upload runs in the
    /// background; on success those rows are marked Version -2 (Uploaded).
    /// </summary>
    /// <response code="202">Upload queued — processing in background.</response>
    [HttpPost("upload")]
    [ProducesResponseType(typeof(object), StatusCodes.Status202Accepted)]
    public async Task<IActionResult> UploadAsync([FromQuery] bool force = false, CancellationToken token = default)
    {
        if (force)
        {
            // Synchronous full re-upload: include already-uploaded rows so a
            // cloud wipe can be repopulated. Runs inline (spans network + DB).
            var result = await _uploadService.UploadPendingChangesAsync(force: true, token).ConfigureAwait(false);
            return result.Success
                ? Ok(new { queued = true, force = true, processed = result.Processed, total = result.Total })
                : BadRequest(new { error = result.Error });
        }

        bool queued = _uploadService.EnqueueUpload();
        return StatusCode(StatusCodes.Status202Accepted, new { queued });
    }

    /// <summary>
    /// Downloads the current cloud contribution snapshot (GET /snapshot on the
    /// worker) and applies it to the local contribution database. Cloud mapping /
    /// metadata ids are transient references — they are re-keyed to local ids by
    /// title-overlap (mappings) and (mapping, provider, key) dedup (metadata).
    /// Runs synchronously (spans several network + DB operations).
    /// </summary>
    /// <response code="200">Download applied with per-entity counts.</response>
    [HttpPost("download")]
    [ProducesResponseType(typeof(ContributionDownloadResult), StatusCodes.Status200OK)]
    public async Task<ActionResult<ContributionDownloadResult>> DownloadAsync(CancellationToken token)
    {
        var result = await _downloadService.DownloadSnapshotAsync(token).ConfigureAwait(false);
        if (!result.Success)
        {
            return BadRequest(new { error = result.Error });
        }
        return Ok(result);
    }
}