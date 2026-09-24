# Momus control room — proposal

*Proposed 24 September 2026. Not decided: `DESIGN.md` stays the source of truth until a Decisions
row in `PLAN.md` adopts part of this.*

Momus today is a page you open. It sits beside the database, remembers what it saw, and ranks what
to fix first. The control room makes it the thing that also watches — every deploy, every morning,
every job that should have run — and tells you, instead of waiting to be looked at.

## Where this comes from

The author runs a production system alone: .NET 10, Blazor Server, EF Core on SQL Server, a few
dozen scheduled jobs, one pod. For a month a coding agent did by hand what this document proposes
Momus does by itself:

- **A weekday morning digest**, opened in the browser at 07:30. It diffed the night against a
  seven-day baseline and led with what had no precedent. The first morning it surfaced a login
  failure that had been climbing for a week and that no dashboard showed.
- **A watch on every release.** Pin the cutover, compare before and after, prove each change by the
  changed path being exercised, check the business invariants did not move, report on a schedule.
  It caught a regression that had broken one feature completely for three days with zero server
  errors, because the failure was swallowed.
- **A before/after deploy report** for people who do not read dashboards.
- **A nightly-jobs digest**, because a job that stops firing posts nothing and silence reads like a
  quiet night. It found every nightly job running three hours late after a scheduler migration.

All of it worked, and all of it was fragile in the same few ways. The laptop that ran it was asleep.
A scheduled agent session hung waiting for an approval nobody saw and silently swallowed the slots
after it. A cloud runner could not reach the internal monitoring host. An unattended job could not
publish its own report. Every release rebuilt the same machinery, and rediscovered the same traps:
a detector that found the wrong one of two deploys, a weekend compared against a weekday, a bursty
client's duty cycle read as a regression, a counter function that lost a job whose series started
and ended inside the window.

That is the argument for putting it in Momus. Momus already lives beside the system rather than on
a laptop, already keeps history, already notices a deploy without being told, and already has the
`regression` rule. What it lacks is push, the release as a first-class object, and memory of what
normal looks like on a Monday at 09:00.

## Rules the month paid for

| What happened | Rule |
| --- | --- |
| The digest opened itself at 07:30, and was read every day | **Push by default.** A finding nobody opens the page for does not exist. |
| ~1,900 identical lines a night from one worker | **Novelty over volume.** Lead with what has no precedent; fold what is steady. |
| A late report produced different numbers from an early one | **Anchor to the change.** Every window is measured from the cutover, never from now. |
| "Successful logins are the proof the identity-provider change works" | **Proof, not absence.** A change is Proven, Not yet, or Regressed. No errors is not proof. |
| Weekend movement is 4 a day against 130 on a weekday | **Baselines know the calendar.** Same hour of the same weekday. Silence is a finding only when that hour is normally busy. |
| A page of "noise floor, do not re-triage" notes | **Remember the noise.** Known floors carry a reason, a rate and an expiry, and live in Momus. |
| "19 of 19 jobs ran"; the laptop asleep | **Silence must be loud**, including Momus's own. |
| A model-based triage step was built and removed | **Rules decide urgency.** A model sits behind the copy button and MCP, never in the loop. |

## What it adds

Six pieces, each on top of something that already exists.

### 1. Event log and live feed

An append-only `events` table: a version seen for the first time, an insight opened, reopened or
closed, a finding first seen, a watch check changing state, an expectation missed, a notification
sent or failed, an acknowledgement. Every other piece writes here; the feed, the digest and the
notifications all read from here, so they cannot disagree about what happened.

- `GET /api/v1/events/stream` as server-sent events. The page subscribes with a few lines of
  `EventSource`, which keeps D7: Razor, no Node toolchain. It replaces the fifteen-second reload on
  the pages that show the feed.
- `momus tail --server <url> --min P2` prints the stream to a terminal. A coding-agent session can
  wait on it and wake on an event, instead of polling on a timer the way every release watch did.
- MCP gains `momus_feed(since)` and `momus_watch(version)` beside the M4 tools.
- Every event that points at an insight keeps the evidence-pack copy button.

