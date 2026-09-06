# Momus

> Named after the Greek god of criticism. Point it at a database; it tells you what's wrong.

**Status:** pre-release (`0.0.1`). The collector core and CLI work today; the API and package layout may change until 1.0.

Momus is an AI-era DBA/SRE collector: it connects to your database, runs a suite of
diagnostic checks against the engine's own statistics views, and produces structured,
prioritized findings — the raw material for "two consultants at once" (DBA + SRE) aimed
at small dev teams. This is the **collector core**: the scanning engine, the first two
providers (PostgreSQL and SQL Server), and a CLI.

## Quick start

Requires the .NET 10 SDK.

```bash
dotnet build

# PostgreSQL
dotnet run --project src/Momus.Cli -- scan \
  --provider postgres \
  --connection "Host=localhost;Username=postgres;Password=...;Database=mydb"

# SQL Server
dotnet run --project src/Momus.Cli -- scan \
  --provider sqlserver \
  --connection "Server=localhost;Database=mydb;User Id=sa;Password=...;TrustServerCertificate=true"

# Machine-readable output
dotnet run --project src/Momus.Cli -- scan -p postgres -c "..." --json report.json --quiet
```

Exit codes: `0` clean scan · `3` High/Critical findings (CI-friendly) · `1` scan failed · `2` bad arguments.

## What it checks today

**PostgreSQL** — buffer cache hit ratio, connection saturation, tables dominated by
sequential scans, unused indexes, dead-tuple/vacuum debt, idle-in-transaction and
long-running sessions, top queries by cumulative cost (via `pg_stat_statements`,
degrades gracefully when absent).

**SQL Server** — dominant wait types (benign waits filtered, known causes annotated),
optimizer-suggested missing indexes, top queries by CPU, live blocking chains,
buffer-pool pressure (page life expectancy).

Findings carry a severity (`info`→`critical`), a plain-language explanation, a
recommendation, and structured evidence for downstream/AI consumption.

## Architecture

```
src/Momus.Core       engine + models: IDiagnosticCheck, IScanTarget, CollectorEngine, Finding
src/Momus.Postgres   PostgresScanTarget + checks (Npgsql)
src/Momus.SqlServer  SqlServerScanTarget + checks (Microsoft.Data.SqlClient)
src/Momus.Cli        `momus scan` — console report + JSON export
tests/Momus.Tests    engine + threshold unit tests, opt-in live Postgres test
```

Design rules the code follows:

- **Checks are isolated.** A check that throws (missing permission, old server version)
  becomes a failed `CheckResult`; the scan always completes with whatever it could learn.
- **Thresholds are pure functions** (`PostgresThresholds`) so the "when is it a problem"
  logic is unit-testable without a database.
- **Read-only by construction.** Every query targets statistics/system views only.
- **Empty is healthy.** A check returning no findings is the good outcome, not an error.

## Tests

```bash
dotnet test                                                        # unit tests
MOMUS_TEST_PG="Host=localhost;Username=postgres" dotnet test       # + live Postgres scan
```

## Roadmap (from the product plan)

- Docker image for in-environment deployment (customer runs it next to their DB)
- OpenTelemetry ingestion alongside DB-native stats
- EF Core-aware suggestions (combine app-side and DB-side signals)
- AI layer that turns findings into narrative advice; freemium audit report
