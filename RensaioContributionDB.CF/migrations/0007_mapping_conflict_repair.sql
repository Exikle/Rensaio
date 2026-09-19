-- 0007_mapping_conflict_repair.sql
-- Rensaio Contribution Database — support the mapping-conflict repair rules shipped in
-- upload-service.ts:
--
--   1. metadata: a re-keyed provider link archives the stale (mapping_id, provider_id,
--      provider_key <> new) active row so the corrected row is the only active one.
--   2. mapping_titles: a title may belong to exactly ONE mapping; uploading an association
--      archives the title's other active mapping associations.
--
-- Both statements target rows by (mapping_id, provider_id) / title_id, so we add the
-- supporting indexes to keep the UPDATEs (which run on hot paths during upload/export) cheap.

-- Single-ownership: "release title from all OTHER mappings" scans mapping_titles by title_id.
CREATE INDEX IF NOT EXISTS idx_mapping_titles_title_id ON mapping_titles(title_id);

-- Metadata replace: "archive stale keys for this (mapping_id, provider_id)" scans by the pair.
CREATE INDEX IF NOT EXISTS idx_metadata_mapping_provider
  ON metadata(mapping_id, provider_id)
  WHERE archived_at IS NULL;