### 2. Release watches

Momus already recognises a deploy as a version turning up for the first time, and `regression`
already judges the newest running version. A watch makes the release the object:

- **Opened automatically** when a version first reports. Nobody has to ask for one.
- **Anchored to the cutover.** Before and after windows are the same length and end and start at the
  first report of the new version, so a reading taken at 09:00 and one taken at 17:00 agree on the
  same span.
- **Chained.** A second version inside an open watch joins it, with both cutovers on the timeline,
  rather than silently replacing the anchor. Two deploys in one evening is normal.
- **Per-operation verdicts.** Every operation whose numbers differ between the windows, and every
  operation named in the release's checks, is Proven (exercised, succeeding), Not yet (not exercised
  since the cutover — with the hour it usually is), or Regressed.
- **Checks as data, optional.** `POST /api/v1/deploys` from CI with the revision, a short narrative
  and a list of expectations (operation or statement, `op`, value, `min_hours`, `absent_is_zero`).
  Deploy *detection* stays inferred; the post only adds meaning.
- **Closes itself** at a set age with a closing report: the deploy report, generated rather than
  written.

This needs one additive contract change. `IngestOperation` carries a count, durations and queries,
but no outcome, so today "Proven" could only mean "called". Add to each operation:

- `outcomes`: counts by status class (`2xx`, `3xx`, `4xx`, `5xx`) for HTTP operations, and
  succeeded / failed for named ones;
- `exceptions`: `{ type, callSite, count }`, where the call site is the first application frame —
  the same walk `CallSites` already does for statements. Exceptions that the application catches
  and turns into a response never reach the middleware, so also read the OpenTelemetry `exception`
  events on the request's `Activity`, which is where well-instrumented apps record them.

No exception message leaves the process, which keeps the client's privacy rule. The type plus the
innermost application frame turned out to be exactly the grouping the morning digest converged on
after a week of false "new" items from messages that carried ids.

### 3. Digest: since you last looked

- **The window runs from the previous digest**, not a fixed 24 hours, so Monday covers the weekend.
- **Novelty rules**: anything with no occurrence in the baseline; anything at least 3× its mean
  *and* above its previous peak; low-volume errors that are rising; a severity change reported as
  a severity change rather than as something new; anything that stopped after a cutover.
- **Rated P1 (act now) to P4 (for information) by rules**, each card showing the reasons for its
  rating. Errors before warnings. Everything steady folded into one line.
- Delivered on a schedule to the configured channels. On the page, the same thing is a per-viewer
  "since you last looked" banner.

### 4. Notifications

- **An outbox.** A `notifications` table with delivery state, attempts and the last error, retried
  in the background. A failed delivery is shown on the Diagnostics tab, because an alert that
  quietly did not send is the worst kind of silence.
- **Channels**: a generic webhook, Slack incoming webhook, email over SMTP, and ntfy. ntfy puts a
  push notification on a phone and needs nothing from the server but outbound HTTPS, which suits a
  self-hosted Momus with no public address.
- **Routing by rating**: P1 to the phone now, through quiet hours; P2 to chat; P3 and P4 wait for
  the digest.
- **Hysteresis**: two consecutive evaluations before anything fires. One message per insight
  lifetime, keyed on the insight's stable key, and one more when it resolves.
- **Acknowledge and snooze** from a signed link in the message.

### 5. Expected activity and heartbeats

Declare what should happen, and Momus reports when it does not:

- a named operation at least once a day before a given hour (a nightly job);
- an operation at least N times an hour inside business hours (logins);
- a digest, a scan and an ingest window at least every interval.

Named background work already exists (`MomusOperation.Begin`). The `Momus.Client.Switchboard` and
`Momus.Client.MediatR` adapters planned for M4 would name every handler without any code.

Momus reports on itself too: a daily "all quiet — N scans, M windows" message, and an optional
outbound ping to a dead-man's-switch URL, so a stopped Momus is noticed from outside rather than
read as a quiet night.

