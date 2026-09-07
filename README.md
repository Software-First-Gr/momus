# Momus

> Named after the Greek god of criticism. Point it at a database; it tells you what's wrong.

**Status:** pre-release. The collector core, the CLI, `momus serve` and the app-side client all
work today — **from a checkout**. Nothing current is published: `Momus` 0.0.1 on nuget.org was
uploaded to claim the id before the server existed, so it has no `serve` command, and no image
has been pushed to GHCR yet. Both land with `v0.1.0`. Until then, build from source
(`docker compose up --build`, or the *Quick start from source* section below). The API and
package layout may change until 1.0.

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

Build the image once from a checkout — the published one arrives with `v0.1.0`:

```bash
docker build -f src/Momus.Cli/Dockerfile -t momus .
docker run -d --name momus -p 127.0.0.1:4848:4848 -v momus-data:/data \
  -e MOMUS_TARGETS__0__PROVIDER=postgres \
  -e MOMUS_TARGETS__0__CONNECTIONSTRING="Host=host.docker.internal;Username=momus;Password=…;Database=shop" \
  momus
```

Then open <http://localhost:4848>. Momus scans every 60 seconds, keeps every scan in a SQLite
file on `/data`, and shows each finding with the first and last time it saw it — which is the
one thing a one-shot scan can never tell you.

**The server has no authentication and binds to all interfaces inside the container**, and the
connection string is stored as given in the SQLite file on `/data`. Publish the port to
`127.0.0.1` as above, or put it behind your own proxy.

### The user it needs

Momus only reads statistics views, but on both engines those are privileged. A dedicated
read-only login is enough:

```sql
-- PostgreSQL. pg_monitor is the built-in monitoring role; without it pg_stat_activity and
-- pg_stat_statements hide other users' statements, and half the checks see nothing.
CREATE ROLE momus LOGIN PASSWORD '…';
GRANT pg_monitor TO momus;
GRANT CONNECT ON DATABASE shop TO momus;

-- Query ranking needs the extension, installed once by a superuser, after adding
-- pg_stat_statements to shared_preload_libraries and restarting.
CREATE EXTENSION IF NOT EXISTS pg_stat_statements;
```

```sql
-- SQL Server. VIEW SERVER STATE covers the dm_os_* and dm_exec_* views;
-- on Azure SQL Database, grant VIEW DATABASE STATE in the database instead.
CREATE LOGIN momus WITH PASSWORD = '…';
GRANT VIEW SERVER STATE TO momus;
```

Without `pg_stat_statements` Momus says so as an Info finding and keeps running; every other
check still works.

Or from a checkout, with no container:

```bash
dotnet run --project src/Momus.Cli -- serve \
  --target postgres:"Host=localhost;Username=momus;Password=…;Database=shop"
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

## When it is not doing what you expected

Open <http://localhost:4848/diagnostics>. Momus has two halves and most of the ways they fail are
quiet — a client that cannot reach the server, a scanning user that cannot see other sessions'
statements, two sides fingerprinting the same SQL differently. Each of those looks exactly like
"nothing to report", so the page names them:

```
- OK — shop: scanned 12s ago, 54 finding(s).
- OK — Shop.Api is reporting: 260 window(s) and 9 distinct statement(s) in the last hour.
- OK — 6 statement(s) matched on both sides. 15 insight(s) from it.
```

It ends with the whole report as Markdown, with no connection strings in it, written to be pasted
into an issue or a coding agent. `curl localhost:4848/api/v1/diagnostics` returns the same as JSON.

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
