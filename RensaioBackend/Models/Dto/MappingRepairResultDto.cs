using RensaioBackend.Models.Enums;
using System.Text.Json.Serialization;

namespace RensaioBackend.Models.Dto;

/// <summary>
/// A single conflict resolution performed by the mapping-conflict repair pass: which
/// (provider, externalId) was contested, which local series kept it (owner) and which
/// local series were auto-blocked from it (losers).
/// </summary>
public sealed class MappingRepairConflictRowDto
{
    [JsonPropertyName("provider")]
    public ExternalSeriesProvider Provider { get; set; }

    [JsonPropertyName("externalSeriesId")]
    public string ExternalSeriesId { get; set; } = string.Empty;

    [JsonPropertyName("externalSeriesTitle")]
    public string? ExternalSeriesTitle { get; set; }

    [JsonPropertyName("ownerSeriesId")]
    public Guid OwnerSeriesId { get; set; }

    [JsonPropertyName("ownerSeriesTitle")]
    public string? OwnerSeriesTitle { get; set; }

    /// <summary>Local series ids that were auto-blocked against this (provider, externalId).</summary>
    [JsonPropertyName("blockedSeriesIds")]
    public List<Guid> BlockedSeriesIds { get; set; } = [];
}

/// <summary>Result of a mapping-conflict repair pass.</summary>
public sealed class MappingRepairResultDto
{
    [JsonPropertyName("conflictsDetected")]
    public int ConflictsDetected { get; set; }

    [JsonPropertyName("seriesRepaired")]
    public int SeriesRepaired { get; set; }

    [JsonPropertyName("blocksCreated")]
    public int BlocksCreated { get; set; }

    [JsonPropertyName("mappingsCleared")]
    public int MappingsCleared { get; set; }

    [JsonPropertyName("conflicts")]
    public List<MappingRepairConflictRowDto> Conflicts { get; set; } = [];

    [JsonPropertyName("repairedSeriesIds")]
    public List<Guid> RepairedSeriesIds { get; set; } = [];

    [JsonPropertyName("skippedUserConfirmedConflicts")]
    public bool SkippedUserConfirmedConflicts { get; set; }
}