### 6. Known floors and hour-of-week baselines

- **Mute becomes "known, with a floor"**: a reason, an expected rate, an expiry. Momus alerts only
  above a multiple of the floor, and the floor shows on the card. A scheduled integrity check or
  backup that empties the buffer pool every morning is the first example any SQL Server user will
  meet: Momus's own memory-pressure check will fire at the same minute every day.
- **A 168-bucket hour-of-week profile per series**, exponentially weighted over about four weeks. It
  is constant-size, independent of the 7-day rollup retention, and makes "same hour, same weekday"
  the default baseline for every rule that compares.

## The page

A control-room view beside Fix first, designed to sit on a second screen:

- **Status strip**: app, environment, version and time since its cutover, scan age, window age, and
  one verdict colour driven by the highest open rating.
- **Now**: open P1 and P2 items, and the open release watch with its per-operation verdicts.
- **Feed**: the live event stream.
- **Expected activity**: each declared expectation, green, amber or red.
- **Next**: the next digest, the next watch report, the next expected job.
- `?kiosk=1` drops the navigation.

## What it does not do

- **No model in the loop.** Ratings are rules, and the reasons are on the card. A coding agent reads
  the evidence through the copy button and MCP.
- **No charts for their own sake** (DESIGN.md). The one chart question is still "did this start when
  we shipped something".
- **No OTLP ingestion** (D5). Phase 4 reads other stores through their query APIs; it does not
  become one.
- **No writes to the monitored database.** The read-only rule stands.

## Phases

| | Name | Done when |
| --- | --- | --- |
| 0 | Beside a real system | M1's week served against the author's staging database, and each Phase 0 finding below closed or deliberately deferred with a Decisions row. |
| 1 | Feed and push | A High insight opened on the server reaches the author's phone within a minute, and stopping Momus is reported by the outside ping within its interval. |
| 2 | Release watches | Deploying the demo shop's slow build opens a watch that marks the cart path Regressed and an untouched path Proven, without anyone telling Momus about the deploy. |
| 3 | Digest and memory | The author's hand-built morning digest is switched off for its database and application half, because Momus's says the same things or more. |
| 4 | Probes, optional | Only if Phase 3 leaves a gap that matters: Prometheus and Loki queries, and count-only SQL invariants. |

## Phase 0 findings

Found while preparing the first deployment beside a real system — staging only, the Momus server on
a separate monitoring host, the application in a container. Each is a place where the demo stack
could not have shown the problem.

1. **Instance-wide views on a shared instance.** On SQL Server, `sys.dm_exec_query_stats` and
   `sys.dm_exec_requests` cover every database on the instance, and the top-CPU and blocking checks
   do not filter. The staging database shares its instance with a demo database running the same
   code, so the same statements would appear twice with identical fingerprints, and half of them
   would belong to another database. Filter top-CPU on the plan's `dbid` attribute
   (`sys.dm_exec_plan_attributes`) and blocking on `r.database_id`, both against `DB_ID()`. Waits and
   page life expectancy describe the instance by nature and should say so. Check the Postgres side
   for the same assumption. **Fixed (D24)**, Postgres's session and lock-wait checks included, each
   proven by a live test that fails without it.
2. **Long-lived requests.** `MomusMiddleware` opens one operation per request and completes it when
   the request ends. A Blazor Server circuit is one WebSocket request for as long as the tab is open,
   so every statement the UI runs is expected to land in one operation per circuit: reported only
   when the tab closes, and repeating the same statement far more than five times, which is exactly
   what `n_plus_one` looks for. To be confirmed on the first run; an operation named after the hub
   path on the Queries tab is this. The fix is to open no operation for WebSocket and server-sent-
   event requests and let those statements fall through to the ambient path, which names them from
   `Activity.Current` — a mediator's `Send <Request>` span or a job's root span when the application
   has them. The Switchboard adapter is the complete answer. The same applies to SignalR and gRPC
   streaming anywhere. **Fixed (D25)**, and confirmed first: with a WebSocket and an event stream
   held open, the client had reported 0 of their statements. Preparing the test found something
   worse: a statement that ran after its request had ended — a fire-and-forget task, a circuit on
   the long-polling transport — made the *application's own query* throw, because the operation's
   name was read from an `HttpContext` ASP.NET Core had already disposed. gRPC streaming is still
   one operation per call; nothing in the request says it will be long.
