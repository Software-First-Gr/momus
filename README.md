# Momus

> Named after the Greek god of criticism. Point it at a database; it tells you what's wrong.

**Status:** pre-release (`0.0.1`). The collector core, the CLI and `momus serve` work today;
the API and package layout may change until 1.0.

Momus is an AI-era DBA/SRE collector: it connects to your database, runs a suite of
diagnostic checks against the engine's own statistics views, and produces structured,
prioritized findings — the raw material for "two consultants at once" (DBA + SRE) aimed
at small dev teams. Today that is the scanning engine, two providers (PostgreSQL and
SQL Server), a CLI for one-off scans and CI, and a small server that scans on a schedule
and remembers what it found.

## Try it in one command

Nothing to install but Docker. This brings up a Postgres, a deliberately flawed .NET shop that
keeps traffic running against it, and Momus watching the database from the outside:

```bash
docker compose up --build
```

- <http://localhost:8080> — the demo shop. Buttons for an N+1 page, an unindexed search, an
  idle transaction, a connection flood. Each one says what Momus should make of it.
- <http://localhost:4848> — Momus. Findings, with the day each one first appeared.

Give it a minute of traffic, then compare the two.

## Run it against your own database

```bash
docker run -d --name momus -p 4848:4848 -v momus-data:/data \
  -e MOMUS_TARGETS__0__PROVIDER=postgres \
  -e MOMUS_TARGETS__0__CONNECTIONSTRING="Host=host.docker.internal;Username=postgres;Password=…;Database=shop" \
  ghcr.io/software-first-gr/momus
```

Then open <http://localhost:4848>. Momus scans every 60 seconds, keeps every scan in a SQLite
file on `/data`, and shows each finding with the first and last time it saw it — which is the
one thing a one-shot scan can never tell you.

The image binds to all interfaces with no authentication. Put it behind your own proxy if that
port leaves your machine.

Or as a dotnet tool, with no container:

```bash
dotnet tool install -g Momus
momus serve --target postgres:"Host=localhost;Username=postgres;Database=shop"
```

## See what your application asked for

Everything above works without touching your code: Momus reads the database's own statistics
views, so it works just as well against a Java, PHP or .NET Framework application as a modern
.NET one. `Momus.Client` adds the other half — which endpoint ran which statement, how many
times per request, and from which line — and the Queries tab puts the two views of one
statement on one row:

| Query | Operation | Call site | Calls/min | App mean | DB mean | Database says |
| --- | --- | --- | ---: | ---: | ---: | --- |
| `select … from order_lines where order_id = ?` | `GET /api/orders/{id}` | `OrdersHandler.cs:42` | 371 | 8.3 ms | 7.9 ms | #1 query by total time |

Both numbers describe the same statement because both sides fingerprint it the same way.

> **Not on nuget.org yet.** `Momus.Client` ships with `v0.2.0` (M2.5 in `docs/PLAN.md`). Until
> then, reference `src/Momus.Client/Momus.Client.csproj` from a checkout, the way
> `samples/Shop.Api` does.

```csharp
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<ShopContext>(o => o.UseNpgsql(connectionString));
builder.AddMomus();   // after your AddDbContext calls
```

That is the whole integration. It registers an EF Core interceptor, names each operation after
the matched route, and posts aggregates to `http://localhost:4848` every five seconds.

**It has to come after `AddDbContext`.** `AddMomus()` works by wrapping the
`DbContextOptions<T>` registrations that already exist; called first it has nothing to wrap, and
says so loudly at startup. A `DbContext` constructed by hand outside DI is not seen at all.

What leaves your process, and what never does:

- **Sent:** normalized statement text (`where order_id = ?`), counts, timings, row counts, the
  operation name and the call site.
- **Never sent:** parameter values, literals, result rows. They are not read in the first place.
- **Off outside Development.** Set `Momus__Enabled=true` to turn it on elsewhere.
- **Never blocks a request.** A finished request hands an object to a bounded channel and
  returns; a Momus server that is down costs one warning line and nothing else.

| Setting | Default |
| --- | --- |
| `Momus:Enabled` | on in Development only |
| `Momus:Endpoint` | `http://localhost:4848` |
| `Momus:ShareConnectionStrings` | on when the endpoint is loopback |
| `Momus:FlushSeconds` | `5` |
| `Momus:AppName` | the entry assembly's name |

`ShareConnectionStrings` is how the server learns which database to scan without you configuring
it twice. It sends the connection string of the contexts that ran statements, so leave it off
unless the Momus server is one you run.

For work that is not a request, name it yourself:

```csharp
using var operation = MomusOperation.Begin("NightlyInvoiceRun");
```

## Quick start from source

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
src/Momus.Core       engine + models: IDiagnosticCheck, IScanTarget, CollectorEngine,
                     Finding, Subject, SqlFingerprint
src/Momus.Postgres   PostgresScanTarget + checks (Npgsql)
src/Momus.SqlServer  SqlServerScanTarget + checks (Microsoft.Data.SqlClient)
src/Momus.Server     `momus serve`: SQLite store, scheduler, ingest endpoint, web UI
                     (ships inside the CLI, never as its own package)
src/Momus.Client     the in-app half: EF Core interceptor, operation scope, exporter
src/Momus.Cli        `momus scan` and `momus serve`; the Docker image is this project
samples/Shop.Api     the demo shop: deliberate N+1, unindexed search, control panel
tools/FingerprintCapture   captures real (app SQL, statistics-view SQL) pairs for the tests
tests/Momus.Tests    engine, thresholds, fingerprint fixtures, store and scheduler
```

Design rules the code follows:

- **Checks are isolated.** A check that throws (missing permission, old server version)
  becomes a failed `CheckResult`; the scan always completes with whatever it could learn.
- **Thresholds are pure functions** (`PostgresThresholds`) so the "when is it a problem"
  logic is unit-testable without a database.
- **Read-only by construction.** Every query targets statistics/system views only.
- **Empty is healthy.** A check returning no findings is the good outcome, not an error.
- **Every finding names its subject.** A typed key — `table:public.orders`,
  `query:9f3a1c77d02b4e10` — so findings can be joined and followed over time instead of
  matched on their text.
- **One fingerprint, both sides.** `SqlFingerprint` gives the same key to a statement whether
  it comes from your application, from `pg_stat_statements` or from `dm_exec_sql_text`. The
  tests are 42 pairs captured from running servers, not written by hand.

## Tests

```bash
dotnet test                                                        # unit tests
MOMUS_TEST_PG="Host=localhost;Username=postgres" dotnet test       # + live Postgres scan
```

## Where this is going

The two halves are joined; what is missing is the part that reads the join and tells you what to
do about it. Next is the insight engine: *this endpoint has an N+1 on that table*, and after
that *and it started with last Tuesday's deploy* — the question a general-purpose AI cannot
answer, because it cannot see your database.

`docs/DESIGN.md` is the whole design; `docs/PLAN.md` is what happens next.
