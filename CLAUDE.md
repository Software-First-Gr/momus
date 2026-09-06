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
```

Exit codes for `scan`: 0 clean, 3 High/Critical findings, 1 scan failed, 2 bad arguments.

## Layout

```
src/Momus.Core        engine + models: IDiagnosticCheck, IScanTarget, CollectorEngine, Finding, ScanReport
src/Momus.Postgres    PostgresScanTarget + checks (Npgsql)
src/Momus.SqlServer   SqlServerScanTarget + checks (Microsoft.Data.SqlClient)
src/Momus.Cli         `momus` dotnet tool: scan today; serve / report / mcp per the plan
tests/Momus.Tests     engine + threshold unit tests, opt-in live Postgres test
docs/                 DESIGN.md, PLAN.md
```

## Rules the code follows

- **Checks are isolated.** A throwing check becomes a failed `CheckResult`; the scan always completes.
- **Thresholds are pure functions** (`PostgresThresholds`) so "when is it a problem" is testable without a database.
- **Read-only by construction.** Every query targets statistics/system views only. Never add a query that writes or locks.
- **Empty is healthy.** A check returning no findings is the good outcome.
- **Insights never open a database connection** (1.0). They read the store. `IDiagnosticCheck` reads live views; keep the two kinds apart.
- **The client never sends literals, parameter values or result rows** (1.0). Only normalized text and aggregates leave the app.

## Working conventions

- Work on `develop`; `main` is for releases (see PLAN.md housekeeping for the pending branch decision).
- One version for all packages in `Directory.Build.props`. CI overrides it from the tag. Do not bump it or push a `v*` tag unless a release is intended: `.github/workflows/ci.yml` publishes to nuget.org on every `v*` tag via NuGet Trusted Publishing (no key is stored; the `NUGET_USER` secret is the nuget.org owner name).
- Package ids are claimed on nuget.org: `Momus`, `Momus.Core`, `Momus.Postgres`, `Momus.SqlServer`. 0.0.1 was published only to claim them; its listing shows placeholder text until the next release.
- Licensing: Apache-2.0 for everything today. Planned: server + CLI binary under FSL-1.1-ALv2 once the server ships. This is decision D3 in PLAN.md and is still open.
- The repo is public. No announcement, launch post or Show HN until M1 runs end to end.
- Keep the docs honest: when a task lands, tick it in PLAN.md; when a decision is made, add it to the Decisions table with the date.
