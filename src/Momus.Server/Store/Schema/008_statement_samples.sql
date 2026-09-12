-- The database's statement counters, as they stood at each scan.
--
-- pg_stat_statements and dm_exec_query_stats only ever add up, from the last time somebody reset
-- them. Ranked as they are, a seed INSERT that ran once five days ago stays "#3 by total time"
-- until the view is reset, while the app side is talking about the last hour. Keeping every
-- statement's counters per scan lets the server rank on the difference between two scans instead.
--
-- Narrow on purpose: one scan writes up to five hundred of these, so they carry the two numbers a
-- difference needs and nothing else. The text and the severity stay on the finding.

CREATE TABLE statement_samples (
    scan_id     INTEGER NOT NULL REFERENCES scans(id) ON DELETE CASCADE,
    target_id   TEXT NOT NULL,
    fingerprint TEXT NOT NULL,
    calls       INTEGER NOT NULL,
    total_ms    REAL NOT NULL,
    PRIMARY KEY (scan_id, fingerprint)
);

CREATE INDEX ix_statement_samples_target ON statement_samples (target_id, scan_id);
