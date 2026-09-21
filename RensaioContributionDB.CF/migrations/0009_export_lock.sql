-- 0009_export_lock.sql
-- Rensaio Contribution Database — single-slot export lock.
--
-- Serializes concurrent export runs (the 06:00 cron and a manual
-- /admin/export can overlap, and Cloudflare may double-fire a cron). Without a
-- durable lock two concurrent runDailyExport executions can:
--   1. double-bump the Replication Version Number (8->9->10... while nothing
--      is actually pushed),
--   2. race clearPendingChanges (one run clears the flag while the other is
--      mid-export, silently losing the pending state), and
--   3. both push to GitHub with a stale `sha` -> 409 conflicts.
--
-- acquireExportLock does `INSERT OR IGNORE INTO export_lock (id=1)`: changes=1
-- means THIS invocation won the slot; changes=0 means another run is in
-- progress (skip). The winner DELETE's the row in a finally block.
CREATE TABLE IF NOT EXISTS export_lock (
  id           INTEGER PRIMARY KEY CHECK (id = 1),  -- singleton slot
  acquired_utc TEXT NOT NULL DEFAULT (datetime('now'))
);