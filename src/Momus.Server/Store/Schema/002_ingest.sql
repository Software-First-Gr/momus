-- M2: the application's half. Everything here arrives over POST /api/v1/ingest as aggregates —
-- no literal, no parameter value, no result row — and is joined to the scan tables by one thing:
-- the SqlFingerprint key, which both sides compute from the same statement.

CREATE TABLE apps (
    id           TEXT PRIMARY KEY,
    name         TEXT NOT NULL,
    environment  TEXT,
    -- The newest version seen. A change here is a deploy, which is what M3 hangs regressions on.
    last_version TEXT,
    first_seen   TEXT NOT NULL,
    last_seen    TEXT NOT NULL
);

-- One row per process per version, so two replicas stay distinguishable and the first sighting
-- of a version is a date rather than a guess.
CREATE TABLE app_instances (
    app_id     TEXT NOT NULL REFERENCES apps(id) ON DELETE CASCADE,
    instance   TEXT NOT NULL,
    version    TEXT NOT NULL DEFAULT '',
    first_seen TEXT NOT NULL,
    last_seen  TEXT NOT NULL,
    PRIMARY KEY (app_id, instance, version)
);

-- A few seconds of one app's life as the client posted it, or an hour of it after the rollup.
CREATE TABLE windows (
    id          INTEGER PRIMARY KEY AUTOINCREMENT,
    app_id      TEXT NOT NULL REFERENCES apps(id) ON DELETE CASCADE,
    instance    TEXT,
    version     TEXT,
    -- raw: as posted, kept 24 h. hourly: rolled up, kept 7 days.
    kind        TEXT NOT NULL DEFAULT 'raw',
    from_at     TEXT NOT NULL,
    to_at       TEXT NOT NULL,
    -- Executions the client could not attribute because the window hit its key limit.
    overflow    INTEGER NOT NULL DEFAULT 0,
    received_at TEXT NOT NULL
);

CREATE INDEX ix_windows_app_to ON windows (app_id, to_at DESC);
CREATE INDEX ix_windows_kind_to ON windows (kind, to_at);

-- One statement, as one operation ran it, over one window. The fingerprint is the join.
CREATE TABLE query_stats (
    id           INTEGER PRIMARY KEY AUTOINCREMENT,
    window_id    INTEGER NOT NULL REFERENCES windows(id) ON DELETE CASCADE,
    fingerprint  TEXT NOT NULL,
    target_id    TEXT NOT NULL DEFAULT '',
    operation    TEXT NOT NULL,
    call_site    TEXT NOT NULL DEFAULT '',
    calls        INTEGER NOT NULL,
    duration_sum REAL NOT NULL,
    duration_max REAL NOT NULL,
    -- Eight log2 buckets as a JSON array, so a rollup can sum them bucket-wise in SQL.
    hist         TEXT NOT NULL DEFAULT '[0,0,0,0,0,0,0,0]',
    rows_read    INTEGER NOT NULL DEFAULT 0,
    -- The N in N+1: how often this one statement ran inside a single operation, at worst.
    max_repeats  INTEGER NOT NULL DEFAULT 0,
    errors       INTEGER NOT NULL DEFAULT 0
);

CREATE INDEX ix_query_stats_window ON query_stats (window_id);
CREATE INDEX ix_query_stats_fingerprint ON query_stats (fingerprint);

CREATE TABLE operation_stats (
    id           INTEGER PRIMARY KEY AUTOINCREMENT,
    window_id    INTEGER NOT NULL REFERENCES windows(id) ON DELETE CASCADE,
    name         TEXT NOT NULL,
    kind         TEXT NOT NULL,
    calls        INTEGER NOT NULL,
    duration_sum REAL NOT NULL,
    duration_max REAL NOT NULL,
    query_sum    INTEGER NOT NULL DEFAULT 0,
    query_max    INTEGER NOT NULL DEFAULT 0,
    db_ms        REAL NOT NULL DEFAULT 0
);

CREATE INDEX ix_operation_stats_window ON operation_stats (window_id);

-- The normalized text behind a fingerprint, kept once rather than on every window row.
CREATE TABLE query_texts (
    fingerprint TEXT NOT NULL,
    target_id   TEXT NOT NULL DEFAULT '',
    sample      TEXT NOT NULL,
    first_seen  TEXT NOT NULL,
    last_seen   TEXT NOT NULL,
    PRIMARY KEY (fingerprint, target_id)
);
