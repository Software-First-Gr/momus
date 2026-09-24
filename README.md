# Momus

> Named after the Greek god of criticism. Point it at a database; it tells you what's wrong.

**Status:** pre-release. The collector core, the CLI, `momus serve` and the app-side client all
work today — **from a checkout**. Nothing current is published: `Momus` 0.0.1 on nuget.org was
uploaded to claim the id before the server existed, so it has no `serve` command, and the image
`softwarefirst/momus` on Docker Hub carries only `edge`, rebuilt from `develop` on every push.
Released versions of both land with `v0.1.0`. Until then, use `edge` or build from source
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
  idle transaction, a connection flood, a checkout that holds its transaction open and an
  export that hogs the connection pool. Each one says what Momus should make of it.
- <http://localhost:4848> — Momus. Findings, with the day each one first appeared.

Give it a minute of traffic, then compare the two.

A deploy is one more command. This builds the shop again as version 1.1.0 with a regression in it —
the cart update's SQL is unchanged, but a new background job holds a lock it has to wait for:

```bash
SHOP_VERSION=1.1.0 SHOP_SLOW_BUILD=true docker compose up --build -d shop
```

Within a minute or so of traffic Momus says which statement got slower, by how much, and in which
version. Nobody tells it about the deploy; a new version turning up is the marker.

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

When the application runs on another machine, it needs to reach the ingest endpoint and nothing
else. Give it a port of its own and a key, and keep the pages on loopback:

```bash
docker run -d --name momus -v momus-data:/data \
  -p 127.0.0.1:4848:4848 -p 4849:4849 \
  -e MOMUS_INGEST_PORT=4849 -e MOMUS_INGEST_KEY="<a long random value>" \
  -e MOMUS_TARGETS__0__NAME=shop -e MOMUS_TARGETS__0__PROVIDER=postgres \
  -e MOMUS_TARGETS__0__CONNECTIONSTRING="Host=db;Username=momus;Password=…;Database=shop" \
  softwarefirst/momus:edge
```

Port 4849 answers `POST /api/v1/ingest` and `GET /healthz` and returns 404 for everything else,
judged by the port the connection arrived on. With `MOMUS_INGEST_KEY` set, a window without the
same value in `Momus:IngestKey` is refused, the application logs that once, and the server's
diagnostics page counts the refusals. The key travels in a header, so across a network you do not
trust, put TLS in front. Open the pages through an SSH tunnel: `ssh -L 4848:127.0.0.1:4848 <host>`.

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
-- SQL Server. VIEW SERVER STATE covers the dm_os_* and dm_exec_* views; on SQL Server 2022
-- and later, VIEW SERVER PERFORMANCE STATE is enough and narrower. On Azure SQL Database,
-- grant VIEW DATABASE STATE in the database instead.
CREATE LOGIN momus WITH PASSWORD = '…';
GRANT VIEW SERVER STATE TO momus;

-- In the database the connection string names. Without a user there, the login cannot open
-- it and no check runs at all. The user needs no permission of its own: Momus never reads a
-- table.
USE shop;
CREATE USER momus FOR LOGIN momus;
```

On a server shared with other databases, statements, sessions and blocking are reported for the
database in the connection string only; waits and page life expectancy are the whole instance's.

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
| `Momus:IngestKey` | none; required when the server sets `MOMUS_INGEST_KEY` |

`ShareConnectionStrings` is how the server learns which database to scan without you configuring
it twice. It sends the connection string of the contexts that ran statements, so leave it off
unless the Momus server is one you run. Without it, the server still puts the application's
statements beside the database's own: a database it does not know by the application's name for
it is matched to a scanned target on provider and database name, and the server's log says so once.

A deploy is a version the server has not seen before. The SDK writes the commit into the
informational version when it can see `.git`, and most Dockerfiles leave `.git` out, so a
container build reports `1.0.0` after every deploy. When the version names no revision, the client
appends a build fingerprint — `1.0.0+build.3e45f2a750a8`, a hash of the module id of every
assembly shipped with the application and of the runtime version. The same source gives the same
fingerprint, so a restart is not a deploy; any changed project or package gives a new one. To see
the commit instead, build with `-p:SourceRevisionId=$(git rev-parse HEAD)`.

For work that is not a request, name it yourself:

```csharp
using var operation = MomusOperation.Begin("NightlyInvoiceRun");
```

A WebSocket or a server-sent event stream is a connection, not a request, so it is never one
operation: a Blazor Server circuit would otherwise report nothing until its tab closed, and read
every repeated lookup as a loop. Statements run on one are reported as they happen, named after the
innermost `Activity` — a mediator's or a job's span, if the application has them.

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
long-running sessions, sessions blocked on a lock and the session holding it, top queries by cumulative cost (via `pg_stat_statements`,
degrades gracefully when absent).

**SQL Server** — dominant wait types (benign waits filtered, known causes annotated),
optimizer-suggested missing indexes, top queries by CPU, live blocking chains,
buffer-pool pressure (page life expectancy).

Statements, sessions and blocking are reported for the database in the connection string, even
when other databases share the server; connect to `master` to see the whole SQL Server instance.
Waits, page life expectancy, the cache hit ratio and connection saturation belong to the server
and say so.

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
MOMUS_TEST_PG="Host=localhost;Username=postgres" dotnet test       # + live Postgres tests
MOMUS_TEST_MSSQL="Server=localhost;User Id=sa;Password=…;TrustServerCertificate=True" dotnet test   # + live SQL Server tests
```

## What it tells you

Open <http://localhost:4848> and the first thing is a list of five things to fix, ordered by what
they are costing rather than by how alarming they sound — severity, times the share of your
traffic that hits them, times how recently they started:

```
MEDIUM · N+1 · GET /api/orders/{id} runs one statement ×6 per call
The same statement runs up to 6 times inside a single GET /api/orders/{id} (OrdersHandler.cs:42),
2,523 times a minute at 0.2 ms each. Collapsing the loop would remove roughly 2,103 round trips a
minute. The database ranks it too: #4 query by total time.
```

Each card copies as Markdown — both sides' numbers, the normalized statement, the versions —
written to be pasted into a coding agent, which is how most of this actually reaches a fix.

Six rules produce them: a loop inside one request, the database's most expensive statements mapped
to the line that runs them, a statement that got slower with a deploy, a transaction held open
across work that is not database work, a wait for a connection, and every database-side finding
passed through with the endpoints that touch it. A deploy is not something you tell Momus about —
it is a version turning up for the first time.

## Where this is going

1.0 adds MCP, so a coding agent asked "what is wrong with my database" answers from this evidence
rather than from guesses, and an HTML report for a one-off audit.

`docs/DESIGN.md` is the whole design; `docs/PLAN.md` is what happens next.