3. **Ingest has no authentication.** Fine on loopback, which is what the defaults assume. Off-box,
   anything that can reach the port can post windows, read the UI and add targets. Add a shared key
   (`Momus:ApiKey` on the client, sent as a header; `MOMUS_INGEST_KEY` on the server), or document
   exposing only `POST /api/v1/ingest` through a proxy and reaching the UI through a tunnel.
   **Fixed (D26)** with both halves and no proxy: `MOMUS_INGEST_KEY` / `Momus:IngestKey`, and
   `MOMUS_INGEST_PORT`, a second port that serves only ingest and the health check.
4. **A container build without `.git` has one version forever.** The SDK appends the commit to
   `InformationalVersion` only when it can see the repository, and most Dockerfiles exclude it. Every
   deploy then reports the same version, so there are no deploy markers and `regression` can never
   fire. Document passing the revision (`-p:SourceRevisionId=$COMMIT`), and have Diagnostics warn
   when an app's version carries no revision and has never changed.
5. **The SQL Server grant in the README is incomplete.** A login that connects with `Database=X`
   needs a user in X, and the missing-index check's `OBJECT_NAME(object_id, database_id)` returns
   NULL without metadata visibility. The minimum is `VIEW SERVER STATE` at the server, and
   `CREATE USER … FOR LOGIN …` plus `VIEW DEFINITION` in the database.
6. **Target ids must match by accident.** A target from `MOMUS_TARGETS__n__NAME` gets the slug of
   that name as its id; the client names a target by the slug of the database name. When the client
   does not share connection strings — every non-loopback setup — the application's queries join to
   the scanned database only if the two happen to be equal. Match on provider and database name as
   well as id, or at least say so in the README.
7. **The container could not scan SQL Server at all.** The Alpine runtime image runs .NET in
   globalization-invariant mode, and Microsoft.Data.SqlClient refuses to connect in it: every scan
   ended with "Globalization Invariant Mode is not supported" before a check ran. The Postgres demo
   never showed it. Fixed in the Dockerfile (ICU plus `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false`),
   verified with `scan` and with `serve` against SQL Server 2022. It costs 38 MB (200 to 238 MB;
   DESIGN.md's "well under 150 MB" was already out of date). Worth a CI job that scans a SQL Server
   container, so the provider cannot break unseen again.
8. **Idle waits read as findings.** On a quiet SQL Server 2022 the wait check's top five were
   `SOS_WORK_DISPATCHER` (94%), `SQLTRACE_INCREMENTAL_FLUSH_SLEEP`, `PWAIT_EXTENSIBILITY_CLEANUP_TASK`,
   `QDS_ASYNC_QUEUE` and `BROKER_EVENTHANDLER` — all background waits that mean nothing. The benign
   list predates 2019/2022. A staging instance is quiet most of the day, so without this every scan's
   waits are noise. **Fixed**: the list now holds what two idle 2022 instances actually reported,
   and `WaitStatsTests` pins both directions — those waits benign, a dozen real ones never.

## Decisions this needs

- **Alerts are Pro under D10.** A month of doing this by hand says one person cannot trust a tool
  that does not push. Proposed: the free tier gets the feed, the digest, one channel and the
  heartbeat; Pro gets routing across several channels, quiet hours and escalation, and
  acknowledgement for a team. That contradicts D10 as written.
- **Phase 4 against D5, D6 and the read-only rule.** SQL invariants read application tables, not
  statistics views. If they are built: opt-in, their own login, a statement timeout, snapshot
  isolation, and a count-only contract — Momus stores an integer, never rows, which keeps personal
  data out of the store by construction.
- **The model line.** Recorded here so it is not relitigated per feature: rules rate, a model reads.
