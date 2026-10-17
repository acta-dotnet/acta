# Design: principles and settled decisions

The non-negotiable identity of Acta and the rationale behind it: the "why" manifesto. Every other
document defers to this one. Reopening anything here is a category-reshaping change, not a refinement.

The vocabulary itself (Job, Execution, Attempt, Status, namespace, durable slot, provider, the three
identities, the substrate tables) is defined once in [concepts](./guide.md#concepts-and-glossary). This
document explains why the model is shaped that way; it does not re-teach the mechanics. Where the code
lives is in [source layout](../CONTRIBUTING.md#source-layout). Whether Acta fits a problem is
[`choosing-acta.md`](./choosing-acta.md).

## What Acta is

Acta is SQL-native durable work for .NET. It records jobs, retries, schedules, checkpoints, events, workers, and operator controls in your database, so durable work lives in SQL and the runtime state is inspectable with ordinary queries.

One work-unit shape for every kind of work, source-generated dispatch with zero runtime reflection on the hot path, one handler and one SQL file per dialect for every operation, durable coordination composed from ordinary jobs, and an append-only event ledger of every lifecycle transition and execution attempt.

**Leaderless** means every worker is a peer, every claim is a competitive race, and there is no persistent leader role, election, or lock. Crash recovery rides every worker's heartbeat, and the other system maintenance runs as ordinary recurring `[Job]`s claimed via the same `Status: Ready -> Executing` discipline as user code; singleton-per-namespace comes from row-level claim mutual exclusion, not coordinated leadership.

## What Acta is not

Deliberate non-goals. Adding any of these is a category-reshaping change. Acta does not provide:

- deterministic workflow replay or an event-history replay engine. Acta uses checkpointed re-entry over named durable slots, not history reconstruction.
- BPMN or a visual workflow designer.
- a message bus, broker, or transport abstraction.
- a sidecar or auxiliary service to deploy.
- a hosted control plane, leader role, or cloud scheduler.
- hosted workflow SaaS orchestration. Acta has tenant support for job scope, audit, and query; it is not a hosted multi-tenant orchestration service.
- a generic workflow-graph table or generic checkpoint-JSON blob. The substrate is typed tables (`checkpoints` discriminated by kind with typed columns, plus `steps`), not an untyped graph.
- guaranteed exactly-once effects against arbitrary external systems. No durable executor can make
  a card network, email provider, webhook receiver, and SQL database share one exactly-once commit.
  Acta gives you at-least-once jobs, checkpointed durable steps, deduplication keys, and
  `AtMostOnce()` step semantics; applications still own external side-effect safety.

## The four claims

1. **Acta observes work lifecycle, not message flow.** Every Job has a durable record, every transition is captured in `JobEvent`, every job tree has lineage, all queryable in SQL. The event ledger persists past Job retention.
2. **SQL is the operational interface; curated views are the supported query surface.** Acta's runtime queries the same database operators query. No proprietary query layer, no opaque storage format. Operators write ad-hoc queries against production data without coordinating with the framework; the curated views are the stable surface, storage tables are not a compatibility contract.
3. **No control plane, no leader.** Every worker is a peer. System maintenance is recurring `[Job]`s claimed competitively per namespace, and crash recovery is a pass whichever heartbeat finds due takes, not leader-elected work. No `Supervisor`, no `ILeaderTask`, no auxiliary services. The worker process is the unit of operation.
4. **Distributed correctness without distributed operations.** Maintenance scheduling, lease reclamation, RCSI-correct concurrency, rate limiting, transition event ledger, idempotency horizons, deadline-bound retries are done in code. The target is one operator on commodity SQL Server / Postgres with nothing else to run. Execution is at-least-once over SQL claims and leases; handlers own side-effect idempotency.

## The core principle

**Everything externally scheduled, claimed, retried, paused, cancelled, and audited as work is a
Job; Job-internal state lives in the `checkpoints` and `steps` substrate tables, not as Jobs.** The mechanics
(which slot writes which checkpoint kind, the `(JobId, Kind, Name)` keying, the CASCADE FKs) are in
[concepts](./guide.md#concepts-and-glossary).

The reason is the two-tier model: it keeps heavy machinery (claim queue, lease, audit, alerting) at
the unit-of-work tier and lightweight per-Job substrate at the Job-internal tier, so one
unit-of-work tier (the `jobs` row plus its 1:1 `runtimes` row) carries every external access
pattern while internal state does not pay the per-claim cost.

## Substrate-generality principle

**Acta tables are substrates, not record types. Acta APIs are surfaces, not per-feature endpoints.**

Adding a table requires showing that no existing substrate's access pattern, lifecycle, or invariants can carry the new use case. Generic columns, new discriminator values, and application-layer logic are the preferred extension mechanisms. **Table count is a budget.**

Examples:

- Coordinating jobs are ordinary jobs, no dedicated coordinator table.
- The `DeduplicationKey` column doubles as the operator-facing secondary key: no separate name-resolution table.
- Domain metadata lives as fields on the input record, cross-cutting searchable metadata as `Tag` rows: no `JobMeta` table.
- System jobs (`sys.alerts`, `sys.retention`, and `sys.outbox` where a relay is registered) are ordinary `Job` rows in the target's own `JobNamespace`, not a global namespace.
- Per-attempt history is one `JobEvent(job.execution-finished)` per `(JobId, ExecutionNumber)`, carrying the worker and the handler's duration: no separate execution table. An attempt in flight is its `runtimes` row.
- Lanes earned the `lanes` table. A laned enqueue needs a row to lock until it commits, so two producers cannot interleave out of order, and the lane name is stored once. A hash on `runtimes` has no row to lock and can merge two lanes; `locks` rows are expiring leases, a different lifecycle.

A worked example (see [the settled-decisions ledger below](#settled-decisions-ledger) § Substrate): the simple substrate is one merged `checkpoints` table discriminated by `Kind`, spending one table on five slot features; only `steps` earned its own table by carrying a genuinely different shape (attempt counters, retry budget, stored results).

The principle extends to APIs: adding a method requires showing no existing surface can carry the use case via composition (`IJobs.GetJobIdAsync` + standard `JobId` lifecycle, vs proliferating `*ByName` overloads). API count is a budget, same as table count.

## Vocabulary discipline

**`Execution`** is what gets recorded: every run of the handler, success or failure, as a
`job.execution-finished` event. **`Attempt`** is what counts against the retry budget
(`MaxAttempts`) when it fails. Names that drift this are rejected: `MaxFailedExecutions` (a
successful execution is also an execution), `MaxRetries` (off-by-one trap, `MaxAttempts = 3` means
three tries), a `JobAttempt` table (the finished event is the per-attempt record).

**`Status`** is the uniform column name for the operator-facing lifecycle enum; per-execution
outcome is the event's `ExecutionStatus`; steps and checkpoints carry `status_code` like every other
table, and only their two views name the decoded column `state`. The `runtimes` row stores current state and events explain transitions: the machine
code is `ReasonCode`, the prose is `ReasonMessage`, both on the event, never on the runtime row.

A **firing** is one scheduled occurrence of a recurring `[JobSchedule]`; the **execution** is the
handler run that processes it (1:1 in steady state; misfire policy decides missed firings).

## Settled decisions ledger

Every load-bearing decision, one line each, the call plus its reason. Vocabulary is defined in
[concepts](./guide.md#concepts-and-glossary); the rationale behind the model is the principles above.
Reopening an entry means writing a proposal, not editing this file.

### Identity

- **Leaderless.** Every worker is a peer and every claim is a competitive race; no leader role, election, or lock. *Reason:* removes failover and split-brain as operational concerns.
- **One work-unit shape.** All work units (one-shot, recurring, coordinating parents, system maintenance) share one shape (a `jobs` row plus its 1:1 `runtimes` row); no `JobKind` enum, no per-kind tables. *Reason:* one substrate carries claim, lease, audit, and retention without per-kind branching.
- **SQL is the operational interface; curated views are the supported query surface.** The runtime and operators query the same database; no proprietary query layer. Storage tables are not a compatibility contract. *Reason:* operators write ad-hoc production queries without coordinating with the framework.
- **No reflection on the hot path.** Source generators emit all dispatch and persistence code; AOT-clean by construction. *Reason:* trim-friendly, no reflection cost per attempt.
- **Category: durable work ledger for .NET.** Never positioned as a workflow engine, message bus, or scheduler; "workflow" appears only in non-goals, comparisons, and search metadata. *Reason:* the positive category claim stays singular.

### Substrate

- **Fourteen installed routines carry the hot ledger mutations on PostgreSQL and SQL Server; every other operation is inline SQL.** Enqueue, claim, complete, the step writes, checkpoints, leases, meters, job signals and sleep timers, recovery, and retention are routines; registration, alerts, locks and slots, the outbox inbox, reads, control verbs, and the rest run as embedded SQL, and SQLite runs every mutation inline inside an owned transaction. `SqlCommandCatalogTests` pins the list, so adding a routine is a decision. See [SQL execution policy](./internals/sql-execution-policy.md). *Reason:* a routine buys one round trip and a server-side plan, and costs a DBA-run package revision on every edit; only the hot paths earn that. Retention and recovery stay routines although they are periodic: a retention sweep calls its purge once per slice and batch, and both re-check rows under the locks they take, which inline SQL could only do with temp tables built on every call, measured two to three times slower.
- **Source code is the source of truth.** Entity classes + XML docs are canonical; `data-model.md`, `code-families.md`, `operations.md`, `conformance-contracts.md`, the provisioning scripts, and each provider's baseline migration are emit-generated and CI drift-gated. *Reason:* one declaration site, no doc drift.
- **Every operation is one handler over `IDbSession` and `ISqlDialect`, written once, with its SQL beside it per dialect.** Providers own executable SQL and dialect binds, not handlers. No production generic `InsertAsync`/`UpdateAsync`/`DeleteAsync`. *Reason:* SQL enforces atomicity while one shared C# implementation holds provider-independent policy.
- **`JobEvent` is the execution ledger AND the audit timeline.** One `job.execution-finished` event per `(JobId, ExecutionNumber)` carries per-attempt history; no per-attempt table. *Reason:* one append-only substrate, one retention sweep.
- **`JobEvent` carries an optional `detail`/`detail_format_id` payload pair**, paired by `ck_events_detail_pair`; format `0` means no detail. Richer event-specific data still goes to OTel spans and logs; durable state lives on the entity row. *Reason:* keeps `JobEvent` frugal while still giving free-form (text) or structured (json) context beyond `ReasonCode`/`ReasonMessage` a home.
- **One merged `checkpoints` table; `steps` stays separate.** Variables, signals, timers, progress, and child latches share one `(job_id, kind_code, name)` table; steps keep their own (attempt counters, retry budget, stored results). *Reason:* one physical shape for every simple slot; only steps carry a genuinely different shape.
- **Single-tier retention on `JobEvent`.** One knob (`JobEventsRetention`, default 365 days) sweeps every row. *Reason:* the two-tier variant added a knob nobody tuned.
- **`JobResult` is keyed by `(JobId, ExecutionNumber)`** with CASCADE FK. *Reason:* the natural identity callers already address by.
- **Timer checkpoints are arm/consume only.** `Pending → Consumed`; no `Cancelled` state; `ResetJobState` deletes rows outright. *Reason:* a state the runtime cannot reach is schema debt.
- **A `Lock` carries no acquisition timestamp.** Lifecycle is `expires_at_utc` plus a per-hold `hold_token` that extend and release compare. *Reason:* no code path ever consulted the acquire instant.
- **`jobs` + `runtimes` split: immutable identity vs mutable state.** `jobs` is never UPDATEd; the 1:1 `runtimes` row owns every mutable column with `runtimes.version` as the CAS token, and execution ownership lives on it, not in a lease table (that variant cost ~30-40% drain throughput and was reverted). *Reason:* the hot path rewrites a narrow row, and identity is immutable by construction.
- **Reasons are events, not row state.** Why a job failed/paused lives on `JobEvent`, never on a `runtimes` reason column; snapshots and outcomes expose state only. *Reason:* a denormalized current-reason goes stale on re-arm; an append-only event cannot.

### Boundaries

- **Three-key job identity.** `JobId` internal (bigint), `JobRef` public (`job_` + Crockford Base32 over a C#-allocated UUIDv7), `DeduplicationKey` caller-defined; `events`/`alerts` denormalize `job_ref` at INSERT so it resolves past purge. *Reason:* numeric ids leak ordering/volume and exceed JavaScript safe integers.
- **`JobNamespace` is the service-owned execution boundary.** One deployable service owns one namespace; replicas share it. *Reason:* namespace identity = deployment unit, not tenant marker.
- **Workers only claim within their own namespace.** *Reason:* blast-radius firewall, a bug cannot escape its namespace; cross-namespace enqueue allowed, cross-namespace claim impossible by index seek.
- **One `WorkerRuntime` per `Run(...)`; enqueue-only hosts register nothing.** A process running several `Run(...)` calls fans out one claimer, executor set, finalizer, and heartbeat per namespace. `Reference(...)` and enqueue-only frontends create no worker registration and write no catalog rows; typed enqueue resolves `(namespace, jobName)` to ids via SQL JOIN at INSERT time. *Reason:* routing lives in the database, so worker-hosted and enqueue-only deployments share one enqueue code path and a frontend can enqueue for a namespace it never claims.
- **Tenant is validated identity, not an isolation primitive.** The catalog is global per store; enqueue validates and stamps it, children inherit it, and suspension is admission control only. No tenant-aware claim fairness, per-tenant limits, data security, or failure domain; that isolation = application enforcement + separate deployments. *Reason:* an audit/query scope is a cheap nullable column; fairness would widen every hot index.
- **System jobs are namespace-local.** `sys.alerts`/`sys.retention`/`sys.outbox` live in the target's own namespace, and so does each recovery pass. *Reason:* a global namespace would violate the firewall.

### Behavior

- **`MaxAttempts` is the unified cap** on jobs and steps; `Reschedule`/`Suspend`/`Pause` never consume the budget. *Reason:* one retry mental model across both tiers.
- **Strict priority ordering** in the claim path; no aging, no weighted fairness; within a priority and next-run time the claim breaks ties by job id, which is enqueue order for one producer enqueuing sequentially; concurrent claims promise no global FIFO; strict per-key order is a lane. *Reason:* predictable semantics; fairness = separate namespaces.
- **`ConcurrencyKey` is execution-time admission (at most `ConcurrencyLimit` per key, 1 by default), not rate limiting or ordering.** Enforced by a lock taken after claim; losers bounce budget-neutrally. *Reason:* claim-time gating collapsed namespace claim throughput under a hot-key backlog (~20/s vs 500-2,500/s exec-time).
- **Delayed enqueue: relative delay is DB-clock; absolute is the only caller-instant path.** The two are mutually exclusive. *Reason:* an enqueue-only frontend must not silently depend on its own clock.
- **Recurring schedules are a single slot job per `(namespace, definition)`** carrying many `schedules` rows; due schedules coalesce into one execution; cursors computed in C#, applied in SQL. *Reason:* single-cursor claim scan, no per-firing row inflation, no Cronos in SQL.
- **Misfire is a two-strategy per-schedule choice.** `Skip` (default, forward-only) or `CatchUpOnce`. *Reason:* forward-only avoids startup catch-up bursts; catch-up is opt-in.
- **`job.audit_level_code` is a per-job snapshot** copied from the definition at enqueue; unset defaults to `Audit`. *Reason:* hot-path read with no join.
- **`AuditLevel=Off` records no transition reasons.** No `JobEvent` rows means no persisted reason; current state and `FailureCount` remain. *Reason:* leanness is the point of `Off`; no hot-row reason columns to paper over a disabled trail.
- **`Job.Status = Failed` is terminal.** Non-terminal failures stay `Ready` with `NextRunAtUtc` advanced. *Reason:* terminal is terminal.
- **A claim starts its jobs and writes no event.** The claim moves each row `Ready → Executing` under the lease; no status sits between claimed and running, and the `runtimes` row is the attempt in flight. *Reason:* a separate start cost one commit per job, and the window it made visible lasted milliseconds; a started event cost most of a SQL Server claim, waiting on the events table's last-page latch, and told nothing the finished event does not.
- **One execution path, no profile.** A worker claims a row for every free executor. Every completion commits in a database transaction, in one batch the finalizer writes in one call whatever its shape, and a drain lands every completion the executors produced; there is no relaxed-durability mode. *Reason:* a terminal result never reverts after an unclean shutdown, which is the point of a durable ledger; a group-committed profile bought throughput by giving that up, and a buffered profile bought a visible claimed state at the price of a commit per job.
- **Pause is sticky.** No `PauseUntilUtc`, no auto-resume sweep. *Reason:* bounded pause is composable by the caller.
- **Backoff is one expression column** (`backoff`, e.g. `"1m..8h x2 ±10%"`, with override/effective pair). *Reason:* the attribute is authored as a single DSL string, so storing parsed knobs was lossy and unfaithful; the shared parser validates at compile time and at override write, and SQL never computes with it.
- **Crash recovery rides the heartbeat, not a job.** The heartbeat call that finds the namespace's `recovery_due_at_utc` passed moves it one lease on in the same statement, and that worker runs the pass beside its heartbeat. *Reason:* a recovery job could be stranded by the crash it exists to repair, which needed a slot monitor and a repair routine of its own; the heartbeat runs on every worker anyway, so the gate costs no statement, nothing can strand it, and a worker whose executors are all busy still sweeps. The interval is the lease because a lapsed lease is what a pass reclaims.
- **System jobs are alerted like any job.** `sys.retention` failures project alerts at `Critical` (`AlertProfile = SysCritical`) to the implicit `default` log channel unless overridden. *Reason:* maintenance failures ride the same alert substrate they maintain.

### Storage and naming

- **The migration history is frozen from 1.0.0.** Every schema change is an additive `M{datetime}_{name}` migration (new columns, tables, routine and view bodies): no renames, drops, retypes, or renumbered code values, and the baseline is never re-cut; `schema reset` remains only as a rebuild-and-compare check that the emitters still reproduce the committed baseline. *Reason:* closing the public, physical, and numeric vocabulary together is the 1.0 promise, and a migration a deployed database has run is history that cannot be folded away.
- **Tag scope is exact attachment.** A tag scope identifies the exact target to which searchable metadata is attached. Tag scopes do not inherit, fall back, propagate, or participate in precedence resolution. *Reason:* a searchable annotation must never acquire configuration precedence semantics.
- **Database schema is an install-time option per deployment**, default `acta`. *Reason:* co-deployment per DB without dynamic routing.
- **Identifier convention:** plural, prefix-free, underscore-free tables; `lower_snake_case` columns; `ix_`/`ux_`/`pk_`/`ck_`/`fk_` prefixes; every persisted name explicit on its attribute; no quoted identifiers; CLR entities keep domain names (`Job*`). *Reason:* unquoted names work on every target DB and survive convention renames.
- **Varchar sizes come from a fixed ladder:** 16, 32, 64, 128, 256, 512, 1024, max. *Reason:* SHA-256/512 hex fit natively; no bespoke widths.
- **All datetime columns are `datetime2(3)` UTC** with a `Utc` suffix. *Reason:* one precision, one zone, self-describing names.
- **Three identifier shapes: name, key, display text.** A name (namespace, definition, schedule, signal, tag, lane, tenant, rate meter) is ASCII dotted kebab, trimmed and lowercased on input, so it compares equal under any collation. A key (deduplication, concurrency, lock, correlation) is printable ASCII kept exactly as given and compared bytewise; the compared key columns carry a binary collation on SQL Server (`DbColumn(CaseExact = true)`). Display text is Unicode within a cap and never compared. *Reason:* Acta-owned identifiers survive every collation, terminal, and URL, and a caller's identity is never altered.
- **Three text tiers via `DbKind`:** `AsciiString`, `UnicodeString` (cold/mid tables only), `BinaryPayload`; never `jsonb`. *Reason:* hot tables stay narrow; validation lives at the application boundary.
- **SQLite tables are `STRICT` and keep `AUTOINCREMENT`.** *Reason:* the file enforces the declared types, as the servers do, and a rowid is never reused.
- **Optimistic-concurrency token is `int Version`**, manually incremented; no `rowversion`, no `xmin`. *Reason:* uniform across providers and operator-observable.
- **Server-side defaults via `DbColumn.Default`** for audit timestamps and version columns. *Reason:* the DB clock is authoritative for "when was this row written"; no NTP-drift hazard.
- **Audit tables (`JobEvent`, `JobAlert`) carry no enforced FKs** to retention-deletable rows; they denormalize identifying facts at INSERT. *Reason:* an FK to a purgeable row collapses the analytics window.

### Codes

- **Every persisted code family is a byte-backed BCL enum + source-generated `<Name>Extensions`.** SQL Server stores it as `tinyint`; PostgreSQL uses `smallint`; SQLite uses `INTEGER`. *Reason:* one byte of logical identity is sufficient for every catalogue, including events and reasons.
- **A numeric code is identified by `(code family, id)`.** IDs are stable only within their family; enum members carry programmatic meaning and textual codes carry operator-facing meaning. Numeric grouping is a readability convention and runtime code must use explicit member switches rather than ordering, division, or range predicates. *Reason:* compact persistence must not become an encoded semantic language.
- **Canonical lifecycle values are conventional, not behavioral:** success `100`, failure/exhaustion/dead `200`, cancellation `220`, interrupted/indeterminate `230`, retired/deprecated `240`. *Reason:* visual alignment aids SQL inspection while exhaustive member logic remains authoritative.
- **`code_kind` is declared explicitly via `[CodeKind("kebab-name")]`.** *Reason:* renaming a C# enum must not silently rename an operator-facing discriminator.
- **No `codes` table, no generated per-table views, and no value-list CHECK on any code column.** The full decode surface is `code-families.md`; SQL operators get curated `_view` surfaces with generated name-only `CASE` decodes for common workflow fields. *Reason:* one C# code truth without a sync table, and adding a value is a release, not a DDL migration a DBA runs.
- **Id `0` is the family's default or `Unspecified`, and an unknown id decodes as it.** `FromId`, textual and JSON conversion read an id this build does not know as the id-0 member; the SQL compares exact ids, so an older worker's claim, retention, and delivery queries pass over a value it does not know. A value that changes engine behaviour ships readers first and writers a release later. Retired ids and text codes are never reused, and `255` is never assigned. *Reason:* a row a newer build wrote must read back on an older one during a rolling deploy, and a zero default cannot masquerade as a real value.
- **`JobPayloadFormat` is the sole `255` exception.** `0` means no payload, `1..127` is framework-owned, and `128..255` is consumer-owned. *Reason:* payload formats are an extensible registry rather than a closed catalogue.

### Packages

- **Packages:** `Acta` (the SDK, the operations, the engine, and database access), the providers, `Acta.AspNetCore`, `Acta.Redis`, and `Acta.Testing`. *Reason:* the public-API contract test is the boundary between what a consumer may use and what stays internal, so a second assembly for the engine bought nothing, and one folder per slice is the point: an operation's public types, handler, and SQL read together.
- **Relational-provider baseline.** SQL Server, PostgreSQL, and SQLite ship side-by-side against one provider-neutral entity model. *Reason:* shipping distributed and embedded providers in lockstep prevents single-provider assumptions.
- **No in-memory provider.** *Reason:* it would pass tests the real providers fail.
- **Visibility default is `internal sealed`**; a type is public only when it is part of the consumer surface. *Reason:* the API boundary is grep-checkable, and `PublicApiContractTests` pins it.

### Structure

The terms for internal structure; where each piece lives is [source layout](../CONTRIBUTING.md#source-layout).

- **Operation:** one thing a caller or the engine can do, in one folder, `Operations/<Group>/<Operation>/`: its public types, its command or query, its handler, its row, and its SQL per dialect. The same `VerbNoun` names the folder, the SQL stem, and the facade method. *Reason:* one place to read an operation whole, in every dialect.
- **Group:** the operations of one subject (Jobs, Schedules, Alerts, ...), with one hand-written facade of one line per operation and the types the group shares. There is no mediator and no layer between facade and handler. *Reason:* a group is a folder, not a boundary; its handlers call what they need directly.
- **Engine:** `src/Acta/Engine/` runs jobs (workers, execution, recovery, retention, alert projection and delivery, the outbox relay, definition registration) and composes operations by calling their handlers. No operation references the engine. *Reason:* the dependency points one way, so an operation reads on its own.
- **Adapter:** a project that connects an external technology: the providers, `Acta.Redis`, `Acta.AspNetCore`, `Acta.Testing`.
- **Manifest:** the generated per-assembly set of job descriptors (`{Area}JobsManifest : IJobManifest`). The word is reserved for that.
- **Public facade:** the consumer surface (`IJobs` and friends), deliberately simpler than the internal structure; the public API never mirrors the folder tree.
- **Tags follow the shared-substrate model:** each target's owner owns its tag rows by scope, the physical `tags` table is one partitioned substrate, and every write goes through the Tags group's SQL under the ownership map. There is no tag-metadata API in front of it. *Reason:* tags span tenants, namespaces, definitions, and jobs; a boundary around a table all four join would be theatre.

### Provider contract

- **Provider conformance is one shared spec suite** all three relational providers gate on. *Reason:* one definition of "provider-compliant".
- **All hot-path writes are semantic operations** whose provider SQL writes the `JobEvent` in the same transaction. *Reason:* one round-trip; audit and state are atomic.
- **SQL sits beside its operation:** `src/Acta/Operations/{Group}/{Operation}/` holds that operation's file for every dialect (`X.pg.sql`, `X.mssql.sql`, `X.sqlite.sql`), the schema bootstrap's included, and the operator views sit in `Schema/Views/`. *Reason:* the SQL tree is public-facing in a product whose pitch is inspectable SQL; one folder per operation shows every dialect of it side by side.
- **Core behavior is implemented once in `Acta`.** Each operation's handler owns command building and mapping; providers own only dialects, executable SQL, and bulk binds, with no per-provider handlers. Statically gated by `ArchitectureBoundaryTests`, and `OperationLayoutTests` keeps each operation's SQL beside its handler, in all three dialects. *Reason:* one implementation of provider-independent policy, with SQL and dialects the only provider-specific surface.
- **No production raw-session API crosses into feature behavior.** `Acta.Testing` owns the test query DSL and raw ADO helpers. *Reason:* ad-hoc product writes would bypass operation-owned invariants.

### Handler contract

- **Two handler attributes and two invocation modes.** `[Job]` and `[JobSchedule]` (namespace declared at `Run(...)`; steps have no attribute); callers use `EnqueueAsync` (fire-and-forget durable) or `RunAndWaitAsync` (durable enqueue + await). *Reason:* the declarative surface stays small.
- **The handler-contract specification lives in [the handler contract](./guide.md#handler-contract)**: shapes, placement, payload inference, exception semantics, diagnostics. *Reason:* one decision record per seam.

### Tooling

- **Two source-gen surfaces.** `Acta.Generators` (Roslyn, compile-time dispatch + persistence) and `tools/Acta.Emit` (CLI, committed artifacts), split by output kind. *Reason:* Roslyn can't emit committed files; the CLI can't emit C# the compilation depends on.
- **Generated artifacts are committed**, drift-gated by `Acta.Emit check` in CI. *Reason:* reviewers see the schema delta alongside the source change.
- **Satellite DI entry points are named for the type they extend**: `{Provider}ActaExtensions`, `ActaEndpointRouteBuilderExtensions`, `ActaServiceCollectionExtensions`. *Reason:* the filename names the extension surface, so a `ServiceCollectionExtensions` suffix on an `ActaBuilder` extension would mislead.

### AOT and SQL parameter metadata

- **The runtime requires no reflection** for discovery, invocation, entity metadata, or parameter metadata; emit tooling and tests may reflect. *Reason:* AOT/trim-clean by construction.
- **Parameters bind with explicit generated metadata** (`DbParameterSpec` from `ActaSchema` specs); `AddWithValue` and value-only parameter constructors are banned. *Reason:* driver type inference bloats the plan cache and breaks max-type round-trips.
- **SQL files carry command text only**; `{{schema}}` is the sole template substitution. *Reason:* parameter metadata belongs next to the value that supplies it.

### Behavior conventions

- **Duration policy has three representations.** ISO 8601 in attributes/config, `TimeSpan` in C#, `int` seconds in the DB. *Reason:* one canonical form per layer.
- **One heartbeat rhythm:** `HeartbeatInterval` 45s is the single setting; `LeaseTtlSeconds` (x4, 180s) and `WorkerDeadAfter` (x7, 315s) derive from it, and leases and worker liveness extend on one loop. *Reason:* one tuning constant, one mental model - and since the triple must agree across replicas with nothing able to verify that at runtime, deriving holds the ratio by construction instead of by validation.
- **`TimeProvider`, not `DateTime.UtcNow`**, for anything driving scheduling, lease, or claim correctness; carve-outs are DB-rendered defaults, the `DeduplicationKey.Per*` convenience overloads, and the clock-free wakeup hint. *Reason:* testability and clock-source clarity.
- **Per-execution DI scope.** Scoped services receive fresh instances per attempt. *Reason:* the attempt is the unit of work.

## Invariants

1. The framework's identity does not change without an update to this ledger.
2. The four claims are testable: every claim about Acta in any other doc is held by a conformance test, a generated diagnostic, or a CI gate.
3. New tables and new methods carry a substrate-generality justification or are rejected.
4. The vocabulary is enforced at the analyzer level for compile-time names; documentation reviewers enforce it for prose.
5. Source code is the source of truth for everything mechanically derivable; this ledger captures only what isn't.
