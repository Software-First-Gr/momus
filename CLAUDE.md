# Momus

Database diagnostics for .NET teams. Today: a CLI that runs read-only checks over
PostgreSQL / SQL Server statistics views and prints prioritized findings. The 1.0
plan adds an in-app client, a server with history, and insights that join the app
side with the database side.

Read first, in this order:

1. `docs/PLAN.md` — what to do next. Tick boxes as tasks finish; log decisions there.
2. `docs/DESIGN.md` — what 1.0 is and why. Change the design there before changing code that contradicts it.
3. `README.md` — the public quick start. It is packed into every NuGet package, so keep it true.

## Commands

```bash
dotnet build
dotnet test                                  # unit tests; MOMUS_TEST_PG="Host=...;Username=..." adds a live Postgres scan test
dotnet pack -c Release -o artifacts          # Momus (tool), Momus.Core, Momus.Postgres, Momus.SqlServer

dotnet run --project src/Momus.Cli -- scan -p postgres -c "<connection string>" [--json report.json --quiet]
dotnet run --project src/Momus.Cli -- serve --target postgres:"<connection string>"   # http://localhost:4848

docker compose up --build                    # db + Momus (:4848) + the demo shop (:8080)
```

Exit codes for `scan`: 0 clean, 3 High/Critical findings, 1 scan failed, 2 bad arguments.

The demo stack is the fastest way to see a change work: the shop generates real traffic, so
findings appear on their own. `docker compose down -v` resets both databases.

## Layout

```
src/Momus.Core        engine + models: IDiagnosticCheck, IScanTarget, CollectorEngine, Finding,
                      ScanReport, Subject, SqlFingerprint, the ingest contract, and the insight
                      contracts (IInsight, IInsightContext, Insight)
src/Momus.Postgres    PostgresScanTarget + checks (Npgsql)
src/Momus.SqlServer   SqlServerScanTarget + checks (Microsoft.Data.SqlClient)
src/Momus.Server      serve: SQLite store (versioned schema scripts), ScanScheduler, ingest
                      endpoint + RetentionService, QueryJoin, Insights/ (the rules and the
                      engine) + InsightService, Razor Pages UI
src/Momus.Client      in-app half: AddMomus(), EF Core interceptor, operation scope, exporter
src/Momus.Cli         `momus` dotnet tool: scan + serve; the Docker image is built from here
samples/Shop.Api      demo shop with deliberate problems and a control panel (:8080)
tools/FingerprintCapture   regenerates the fingerprint fixture pairs from a live database
tests/Momus.Tests     engine, thresholds, fingerprint fixtures, store, ingest, query join,
                      scheduler, options
docker/               postgres.conf and init SQL for the demo stack
docs/                 DESIGN.md, PLAN.md
```

## Rules the code follows

- **Checks are isolated.** A throwing check becomes a failed `CheckResult`; the scan always completes.
- **Thresholds are pure functions** (`PostgresThresholds`) so "when is it a problem" is testable without a database.
- **Read-only by construction.** Every query targets statistics/system views only. Never add a query that writes or locks.
- **Empty is healthy.** A check returning no findings is the good outcome.
- **Every finding carries a `Subject`.** Typed keys (`table:public.orders`, `query:<fingerprint>`) are how findings are joined and how the store recognises the same finding across scans. Never key identity on a title.
- **One fingerprint for both sides.** `SqlFingerprint` must give the same key to the app's SQL and to the statistics view's SQL. Its tests are real captured pairs; regenerate them with `tools/FingerprintCapture` rather than editing the fixtures by hand.
- **Insights never open a database connection** (1.0). They read the store. `IDiagnosticCheck` reads live views; keep the two kinds apart. `IInsightContext` is a snapshot loaded before any rule runs, so a rule is a pure function of its inputs and takes time from `ctx.Now`, never from the clock.
- **Momus's own prose is invariant-culture.** The UI, the insight text and (in M3) the evidence packs are English; their numbers must read as English wherever the server runs. `MomusServer` pins the culture and the rules use `FormattableString.Invariant`.
- **The client never sends literals, parameter values or result rows** (1.0). Only normalized text and aggregates leave the app.

## Working conventions

- Work on `develop`; `main` is for releases (see PLAN.md housekeeping for the pending branch decision).
- One version for all packages in `Directory.Build.props`. CI overrides it from the tag. Do not bump it or push a `v*` tag unless a release is intended: `.github/workflows/ci.yml` publishes to nuget.org on every `v*` tag via NuGet Trusted Publishing (no key is stored; the `NUGET_USER` secret is the nuget.org owner name).
- Package ids are claimed on nuget.org: `Momus`, `Momus.Core`, `Momus.Postgres`, `Momus.SqlServer`. 0.0.1 was published only to claim them; its listing shows placeholder text until the next release.
- Licensing: Apache-2.0 for everything today. Planned: server + CLI binary under FSL-1.1-ALv2 once the server ships. This is decision D3 in PLAN.md and is still open.
- The repo is public. No announcement, launch post or Show HN until M1 runs end to end.
- Keep the docs honest: when a task lands, tick it in PLAN.md; when a decision is made, add it to the Decisions table with the date.
