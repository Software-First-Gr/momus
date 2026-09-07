-- M2.4: what both halves mean together.
--
-- Upserted by kind + subjects, never by title: a title carries live numbers and is recomputed
-- every evaluation, so keying on it would make every insight look new every fifteen seconds.
-- The lifetime columns are the same idea as finding_state, for the same reason — the useful
-- question is "since when", and only something with memory can answer it.

CREATE TABLE insights (
    target_id      TEXT NOT NULL,
    kind           TEXT NOT NULL,
    -- kind + sorted subjects, from Insight.IdentityKey.
    identity_key   TEXT NOT NULL,
    severity       INTEGER NOT NULL,
    title          TEXT NOT NULL,
    detail         TEXT NOT NULL,
    recommendation TEXT,
    evidence       TEXT NOT NULL,
    subjects       TEXT NOT NULL,
    first_seen     TEXT NOT NULL,
    last_seen      TEXT NOT NULL,
    seen_count     INTEGER NOT NULL DEFAULT 1,
    -- open | muted | fixed. Mute and Fixed get their UI in M3; a fixed insight that fires
    -- again reopens itself, exactly as a finding does.
    status         TEXT NOT NULL DEFAULT 'open',
    PRIMARY KEY (target_id, identity_key)
);

CREATE INDEX ix_insights_last_seen ON insights (target_id, last_seen DESC, severity DESC);
