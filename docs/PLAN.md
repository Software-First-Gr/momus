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
| 2026-09-07 | **M2.1 drafted but never compiled.** `src/Momus.Client` and `Momus.Core/Ingest` exist and are uncommitted; the session that wrote them lost the ability to run `dotnet` partway through. Nothing after M1 has been through a compiler. Start the next session with `dotnet build`, then wire `AddMomus()` into `samples/Shop.Api`. The commits up to `6cab539` are green and unaffected. |
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
| D12 | 2026-09-07 | **Legacy .NET support, if built, is separate projects in this repo — never extra target frameworks on `Momus.Client`.** `Momus.Client` carries a `FrameworkReference` to ASP.NET Core, so it cannot also be a `net472` System.Web host; and adding `EntityFramework` 6.x to it would push EF6 onto every EF Core user. So `Momus.Client.Ef6` (the EF6 hook, usable on modern .NET *and* on Framework) and `Momus.Client.Framework` (the System.Web host, Windows only), with the host-neutral capture pieces moved down into `Momus.Core` rather than into a fourth package. Same repo, same version, same tag (D2); the same adapter shape M4 already plans for `Momus.Client.Switchboard`. This settles the **layout**, not whether to build it — that is M6 and it is gated. |

## Open questions

- ~~**EF Core auto-registration of interceptors**~~ **Answered by measurement, 2026-09-07.** The first candidate is wrong: registering an `IInterceptor` in the application's DI container does **not** make EF Core pick it up — verified false on EF Core 8.0.11, 9.0.11 and 10.0.11, with `AddDbContext`, `AddDbContextPool` and registration both before and after. What does work on all three majors is **rewriting the `DbContextOptions<T>` service descriptors**: `AddMomus()` walks the `IServiceCollection`, and for every `DbContextOptions<T>` registration wraps the factory so the options it hands out carry one more interceptor. Confirmed for `AddDbContext`, `AddDbContextPool` and `AddDbContextFactory`. Two consequences, both documented and handled: `AddMomus()` must be called **after** the `AddDbContext` calls (the client warns loudly at startup if it decorated nothing), and a `DbContext` built by hand outside DI cannot be reached at all.
- ~~**Fingerprint edge cases**~~ **Answered in M1.2 by real pairs.** Three things actually differ, and all three are now handled: (1) EF Core appends a trailing `;` that the statistics views do not keep; (2) EF batches several statements into one command while the database records each separately, so joining is per statement — hence `SqlFingerprint.Split`; (3) **SQL Server's simple parameterization rewrites the statement before caching it**, turning `FROM [t] AS [a]` into `FROM [t] [a]` and a literal into `@1`, so the optional alias `AS` must be dropped on both sides. Aliases, `IN` lists, `VALUES` batches, tag comments and pagination parameters needed no special handling. Re-run `tools/FingerprintCapture` against a new EF or engine version to check this still holds.
- **A universal data-access hook.** The descriptor-rewriting trick works, but it costs a call-ordering
  rule (`AddMomus()` after `AddDbContext()`), it cannot see a `DbContext` built by hand, and it is
  EF Core only. There may be a hook below all of that: `Microsoft.Data.SqlClient` emits
  `DiagnosticListener` events per command, and Npgsql emits `ActivitySource` spans carrying the
  statement. One subscriber there would in principle catch EF Core, EF6, Dapper and raw ADO.NET at
  once, with no ordering rule and no dependency on how the context was built. **Unverified — measure
  it (M2.1) before M2.2 freezes the ingest path**, the same way the EF Core interceptor question was
  settled, where the documented-looking answer turned out to be false. If it holds it changes the
  cost of M6 and of the Dapper line under "Later".

