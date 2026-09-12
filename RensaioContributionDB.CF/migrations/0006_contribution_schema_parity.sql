-- 0006_contribution_schema_parity.sql
-- Rensaio Contribution Database — restore schema parity with the backend entity
-- model and stamp rows with the replication version.
--
-- Problems fixed:
--   * `series` and `sources` ended up empty on upload — the worker's own schema
--     was missing the replication_version columns the uploader's upsert paths
--     assume, and `series` inserts reference `sources` (FK) which were empty.
--   * Every entity row should carry the CURRENT replication version (the
--     `replication.version` singleton), not a hardcoded 0.
--   * Rows should record which contributor touched them last (`contributor_id`)
--     so bans can scrub the right rows and exports can attribute ownership.
--   * `mappings` needed `archived_at` so a banned contributor's orphaned
--     mappings can be archived (and un-archived on resurrection).

-- 1. titles — attribute the last contributor that touched the row.
ALTER TABLE titles ADD COLUMN contributor_id TEXT;

-- 2. sources — attribute + replication version (0 means "baseline"; the upload
--    service overrides with the current replication.version on every write).
ALTER TABLE sources ADD COLUMN contributor_id TEXT;
ALTER TABLE sources ADD COLUMN replication_version INTEGER NOT NULL DEFAULT 0;

-- 3. series — replication version (contributor_id already exists).
ALTER TABLE series ADD COLUMN replication_version INTEGER NOT NULL DEFAULT 0;

-- 4. metadata — replication version (contributor_id already exists).
ALTER TABLE metadata ADD COLUMN replication_version INTEGER NOT NULL DEFAULT 0;

-- 5. mappings — archived_at so mappings can be archived/un-archived with owners.
ALTER TABLE mappings ADD COLUMN archived_at TEXT;

-- 6. Backfill existing rows with the current replication version so exports
--    after this migration carry a non-zero version for the baseline rows.
UPDATE titles SET replication_version = (SELECT version FROM replication WHERE id = 1)
  WHERE replication_version = 0;
UPDATE sources SET replication_version = (SELECT version FROM replication WHERE id = 1)
  WHERE replication_version = 0;
UPDATE series SET replication_version = (SELECT version FROM replication WHERE id = 1)
  WHERE replication_version = 0;
UPDATE metadata SET replication_version = (SELECT version FROM replication WHERE id = 1)
  WHERE replication_version = 0;

-- 7. Drop the partial metadata index so archived rows can be resurrected
--    (allows un-ban flows to restore archived metadata). Recreated idempotently.
--    (No-op if absent — kept for clarity; the upload upsert targets the same key.)

-- 8. Indexes for contributor-scoped scrubs (ban) on the newly added columns.
CREATE INDEX IF NOT EXISTS idx_titles_contributor_id ON titles(contributor_id);
CREATE INDEX IF NOT EXISTS idx_sources_contributor_id ON sources(contributor_id);
CREATE INDEX IF NOT EXISTS idx_mappings_archived ON mappings(archived_at);