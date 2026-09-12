-- 0003_replication_and_new_model.sql
-- Rensaio Contribution Database — refactor to the ContributionSnapshotV1 entity model.
--
--  * adds the `replication` singleton (Replication Version Number, bumped daily)
--  * adds `mappings` and `mapping_titles` (MappingEntity / MappingTitleEntity)
--  * replaces `sources` with the canonical ContributionSourceEntity shape (clean wipe)
--  * adds `series` (ContributionSeriesEntity with ContributionRecordV1 flattened)
--  * reshapes `metadata` to the ContributionMetadataEntity shape (clean wipe)
--  * removes `content_hash` (skip-if-match) columns — the backend diffs before upload
--
-- Ownership: `series`/`metadata` keep `contributor_id` (stamped server-side from the
-- authenticated uploader) so the existing ban → scrub flow keeps working. Ownership is
-- never exported and is not part of the wire format.

-- ── 1. Replication version singleton ──
CREATE TABLE IF NOT EXISTS replication (
  id        INTEGER PRIMARY KEY CHECK (id = 1),  -- singleton row
  version   INTEGER NOT NULL DEFAULT 0,
  bumped_at TEXT NOT NULL DEFAULT (datetime('now'))
);

INSERT OR IGNORE INTO replication (id, version) VALUES (1, 0);

-- ── 2. Mappings aggregate ──
CREATE TABLE IF NOT EXISTS mappings (
  id TEXT PRIMARY KEY NOT NULL   -- UUID/Guid
);

-- ── 3. Mapping ↔ title join ──
CREATE TABLE IF NOT EXISTS mapping_titles (
  mapping_id  TEXT NOT NULL,
  title_id    TEXT NOT NULL,
  last_change TEXT NOT NULL DEFAULT (datetime('now')),
  archived_at TEXT,
  PRIMARY KEY (mapping_id, title_id),
  FOREIGN KEY (mapping_id) REFERENCES mappings(id),
  FOREIGN KEY (title_id)   REFERENCES titles(id)
);

CREATE INDEX IF NOT EXISTS idx_mapping_titles_title_id ON mapping_titles(title_id);

-- ── 4. Canonical sources (ContributionSourceEntity) ──
-- Clean wipe: the old contributor-provided `id` + `data` BLOB layout is gone.
DROP TABLE IF EXISTS sources;

CREATE TABLE sources (
  id              TEXT PRIMARY KEY NOT NULL,  -- MD5("package:sourceId") as text Guid
  package         TEXT NOT NULL,
  source_id       INTEGER NOT NULL,
  source_name     TEXT NOT NULL DEFAULT '',
  source_language TEXT NOT NULL DEFAULT '',
  last_batch_utc  TEXT,
  archived_at     TEXT
);

CREATE INDEX IF NOT EXISTS idx_sources_source_id ON sources(source_id);
CREATE INDEX IF NOT EXISTS idx_sources_archived ON sources(archived_at);

-- ── 5. Per-series source entries (ContributionSeriesEntity) ──
-- ContributionRecordV1 is flattened into columns so the export never re-serializes JSON.
CREATE TABLE IF NOT EXISTS series (
  id              TEXT PRIMARY KEY NOT NULL,   -- MD5("package:sourceId:url") as text Guid
  mapping_id      TEXT NOT NULL,
  source_id       TEXT NOT NULL,               -- FK → sources.id
  title_id        TEXT NOT NULL,               -- FK → titles.id (ContributionRecordV1.TitleId)
  thumbnail_url   TEXT,
  status          INTEGER NOT NULL DEFAULT 0,
  seen_in_popular INTEGER NOT NULL DEFAULT 0,
  seen_in_latest  INTEGER NOT NULL DEFAULT 0,
  last_chapter    REAL,
  last_update_utc TEXT,
  contributor_id  TEXT,                        -- stamped server-side from the uploader
  archived_at     TEXT,
  FOREIGN KEY (mapping_id) REFERENCES mappings(id),
  FOREIGN KEY (source_id)  REFERENCES sources(id),
  FOREIGN KEY (title_id)   REFERENCES titles(id)
);

CREATE INDEX IF NOT EXISTS idx_series_mapping_id ON series(mapping_id);
CREATE INDEX IF NOT EXISTS idx_series_source_id  ON series(source_id);
CREATE INDEX IF NOT EXISTS idx_series_title_id   ON series(title_id);
CREATE INDEX IF NOT EXISTS idx_series_archived   ON series(archived_at);
CREATE INDEX IF NOT EXISTS idx_series_contributor_id ON series(contributor_id);

-- ── 6. Metadata links (ContributionMetadataEntity) ──
-- Clean wipe: identity is now the client-generated `id`, not
-- (title_id + metadata_provider + metadata_provider_key).
DROP TABLE IF EXISTS metadata;

CREATE TABLE metadata (
  id             TEXT PRIMARY KEY NOT NULL,     -- client-generated UUID
  mapping_id     TEXT NOT NULL,                 -- FK → mappings.id
  provider_id    INTEGER NOT NULL,              -- ExternalSeriesProvider enum (0..14)
  provider_key   TEXT,
  mapping_status INTEGER NOT NULL DEFAULT 0,    -- SeriesMappingStatus enum (0..5)
  linked_date    TEXT,
  contributor_id TEXT,                          -- stamped server-side from the uploader
  archived_at    TEXT,
  FOREIGN KEY (mapping_id) REFERENCES mappings(id)
);

CREATE INDEX IF NOT EXISTS idx_metadata_mapping_id ON metadata(mapping_id);
CREATE INDEX IF NOT EXISTS idx_metadata_provider   ON metadata(provider_id, provider_key);
CREATE INDEX IF NOT EXISTS idx_metadata_archived   ON metadata(archived_at);
CREATE INDEX IF NOT EXISTS idx_metadata_contributor_id ON metadata(contributor_id);