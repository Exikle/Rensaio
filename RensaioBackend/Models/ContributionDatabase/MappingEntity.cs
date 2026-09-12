namespace RensaioBackend.Models.ContributionDatabase
{
    /// <summary>
    /// A mapping aggregate in the local contributor database. A mapping links a set of
    /// normalized titles (via <see cref="MappingTitleEntity"/>) and gathers sources
    /// (<see cref="ContributionSeriesEntity"/>) and metadata
    /// (<see cref="ContributionMetadataEntity"/>) under one identity.
    /// </summary>
    public class MappingEntity
    {
        /// <summary>BLOB(16) primary key (stored as a true 16-byte binary Guid).</summary>
        public Guid Id { get; set; } = Guid.NewGuid();
    }
}