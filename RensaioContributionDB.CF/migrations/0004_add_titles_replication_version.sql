-- 0004_add_titles_replication_version.sql
-- Rensaio Contribution Database — add the `replication_version` column to `titles`.
--
-- The ContributionSnapshotV1 entity model carries a replication version per row
-- (`v`: 0 = add/update, -1 = tombstone). Upload and export code already read and
-- write `titles.replication_version` (see upload-service.ts / export-service.ts),
-- but migration 0003 never added the column. Without it every D1 batch that
-- touches titles fails with "no such column: replication_version".

-- 1. Add the column (nullable so existing active rows can be backfilled).
ALTER TABLE titles ADD COLUMN replication_version INTEGER;

-- 2. Backfill existing rows as "add/update" so exports carry v=0 and upload
--    upserts can resurrect/interact with them normally.
UPDATE titles SET replication_version = 0 WHERE replication_version IS NULL;

-- 3. Existing rows are considered part of the baseline snapshot — nothing
--    else to do. New rows are stamped by the worker on upload.