-- M1: what a scan produced, and how long each finding has been true.
--
-- Everything here is written by the scheduler and read by the pages. There are no
-- application-side tables yet: those arrive with the ingest endpoint in M2.

CREATE TABLE targets (
    id                TEXT PRIMARY KEY,
    name              TEXT NOT NULL,
    provider          TEXT NOT NULL,
    connection_string TEXT NOT NULL,
    -- Where this target came from: env, cli, ui. The client's hello joins them in M2.
    source            TEXT NOT NULL,
    database_name     TEXT,
    server_version    TEXT,
    created_at        TEXT NOT NULL,
    last_scan_at      TEXT,
    last_error        TEXT
);

CREATE TABLE scans (
    id            INTEGER PRIMARY KEY AUTOINCREMENT,
    target_id     TEXT NOT NULL REFERENCES targets(id) ON DELETE CASCADE,
    started_at    TEXT NOT NULL,
    duration_ms   REAL NOT NULL,
    succeeded     INTEGER NOT NULL,
    error         TEXT,
    server_version TEXT,
    database_name TEXT
);

CREATE INDEX ix_scans_target_started ON scans (target_id, started_at DESC);

CREATE TABLE check_results (
    id          INTEGER PRIMARY KEY AUTOINCREMENT,
    scan_id     INTEGER NOT NULL REFERENCES scans(id) ON DELETE CASCADE,
    check_id    TEXT NOT NULL,
    title       TEXT NOT NULL,
    category    TEXT NOT NULL,
    succeeded   INTEGER NOT NULL,
    duration_ms REAL NOT NULL,
    error       TEXT
);

CREATE INDEX ix_check_results_scan ON check_results (scan_id);

CREATE TABLE findings (
    id             INTEGER PRIMARY KEY AUTOINCREMENT,
    scan_id        INTEGER NOT NULL REFERENCES scans(id) ON DELETE CASCADE,
    target_id      TEXT NOT NULL,
    check_id       TEXT NOT NULL,
    -- check id + its sorted subjects: what makes this "the same finding" as last time.
    identity_key   TEXT NOT NULL,
    severity       INTEGER NOT NULL,
    title          TEXT NOT NULL,
    detail         TEXT NOT NULL,
    recommendation TEXT,
    evidence       TEXT NOT NULL,
    created_at     TEXT NOT NULL
);

CREATE INDEX ix_findings_scan ON findings (scan_id);
CREATE INDEX ix_findings_identity ON findings (target_id, identity_key);

CREATE TABLE finding_subjects (
    finding_id INTEGER NOT NULL REFERENCES findings(id) ON DELETE CASCADE,
    kind       TEXT NOT NULL,
    key        TEXT NOT NULL,
    PRIMARY KEY (finding_id, kind, key)
);

-- The join the insight engine will use in M2: "what does the database say about query:abc123".
CREATE INDEX ix_finding_subjects_lookup ON finding_subjects (kind, key);

-- One row per distinct finding per target, for as long as it keeps being true.
-- This is the whole point of the server: the CLI can tell you what is wrong,
-- only a thing with memory can tell you since when.
CREATE TABLE finding_state (
    target_id       TEXT NOT NULL,
    identity_key    TEXT NOT NULL,
    check_id        TEXT NOT NULL,
    category        TEXT NOT NULL,
    severity        INTEGER NOT NULL,
    title           TEXT NOT NULL,
    subjects        TEXT NOT NULL,
    first_seen      TEXT NOT NULL,
    last_seen       TEXT NOT NULL,
    seen_count      INTEGER NOT NULL DEFAULT 1,
    -- open | muted | fixed. Mute and Fixed are user actions; M3 adds them to the UI.
    status          TEXT NOT NULL DEFAULT 'open',
    last_finding_id INTEGER,
    PRIMARY KEY (target_id, identity_key)
);

CREATE INDEX ix_finding_state_last_seen ON finding_state (target_id, last_seen DESC);