- **Is legacy .NET an audience for Momus?** Unknown, and cheap to find out: `scan` and `serve` already
  work for those shops today — they read the database, not the app — so the probe is one honest line
  in the README (M1.7) and then counting who turns up. M6 is gated on the answer.

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
- [ ] Note in the README that the `Momus` tool now runs on the ASP.NET Core shared framework, because the binary contains the server. Verified working: packed 0.0.1, installed with `dotnet tool install --tool-path`, both `scan` and `serve` run — the .NET SDK ships that framework, and installing a dotnet tool requires the SDK. It only matters for a machine with the runtime but not the SDK.
- [ ] README: say plainly that `scan` and `serve` ask nothing of the application — they read the
      database's own statistics views, so they work just as well against a .NET Framework, Java or
      PHP app as against a modern .NET one. True today, costs a paragraph, and it is the cheapest
      probe for whether legacy shops are an audience (M6).
- [ ] Decide D3 and set the license expression for the `Momus` tool accordingly.
- [ ] Tag `v0.1.0`. Verify packages and image.

---

## M2 — The other side

**Goal.** `Momus.Client` reports operations, query stats and call sites; the server ingests them; the Queries tab shows both sides of every statement; the first two insights fire.

**Done when.** The top query in `pg_stat_statements` on your own project is shown with a file and line, and one N+1 you did not know about is found. Release `v0.2.0`.

### M2.1 Client project

> **State: written, partly compiled.** The session that wrote this lost the ability to run any
> build command partway through, so the boxes below mean "written and reviewed by eye", not
> "verified". What *is* known to compile, because it was built before the block: `Momus.Core` on
> all three target frameworks including `Ingest/IngestContract.cs`, and `MomusCommandInterceptor`
> (its only error was a type that did not exist yet, so every EF Core interceptor signature it
> overrides resolved). Not yet through a compiler: `OperationQueue`, `Window`, `MomusExporter`,
> `MomusMiddleware`, `MomusRuntime`, `MomusOperation`, `MomusServiceCollectionExtensions`, and
> the wiring in `samples/Shop.Api`.
>
> **First thing next session: `dotnet build`, then `dotnet test`.** Only then tick anything.

