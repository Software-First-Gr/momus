-- M3: the two signals that are not about a single statement.
--
-- A transaction held open across work that is not database work makes every other session queue
-- behind its locks, and a pool with nothing left in it is usually the same problem seen one step
-- later. Both are invisible to the database's own views as anything but a symptom — idle in
-- transaction, connection saturation — which is exactly why the app side has to send them.

CREATE TABLE transaction_stats (
    id        INTEGER PRIMARY KEY AUTOINCREMENT,
    window_id INTEGER NOT NULL REFERENCES windows(id) ON DELETE CASCADE,
    operation TEXT NOT NULL,
    call_site TEXT NOT NULL DEFAULT '',
    txn_count INTEGER NOT NULL,
    open_sum  REAL NOT NULL,
    open_max  REAL NOT NULL,
    -- Eight log2 buckets as JSON, so a p95 survives the hourly rollup.
    open_hist TEXT NOT NULL DEFAULT '[0,0,0,0,0,0,0,0]',
    db_sum    REAL NOT NULL DEFAULT 0,
    db_max    REAL NOT NULL DEFAULT 0
);

CREATE INDEX ix_transaction_stats_window ON transaction_stats (window_id);

CREATE TABLE pool_stats (
    id        INTEGER PRIMARY KEY AUTOINCREMENT,
    window_id INTEGER NOT NULL REFERENCES windows(id) ON DELETE CASCADE,
    operation TEXT NOT NULL,
    waits     INTEGER NOT NULL,
    wait_sum  REAL NOT NULL,
    wait_max  REAL NOT NULL,
    wait_hist TEXT NOT NULL DEFAULT '[0,0,0,0,0,0,0,0]'
);

CREATE INDEX ix_pool_stats_window ON pool_stats (window_id);
