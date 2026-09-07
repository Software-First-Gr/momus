# Momus implementation plan

Living document. Any session, on any machine, should be able to open this file and
continue. Rules: tick a task when it is merged to `develop` and tested; add a row to
Decisions when something is decided; add to Open questions when something is not.
The design behind all of this is `docs/DESIGN.md`.

## How to resume a session

1. Read `CLAUDE.md`, then this file's Status and Decisions.
2. Find the first unticked task in the current milestone. Tasks within a milestone are ordered; earlier ones unblock later ones.
3. Work on `develop`. Run `dotnet test` before committing. Tick the task in this file in the same commit.
4. Do not publish (`v*` tag) unless the milestone's release row says so.

## Status

| Date | State |
| --- | --- |
| 2026-09-06 | Collector core 0.x exists (CLI `scan`, Postgres + SQL Server checks, 16 tests). Packages `Momus`, `Momus.Core`, `Momus.Postgres`, `Momus.SqlServer` published as 0.0.1 to claim the ids. Repo public. CI: build/test/pack on push, publish on `v*` tags. M1 not started. |
| 2026-09-07 | **M1.1–M1.6 done, plus the demo stack (M1.8).** `momus serve` scans on a schedule, keeps history in SQLite and shows findings with first-seen dates; `docker compose up` brings up Postgres + Momus + a flawed demo shop and the whole loop works end to end on this machine. 110 tests. Remaining for the `v0.1.0` release: run it beside a real database for a week (M1's "done when"), decide D3, and the housekeeping list. Nothing is published yet — no tag, no image pushed. |

## Decisions

| Id | Date | Decision |
| --- | --- | --- |
| D1 | 2026-09-06 | App-side capture lives in a new package `Momus.Client`, never inside Switchboard. Switchboard stays a few hundred lines with one dependency; an optional `Momus.Client.Switchboard` adapter (one pipeline behavior) lives in this repo later. |
| D2 | 2026-09-06 | One repository, one version number, one `v*` tag publishes every NuGet package, the dotnet tool and the Docker image together. Split only if release cadences diverge. |
| D3 | 2026-09-06 | **Open.** Libraries (`Momus.Core`, providers, `Momus.Client`) are Apache-2.0. Proposed: the executable (`Momus` tool, which will contain the server) moves to FSL-1.1-ALv2 when the server ships. Alternative: Apache-2.0 for everything. Decide before the M1 release. |
| D4 | 2026-09-06 | The server's store is SQLite on a volume. No second database in the image. |
| D5 | 2026-09-06 | Dev loop first. Client is on by default only in Development. No OTLP ingestion, no Dapper/raw ADO.NET capture, no MySQL, no hosted version in 1.0. |
| D6 | 2026-09-06 | Product rule: a feature that only restates what the database already says ranks below any feature that joins app side and DB side. The three 1.0 insights are N+1 with real cost, expensive query mapped to code, regression since deploy. |
| D7 | 2026-09-06 | Web UI is server-rendered Razor with htmx, inline SVG sparklines, no Node toolchain. |
| D8 | 2026-09-06 | Registry: GHCR (`ghcr.io/software-first-gr/momus`), `edge` from `develop`, semver + `latest` from tags, linux/amd64 + linux/arm64. Docker Hub `momus` namespace is taken; a mirror is optional later. |
| D9 | 2026-09-06 | Repo public since 2026-09-06. Quiet until M1 runs end to end; launch post after M4. |
| D10 | 2026-09-06 | Money: free tier never gates checks, insights, MCP or CLI. Pro (self-hosted, offline license key, per server) gates retention, multiple apps/targets, alerts, login, white-label report. Billing is built only after strangers use the free tier daily (M5, not before). |
| D11 | 2026-09-06 | Package ids stay product-first: `Momus`, `Momus.Core`, `Momus.Postgres`, `Momus.SqlServer`, later `Momus.Client`. No `SoftwareFirst.` prefix, unlike `SoftwareFirst.Switchboard` (where the bare id was free and prefixing was a choice). Momus is a product, not a company utility: it owns a CLI command (`dotnet tool install -g Momus` -> `momus`, an id/command split a prefix would force), a Docker image and a multi-package family where `SoftwareFirst.Momus.Client.Switchboard` is a cost Switchboard never paid. Namespace protection comes from an ID prefix reservation instead, not from the id. |

## Open questions

- **EF Core auto-registration of interceptors without user code.** Candidates: register `IInterceptor` implementations in the app's DI (EF Core resolves them from the application service provider) or `IDbContextOptionsConfiguration<TContext>` / `ConfigureDbContext` (EF Core 8+). Verify on EF Core 8, 9 and 10 before building M2.1.
- ~~**Fingerprint edge cases**~~ **Answered in M1.2 by real pairs.** Three things actually differ, and all three are now handled: (1) EF Core appends a trailing `;` that the statistics views do not keep; (2) EF batches several statements into one command while the database records each separately, so joining is per statement — hence `SqlFingerprint.Split`; (3) **SQL Server's simple parameterization rewrites the statement before caching it**, turning `FROM [t] AS [a]` into `FROM [t] [a]` and a literal into `@1`, so the optional alias `AS` must be dropped on both sides. Aliases, `IN` lists, `VALUES` batches, tag comments and pagination parameters needed no special handling. Re-run `tools/FingerprintCapture` against a new EF or engine version to check this still holds.
- **Ranking weights** for "Fix first" (severity × traffic share × recency). Start simple, tune on real data (M3).
- **Pro price point.** Anchor around a consultant hour; decide at M5 with real users.
- **Branch policy.** `main` still sits at the initial commit. Either merge `develop` into `main` at each release, or make `develop` the default branch. Recommended: merge at each release, so `main` always equals the last published tag.

## Housekeeping (before or during M1)

- [ ] Decide branch policy (see Open questions) and apply it.
- [ ] Add CI and NuGet badges to `README.md` like Switchboard's.
- [ ] Decide D3 (license of the executable).
- [ ] Add an embedded `PackageIcon` to `Directory.Build.props` (Switchboard has one since `6828783`). NuGet lists it as a best practice reviewers check for prefix reservation.
- [ ] At the M1 release, once the packages carry real content, mail `account@nuget.org` from owner `nifragos` requesting **both** reservations in one application: `Momus.` (the prefix and the exact id `Momus`) and `SoftwareFirst.` (covers `SoftwareFirst.Switchboard`), with a link to the repo. Gives the verified badge and blocks strangers from publishing `Momus.*`. `SoftwareFirst.` is a near-certain grant, matching the author and org metadata; `Momus.` is a maybe, since the criteria say to avoid common or generic words and Momus is a dictionary word - so asking for both costs nothing. See D11.

---

## M1 — Serve and remember

**Goal.** `momus serve` runs beside a database, scans it on a schedule, keeps every scan in SQLite, and shows findings with first/last seen on one page. Docker image on GHCR. This alone beats the CLI for daily use because it has memory.

**Done when.** It has run beside your own database for a week and told you something the CLI could not: a finding and the day it first appeared. Release `v0.1.0`.

### M1.1 Subjects on findings (Core)

- [x] Add `Subject` record (`Kind`, `Key`) and `IReadOnlyList<Subject> Subjects` (default empty) to `src/Momus.Core/Finding.cs`. Kinds: `query`, `table`, `index`, `session`, `database`, `server`.
- [x] Every existing check sets at least one subject: `pg.top_queries` → `query:<key>`; `pg.seq_scan_heavy_tables`, `pg.dead_tuples` → `table:<schema.name>`; `pg.unused_indexes` → `index:<name>` and `table:`; `pg.problem_sessions` → `session:<pid>`; `pg.cache_hit_ratio`, `pg.connection_saturation` → `server`; `mssql.top_cpu_queries` → `query:<key>`; `mssql.missing_indexes` → `table:`; `mssql.blocking` → `session:<spid>` for both ends of the chain; `mssql.wait_stats`, `mssql.memory_pressure` → `server`.
- [x] `JsonReport` emits subjects as one string each (`table:public.orders`), via `SubjectJsonConverter`. `ConsoleReport` unchanged. JSON stays backward compatible (additive field).
- [x] Tests: a test per provider asserting every finding produced from fixture rows carries at least one subject (`FakeDataConnection` scripts the statistics views).

### M1.2 SQL fingerprint (Core)

- [x] `src/Momus.Core/SqlFingerprint.cs`: `Normalize(string sql)` and `Compute(string sql)` returning the first 16 hex chars of SHA-256 of the normalized text. Steps: strip comments (remember an EF `TagWith` comment as call site), collapse whitespace, replace numeric and quoted literals with `?`, replace parameter markers (`@p0`, `$1`, `:p1`) with `?`, collapse `IN (?, ?, ?)` and repeated `VALUES (...)` groups, lowercase keywords only, keep identifier case. Also drops the optional alias `AS` and splits multi-statement commands (`Split`); see Open questions for why.
- [x] `tests/Momus.Tests/SqlFingerprintTests.cs` with fixture pairs: 21 Postgres and 21 SQL Server pairs in `tests/Momus.Tests/Fixtures/`, every one captured from a running server by `tools/FingerprintCapture` (a new throwaway-schema harness that runs 21 EF Core query shapes and reads back what the engine recorded for each). Re-run it to extend or refresh them.
- [x] `TopQueriesCheck` and `TopCpuQueriesCheck`: compute the key from the stats-view text, add `query:<key>` subject, add `queryid` (PG) / `query_hash` (MSSQL) to Evidence, take a `limit` constructor parameter (default 5; the server will pass 50). Both now select the full statement text for hashing and keep the truncated copy for display.

### M1.3 Server project and store

- [x] New project `src/Momus.Server` (Razor class library with `<FrameworkReference Include="Microsoft.AspNetCore.App" />`), referenced by `Momus.Cli`. Entry point `MomusServer.RunAsync(ServerOptions, CancellationToken)`.
- [x] Store on `Microsoft.Data.Sqlite`, no ORM. Versioned schema scripts in `src/Momus.Server/Store/Schema/NNN_*.sql` (embedded resources) applied at startup, tracked in `schema_version`. M1 tables: `targets`, `scans`, `check_results`, `findings`, `finding_subjects`, `finding_state` (identity key = check id + sorted subjects; first_seen, last_seen, status open|muted|fixed). Writes are serialized through one semaphore: SQLite has one writer and a scan a minute has nothing to contend over.
- [x] `ScanScheduler : BackgroundService`: ticks every 2 s and scans each target that is due or has been requested, so a target added in the UI is scanned within seconds instead of at the end of the interval. Failures are logged, recorded on the target and never stop the loop.
- [x] Target sources: environment (`MOMUS_TARGETS__0__PROVIDER`, `__CONNECTIONSTRING`, `__NAME`), CLI flag `--target [name=]<provider>:<connection string>`, and the Settings page. When running in a container (`DOTNET_RUNNING_IN_CONTAINER=true`) and a target host is `localhost`, retry once with `host.docker.internal` on connection failure and remember which worked.
- [x] Tests: store round-trip of a `ScanReport`; `finding_state` first/last seen across two persisted scans; scheduler runs against `FakeConnection` (including a broken target that must not stop the others).

### M1.4 CLI `serve`

- [x] Refactor `src/Momus.Cli/Program.cs` into a small command dispatcher (`scan`, `serve`, later `report`, `mcp`). `scan` flags, output and exit codes stay byte-compatible.
- [x] `momus serve --data <dir> --port 4848 --scan-interval 60s [--target ...]`. Environment equivalents: `MOMUS_DATA`, `MOMUS_PORT`, `MOMUS_SCAN_INTERVAL`, `MOMUS_TARGETS__n__*`.

### M1.5 Web UI v0

- [x] Razor Pages in `src/Momus.Server/Pages`. Header: target and server version, last scan age (amber STALE pill past three intervals, red when the scan itself failed). Findings tab: grouped by category, severity pills, first-seen / seen-count chips, subject chips, evidence in a `<details>`, and the worst 8 per check with a line saying how many more are in the store. Settings: add and remove a target, and what this server is configured with.
- [x] Empty state: with no target at all it shows the three ways to add one (flag, environment, the form). The DESIGN.md empty state — "the DB half visibly alive while the app half is not yet" — needs the client, so it lands with M2.
- [x] Inline CSS, `prefers-color-scheme` dark mode, no JavaScript build step. Ten lines of vanilla JS refresh the page every 15 s, and skip it while a details panel is open or the tab is in the background. htmx when a partial refresh is actually needed.

### M1.6 Docker and CI

- [x] `src/Momus.Cli/Dockerfile` on `mcr.microsoft.com/dotnet/aspnet:10.0-alpine`, framework-dependent publish, `EXPOSE 4848`, `VOLUME /data`, `.dockerignore`. Entrypoint and command are split (`ENTRYPOINT dotnet Momus.Cli.dll` + `CMD serve`) so `docker run momus scan ...` works too; `MOMUS_DATA=/data` is baked in. 200 MB on arm64.
- [x] Workflow job: buildx multi-arch (linux/amd64, linux/arm64) push to `ghcr.io/software-first-gr/momus`; `edge` on `develop`, semver + `latest` on `v*` tags. Uses `GITHUB_TOKEN` with `packages: write`. The tag is passed as a `VERSION` build arg so the image reports its own version. **Untested until the first push.**
- [x] README: the `docker compose up` demo, the one-line `docker run`, and `momus serve` as a dotnet tool.

### M1.8 Demo stack (added during M1, was M2.1)

`samples/Shop.Api` was planned for M2, when it would have a client in it. It was built during M1
instead because M1 has no other way to produce a database worth looking at: findings, first-seen
dates and staleness all need a database under real, changing load.

- [x] `samples/Shop.Api`: ASP.NET Core + EF Core + Npgsql against a seeded schema (60k products, 40k orders, 240k order lines, 150k audit rows). Deliberate flaws: no index on `order_lines.order_id`, `LOWER(name) LIKE '%x%'` search, two never-read indexes, an N+1 order page.
- [x] Control panel at `:8080`: traffic off/light/heavy, one card per scenario saying what Momus should make of it, live `pg_stat_user_tables` counters, an activity log. Server-rendered, no Node.
- [x] Scenarios that take minutes (idle-in-transaction, a six-minute query, 30 held connections) run as background jobs with a countdown and a Stop button, because Postgres only calls them problems after five minutes.
- [x] Traffic goes through the app's own HTTP endpoints rather than straight to the database, so in M2 the operation names are real routes with no change to the sample.
- [x] `docker-compose.yml` + `docker/postgres.conf`: Postgres with `pg_stat_statements`, a deliberately small `shared_buffers` and `max_connections=40` so the checks have something to find. Host port 55432, so it never fights an existing local Postgres.

### M1.7 Release

- [ ] Restore descriptive package metadata is already in the repo; verify nuget.org listing after publish.
- [ ] Decide D3 and set the license expression for the `Momus` tool accordingly.
- [ ] Tag `v0.1.0`. Verify packages and image.

---

## M2 — The other side

**Goal.** `Momus.Client` reports operations, query stats and call sites; the server ingests them; the Queries tab shows both sides of every statement; the first two insights fire.

**Done when.** The top query in `pg_stat_statements` on your own project is shown with a file and line, and one N+1 you did not know about is found. Release `v0.2.0`.

### M2.1 Client project

- [ ] New project `src/Momus.Client`, multi-target `net8.0;net9.0;net10.0`, dependencies `Microsoft.EntityFrameworkCore.Relational` (floored per major like Switchboard) and the ASP.NET Core framework reference. Package id `Momus.Client`.
- [ ] `services.AddMomus()` with `MomusOptions` bound from `Momus:` configuration: `Enabled` (default: Development only), `Endpoint` (`http://localhost:4848`), `ShareConnectionStrings` (default: endpoint is loopback), `FlushSeconds` (5), `AppName` (entry assembly), `Environment`.
- [ ] Operation scope: `IStartupFilter` inserts middleware first; operation name = endpoint display name or route pattern; `AsyncLocal` scope; `Momus.Operation("name")` API for background work; fallback to `Activity.Current.DisplayName`.
- [ ] EF Core hooks: `DbCommandInterceptor` (reader / non-query / scalar executed and failed), registered without user code (resolve the open question first). Per execution: fingerprint via `SqlFingerprint`, duration, rows where available, operation, call site.
- [ ] Call site: first time a (fingerprint, operation) pair is seen in the process, walk the stack and keep the first frame outside `Microsoft.*` / `System.*`; cache forever. An EF `TagWithCallSite` tag wins when present.
- [ ] Aggregator: bounded dictionary (2,000 keys per window, overflow counter), per key: count, sum/max ms, 8-bucket log2 histogram from 1 ms, rows, max repeats in one operation, errors. Flush on a timer through a `Channel`; the exporter never blocks a request and drops windows when the server is down (one warning log line).
- [ ] Hello on first flush: app name/version/instance/environment and, when allowed, the target(s) with connection strings.
- [ ] `samples/Shop.Api`: a tiny ASP.NET Core + EF Core app with a deliberate N+1 and one slow query, for local development and demos.
- [ ] `benchmarks/Momus.Client.Benchmarks` (BenchmarkDotNet): interceptor overhead at 1,000 queries/s; target under 1% CPU and 5 MB. Numbers go into the README.

### M2.2 Ingest and store

- [ ] `POST /api/v1/ingest` per the contract in DESIGN.md. Version 1 frozen at 1.0; additive changes only.
- [ ] Tables: `apps`, `app_instances`, `windows`, `query_stats`, `operation_stats`, `query_texts`. Target registration from the hello, with the localhost retry rule.
- [ ] Hourly rollup job after 24 h; raw windows kept 24 h, rollups 7 days (free tier defaults).

### M2.3 Queries tab

- [ ] One row per fingerprint: normalized sample, operation, call site, calls/min, app mean ms, DB mean ms, "database says" (joined finding), insight pill. Rows the DB reports but no app sent are shown as "not seen from any app".

### M2.4 Insight engine v1

- [ ] `IInsight` and `IInsightContext` in `Momus.Core` (contracts only); implementations in `Momus.Server/Insights`. Context offers typed reads: latest findings by subject, query stats since, stats by app version, transactions, pool waits, previous insight state.
- [ ] `insights` table upserted by stable key (kind + subject) with first_seen, last_seen, status.
- [ ] `hot_query_origin` and `n_plus_one` per the rules in DESIGN.md. `db_finding` pass-through with the operations touching its subject.
- [ ] Insights tab: plain list ordered by severity then last seen (ranking comes in M3).
- [ ] Tests on an in-memory store: each insight fires on a crafted dataset and stays quiet on a healthy one.

### M2.5 Release

- [ ] Tag `v0.2.0`. `Momus.Client` appears on nuget.org for the first time.

---

## M3 — Time and blame

**Goal.** Deploy markers, regression detection, transaction and pool insights, the "Fix first" ranking, evidence packs, mute/fixed states, History tab.

**Done when.** A deliberately slowed query in a new build is flagged with the version name within two minutes of starting the app. Release `v0.3.0`.

- [ ] `deploys` derived from `app_instances` (first seen per version). Header shows the current version.
- [ ] `regression`: mean ms under newest version > 3× previous and ≥ 20 ms slower, ≥ 100 calls each side; Critical above 10×. Evidence includes DB findings that appeared between the two deploys.
- [ ] Client: `IDbTransactionInterceptor` (open ms, DB ms inside, call site) and `IDbConnectionInterceptor` (pool wait). Contract additions `transactions`, `pool`.
- [ ] `transaction_held_open` (p95 open > 500 ms and DB share < 30%) joined with idle-in-transaction / blocking findings. `pool_wait` (p95 > 100 ms) joined with connection saturation.
- [ ] Ranking score: severity weight × share of traffic hitting the subject × recency factor. Top five become "Fix first" cards with sparkline, call-site chip, DB-evidence chip.
- [ ] Evidence pack: Markdown to clipboard with both sides' numbers, normalized query, versions. Written to be pasted into a coding agent.
- [ ] Mute and Fixed actions; a fixed insight that fires again reopens.
- [ ] History tab: deploys and findings over time; one chart, nothing more.
- [ ] Retention job: nightly trim and `VACUUM`.
- [ ] Tag `v0.3.0`.

---

## M4 — Meet the user where they are

**Goal.** MCP, docs, landing page, launch.

**Done when.** A coding agent asked "what is wrong with my database" answers from Momus's evidence, and twenty people you do not know have run the container. Release `v1.0.0`.

- [ ] MCP endpoint `/mcp` (streamable HTTP, official C# SDK), read-only tools: `momus_top_insights`, `momus_query`, `momus_findings`, `momus_operation`, `momus_since_deploy`. `momus mcp --server <url>` stdio bridge.
- [ ] `momus report --json in.json --html out.html`: self-contained audit page from a scan.
- [ ] README rewritten around the one-line install; `.mcp.json` snippet; benchmark numbers; screenshots.
- [ ] Landing page on softwarefirst.gr in the style of the Switchboard page.
- [ ] Optional adapters `Momus.Client.Switchboard` and `Momus.Client.MediatR` (one pipeline behavior each naming the operation).
- [ ] Launch: r/dotnet, Show HN, awesome-dotnet PR, a post on the three insights with real screenshots.
- [ ] Tag `v1.0.0`.

---

## M5 — Pro (only after M4 has users)

- [ ] Offline license key (signed, validated in-process, `MOMUS_LICENSE`).
- [ ] Gates per D10: retention settings, multiple apps and targets, alerts (Slack, email, webhook on new High/Critical), login, white-label report.
- [ ] Merchant of record (Paddle or Lemon Squeezy) for EU VAT. Pricing page.
- [ ] The tier is shaped by what free users ask for. Do not build ahead of them.

## Later / not planned

OTLP ingestion. Dapper and raw ADO.NET capture through provider activity sources. MySQL provider. Hosted version. All deliberately out of 1.0 (D5).