- [x] New project `src/Momus.Client`, multi-target `net8.0;net9.0;net10.0`, dependencies `Microsoft.EntityFrameworkCore.Relational` (floored per major like Switchboard) and the ASP.NET Core framework reference. Package id `Momus.Client`. `Momus.Core` had to be multi-targeted to match (it holds `SqlFingerprint`), which needed one `#if` for `Convert.ToHexStringLower`, a .NET 9 API.
- [x] `services.AddMomus()` with `MomusOptions` bound from `Momus:` configuration: `Enabled` (default: Development only, and **off** when the environment cannot be determined — the safe direction), `Endpoint` (`http://localhost:4848`), `ShareConnectionStrings` (default: endpoint is loopback or `host.docker.internal`), `FlushSeconds` (5), `AppName` (entry assembly), `Environment`.
- [x] Operation scope: `IStartupFilter` inserts middleware first; `AsyncLocal` scope that restores its parent rather than clearing it; the name is resolved **lazily on first use**, which is what lets the middleware sit first in the pipeline and still say `GET /orders/{id}` instead of `GET /orders/4711`; falls back to `Activity.Current.DisplayName`.
- [x] EF Core hooks: `DbCommandInterceptor` on reader / non-query / scalar, executed and failed, plus `DataReaderDisposing` for row counts (a reader's rows are only known once it has been read to the end). Registered with no user code, by the descriptor-rewriting method the open question settled on.
- [x] Call site: stack walked once per (fingerprint, operation) and cached; first frame outside `Momus.Client`, `Microsoft.*`, `System.*` and the ADO.NET providers; unwraps async state machine names; falls back to `Type.Method` with no PDB. An EF tag comment wins.
- [x] Aggregator: bounded per operation and per window, both with overflow counters. Per key: count, sum/max ms, 8-bucket log2 histogram, rows, max repeats in one operation, errors. Requests hand a finished operation to a bounded `Channel` (drop-on-full, counted) and return; one background loop folds, a timer flushes, and a dead server costs one warning line and nothing else.
- [x] Hello on first flush: app name/version/instance/environment and, when allowed, the target(s) with connection strings, learned from the contexts that execute statements.
- [x] `samples/Shop.Api`: **already built in M1.8.** Now references `Momus.Client` and calls `builder.AddMomus()` after its `AddDbContext`; the compose file sets `Momus__Enabled=true` (the container runs as Production, where the client is off by default), `Momus__Endpoint=http://momus:4848` and `Momus__ShareConnectionStrings=true` (the server is not on loopback here, so sharing has to be asked for).
- [ ] **Until M2.2 lands, the client posts to an endpoint that does not exist yet** and logs one warning that the server is not answering. That is the expected state, not a bug.
- [ ] `benchmarks/Momus.Client.Benchmarks` (BenchmarkDotNet): interceptor overhead at 1,000 queries/s; target under 1% CPU and 5 MB. Numbers go into the README.
- [ ] Tests: a `Momus.Client.Tests` project multi-targeting net8.0/net9.0/net10.0 that asserts `AddMomus()` alone causes an interceptor to fire on all three EF majors. The throwaway probe that answered the open question should become this test, so the answer keeps holding.
- [ ] Spike, half a day, throwaway: can a `DiagnosticListener` / `ActivitySource` subscriber see
      command text and duration from `Microsoft.Data.SqlClient` and from Npgsql? See Open questions.
      Answer it before M2.2 freezes the ingest path — it decides whether M6 and Dapper capture are a
      port or a rewrite.

**Two deviations from DESIGN.md, both needing a decision.**

1. The background-work API is `MomusOperation.Begin("ImportJob")`, not `Momus.Operation("ImportJob")`.
   A type called `Momus` inside namespace `Momus.Client` collides with the root `Momus` namespace at
   every call site. Update DESIGN.md, or find a nicer name.
2. The documented one-liner is `builder.AddMomus()`, not `builder.Services.AddMomus()`. The service
   collection alone cannot reliably answer what the configuration or the environment is — the host
   registers `IConfiguration` as a *factory*, not an instance, so binding the `Momus:` section off
   the collection silently reads nothing. The `IServiceCollection` overload still exists and takes
   an optional `IConfiguration`; without one it stays off rather than guessing.

**Contract location.** The ingest DTOs live in `Momus.Core/Ingest/IngestContract.cs` rather than
being written twice — the client and the server compile against one definition. DESIGN.md shows the
JSON but does not say where the types live; this is that decision.

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

## M6 — Legacy .NET (gated; not ordered against M5)

**Goal.** An application Momus cannot reach today — .NET Framework, EF6, or no ORM at all — reports
the same operations, query keys and call sites as a modern one, with the same one-line install.

**Gate.** Do not start on a hunch. Start when a real .NET Framework shop has run `momus serve`
against their database and asked for the app side, or when the M4 launch produces that request more
than once. Until then the whole cost of keeping the door open is the README line in M1.7 and not
painting `Momus.Client` further into a corner.

**Why it could be worth it.** Legacy apps have the worst database problems and the least
observability — years of accreted queries, EF6 lazy loading, no APM because of what APM costs — and
they are the ones no modern tooling can attach to. The value bar is also lower there: "which page
ran how many queries" is already a win in a codebase where nobody knows.

**Why it might not be.** Those shops are the least likely to add an in-process interceptor to an
application that has run for twelve years, and the most likely to have change control between the
decision and the deployment.

### The three axes, cheapest first

1. **EF6 on modern .NET — small, and separable from everything else.** EF6 has run on modern .NET
   since 6.3, so this is not the same question as .NET Framework. `DbInterception.Add` is a
   process-global static registry: one line, no descriptor rewriting, and it catches contexts built
   by hand outside DI — *better* coverage than the EF Core path. Ships as `Momus.Client.Ef6` (D12).
   Everything downstream is unchanged. **The risk is not the plumbing, it is the fingerprint**: EF6
   emits very different SQL from EF Core (`[Extent1]`, `[Project1]`, subquery towers), and joining
   app SQL to statistics-view SQL is the entire product. Prove it with captured pairs from
   `tools/FingerprintCapture` against an EF6 context, per the rule in CLAUDE.md — do not assume
   `SqlFingerprint` already handles it.

2. **No ORM at all (Dapper, raw ADO.NET).** A large share of legacy .NET never used an ORM. Whether
   this is cheap or impossible is exactly the universal-hook open question above. Settle that first.

3. **.NET Framework / System.Web — a second product, not a target framework.** Nothing in the
   current host layer survives: `IHostApplicationBuilder`, `IStartupFilter`, `HttpContext`,
   `IHostedService`, `AddHttpClient`. The good news is that plug-and-play is *easier* there than on
   modern .NET: `[assembly: PreApplicationStartMethod]` plus `DynamicModuleUtility.RegisterModule`
   self-registers an `IHttpModule` on package install, with zero lines in `Global.asax` — the
   Glimpse / ELMAH / MiniProfiler route. `AsyncLocal` exists on 4.6+, `HttpClient` on 4.5+,
   `HostingEnvironment.QueueBackgroundWorkItem` gives the export loop, and `IRegisteredObject`
   flushes before an app-pool recycle. Floor at `net472`.

### What actually has to move (the seam)

Most of `Momus.Client` is already host-neutral and does not know it: `Window`, `OperationQueue`,
`CallSites`, `MomusOptions`, and the recording logic inside `MomusCommandInterceptor`. The only real
coupling is `OperationContext`'s `HttpContext?` constructor parameter, used solely to resolve the
operation name — a name-resolver delegate is the whole seam. `Microsoft.Extensions.*` is
netstandard2.0, so even `MomusExporter` is more portable than it looks.

- [ ] Move the host-neutral pieces into `Momus.Core` (which already holds `SqlFingerprint` and the
      ingest contract) rather than inventing a fourth package. Leave the exporter and the host wiring
      per-host.
- [ ] Add `netstandard2.0` to `Momus.Core`. `required`, records and collection expressions need
      shims; `System.Text.Json` becomes a package reference. Mechanical, but it taxes every later
      Core change — which is why it is worth keeping Core on portable APIs in the meantime, where the
      portable one is equally good.

### Costs that are not code, and are the real schedule

- **CI is ubuntu-only** — all three jobs in `.github/workflows/ci.yml`. A System.Web project probably
  still *compiles* cross-platform via `Microsoft.NETFramework.ReferenceAssemblies`, but it cannot be
  **run or tested** anywhere but Windows. Verify the compile claim rather than assuming it, and
  decide how `dotnet build` at the repo root stays green on a Mac: a solution filter for the ubuntu
  jobs, or conditioning the project on `'$(OS)' == 'Windows_NT'`.
- **A second sample app and a second demo stack.** `samples/Shop.Api` is Linux and Kestrel; none of it
  transfers. Manual IIS testing is where the time goes.
- **Call sites degrade.** `new StackTrace(true)` works, but without portable PDBs deployed it gives
  method names, not `OrdersHandler.cs:42` — and D6 says mapping to code is what ranks above
  everything else. Measure what survives before promising it.

### The default that does not transfer

D5 keeps the client off outside Development. That is close to useless for legacy: the reason a
twelve-year-old app needs this is that the problem only appears in production, and those shops
usually have no development environment carrying real load. Turning it on in production raises the
overhead budget and the trust story at the same time, in exactly the audience least willing to
accept either. Decide this before writing the client, not after.

There is a zero-install fallback that dodges the whole question and should be documented either way:
set `Application Name=` per app in the connection string and attribute statistics-view rows by app.
Far less than per-endpoint attribution, but it needs no deployment and no package.

---

## Later / not planned

OTLP ingestion. MySQL provider. Hosted version. All deliberately out of 1.0 (D5). Dapper and raw
ADO.NET capture moved from here into M6, where it is one of the three axes and depends on the
universal-hook open question.
