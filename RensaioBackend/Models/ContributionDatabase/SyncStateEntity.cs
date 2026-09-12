namespace RensaioBackend.Models.ContributionDatabase
{
    /// <summary>
    /// Replication cursor for the local contributor database. Tracks the last server
    /// version processed so incremental syncs can resume from the correct point.
    /// </summary>
    public class SyncStateEntity
    {
        /// <summary>INTEGER primary key (single-row table — use Id == 1 as the canonical row).</summary>
        public int Id { get; set; }

        /// <summary>Last server version applied locally (null = never synced).</summary>
        public int LastServerVersion { get; set; }
    }
}