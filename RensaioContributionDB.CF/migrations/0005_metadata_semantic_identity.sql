-- 0005_metadata_semantic_identity.sql
-- Rensaio Contribution Database — semantic identity for metadata links.
--
-- Problem: `metadata.id` and `mappings.id` were client-generated random GUIDs.
-- Two contributors uploading the same association produced duplicate rows (the
-- random PK never collided) and the id-based upsert could not dedup.
--
-- Fix: identity becomes the semantic triple (mapping_id, provider_id, provider_key).
-- The client-generated `id` is demoted to an internal identifier — on conflict it is
-- kept (never overwritten by the uploader). Mappings are reconciled on upload by
-- title-overlap (see upload-service.ts), so distinct client mapping uuids collapse
-- into the existing cloud mapping when their title sets overlap.

-- 1. Normalize provider_key: NULL and '' must be the SAME key for the unique
--    index to work (SQLite treats NULLs as distinct in unique indexes).
UPDATE metadata SET provider_key = '' WHERE provider_key IS NULL;

-- 2. Deduplicate pre-existing active metadata rows by the semantic triple
--    (keep the NEWEST; archive the rest — last upload wins, mirroring 0002).
UPDATE metadata
SET archived_at = COALESCE(archived_at, datetime('now'))
WHERE archived_at IS NULL
  AND rowid NOT IN (
    SELECT MAX(rowid) FROM metadata
    GROUP BY mapping_id, provider_id, provider_key
  );

-- 3. Enforce at-most-one ACTIVE row per (mapping_id, provider_id, provider_key).
--    Partial so archived rows can be re-created after reconciliation (same pattern
--    as idx_titles_title_active). Note: (provider, '') and (provider, '123') are
--    different keys — Unmatched/Blocked/Confirmed rows legitimately coexist.
CREATE UNIQUE INDEX idx_metadata_identity_active
  ON metadata(mapping_id, provider_id, provider_key)
  WHERE archived_at IS NULL;