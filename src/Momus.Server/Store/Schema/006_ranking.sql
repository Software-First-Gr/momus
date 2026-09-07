-- What decides the order of the "Fix first" cards: severity, weighted by how much of the
-- application's traffic actually hits the subject, weighted by how recently it started.
--
-- Stored rather than computed on read, because the numbers behind it are the snapshot the rules
-- saw. Recomputed on every evaluation, so it is never more than one tick stale.

ALTER TABLE insights ADD COLUMN score REAL NOT NULL DEFAULT 0;

CREATE INDEX ix_insights_score ON insights (target_id, score DESC);
