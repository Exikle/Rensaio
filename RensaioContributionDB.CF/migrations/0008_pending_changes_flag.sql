-- 0008_pending_changes_flag.sql
-- Rensaio Contribution Database — only export when actual changes exist.
--
-- The daily cron (and the manual /admin/export) used to bump the Replication
-- Version Number and push metadata.bin every day regardless of whether any
-- contributor uploaded changes that day. This produced pointless versions and
-- redundant exports.
--
-- This migration adds a `pending_changes` flag to the existing `replication`
-- singleton (id = 1):
--   * set to 1 whenever a contributor upload applies rows (or an admin
--     mutation archives data), and
--   * cleared to 0 by the export step (cron or manual) once a new version +
--     metadata.bin have been published.
--
-- The export only bumps the version and publishes when pending_changes = 1;
-- otherwise both the cron and the manual endpoint become cheap no-ops.

ALTER TABLE replication ADD COLUMN pending_changes INTEGER NOT NULL DEFAULT 0 CHECK (pending_changes IN (0, 1));

-- Existing row defaults to 0 (today's version already exists / nothing pending).
UPDATE replication SET pending_changes = 0 WHERE id = 1;