# Design: principles and settled decisions

## Purpose

The non-negotiable identity of Acta and the rationale behind it: the "why" manifesto. Every other
document defers to this one. Reopening anything here is a category-reshaping change, not a refinement.

The vocabulary itself (Job, Execution, Attempt, Status, namespace, durable slot, provider, the three
identities, the substrate tables) is defined once in [`concepts.md`](../guide/concepts.md). This
document explains why the model is shaped that way; it does not re-teach the mechanics.

## What Acta is

Acta is SQL-native durable work for .NET. It records jobs, retries, schedules, checkpoints, events, workers, and operator controls in your database, so durable work lives in SQL and the runtime state is inspectable with ordinary queries.

One work-unit shape for every kind of work, source-generated dispatch with zero runtime reflection on the hot path, named-operation hot writes, durable coordination composed from ordinary jobs, and an append-only event ledger of every lifecycle transition and execution attempt.

**Leaderless** means every worker is a peer, every claim is a competitive race, and there is no persistent leader role, election, or lock. System maintenance runs as ordinary recurring `[Job]`s claimed via the same `Status: Ready -> Dispatched` discipline as user code; singleton-per-namespace comes from row-level claim mutual exclusion, not coordinated leadership.

For the system map, see [`architecture-diagrams.md`](../technical/architecture-diagrams.md).

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

## Use Acta when

- You already run SQL Server, PostgreSQL, or SQLite.
- You want durable background jobs without adding a broker.
- You want job state inspectable with SQL.
- You need retries, delays, schedules, and operator controls.
- You want deterministic tests around job behavior.
- You want a local dashboard/API for operations.

## Do not use Acta when

- You need deterministic workflow replay.
- You need a general-purpose message bus.
- You need Kafka-style streaming.
- You need BPMN or visual workflow modeling.
- You want a hosted orchestration control plane.
- Your external side effects cannot tolerate duplicate attempts, be made idempotent, or be reconciled with `AtMostOnce()` step semantics.

## The four claims

1. **Acta observes work lifecycle, not message flow.** Every Job has a durable record, every transition is captured in `JobEvent`, every job tree has lineage, all queryable in SQL. The event ledger persists past Job retention.
2. **SQL is the operational interface; curated views are the supported query surface.** Acta's runtime queries the same database operators query. No proprietary query layer, no opaque storage format. Operators write ad-hoc queries against production data without coordinating with the framework; the curated views are the stable surface, storage tables are not a compatibility contract.
3. **No control plane, no leader.** Every worker is a peer. System maintenance is recurring `[Job]`s claimed competitively per namespace, not leader-elected work. No `Supervisor`, no `ILeaderTask`, no auxiliary services. The worker process is the unit of operation.
4. **Distributed correctness without distributed operations.** Maintenance scheduling, lease reclamation, RCSI-correct concurrency, rate limiting, transition event ledger, idempotency horizons, deadline-bound retries are done in code. The target is one operator on commodity SQL Server / Postgres with nothing else to run. Execution is at-least-once over SQL claims and leases; handlers own side-effect idempotency.

## The core principle

**Everything externally scheduled, claimed, retried, paused, cancelled, and audited as work is a
Job; Job-internal state lives in the `checkpoints` and `steps` substrate tables, not as Jobs.** The mechanics
(which slot writes which checkpoint kind, the `(JobId, Kind, Name)` keying, the CASCADE FKs) are in
[`concepts.md`](../guide/concepts.md).

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
- System jobs (`sys.alerts`, `sys.recovery`, `sys.retention`) are ordinary `Job` rows in the target's own `JobNamespace`, not a global namespace.
- Per-attempt history is paired `JobEvent(job.execution.started)` + `JobEvent(job.execution.finished)` joined on `(JobId, ExecutionNumber)`: no separate execution table.

A worked example (see [the settled-decisions ledger below](#settled-decisions-ledger) § Substrate): the simple substrate is one merged `checkpoints` table discriminated by `Kind`, spending one table on five slot features; only `steps` earned its own table by carrying a genuinely different shape (attempt counters, retry budget, stored results).

The principle extends to APIs: adding a method requires showing no existing surface can carry the use case via composition (`IJobs.ResolveJobIdAsync` + standard `JobId` lifecycle, vs proliferating `*ByName` overloads). API count is a budget, same as table count.

## Vocabulary discipline

**`Execution`** is what gets recorded: every run of the handler, success or failure, as a paired
`job.execution.started` / `finished` event. **`Attempt`** is what counts against the retry budget
(`MaxAttempts`) when it fails. Names that drift this are rejected: `MaxFailedExecutions` (a
successful execution is also an execution), `MaxRetries` (off-by-one trap, `MaxAttempts = 3` means
three tries), a `JobAttempt` table (the event pair is the per-attempt record).

**`Status`** is the uniform column name for the operator-facing lifecycle enum; per-execution
outcome is the event's `ExecutionStatus`; substrate tables use `state` instead, marking them
job-internal. The `runtimes` row stores current state and events explain transitions: the machine
code is `ReasonCode`, the prose is `ReasonMessage`, both on the event, never on the runtime row.

A **firing** is one scheduled occurrence of a recurring `[JobSchedule]`; the **execution** is the
handler run that processes it (1:1 in steady state; misfire policy decides missed firings).

## Settled decisions and conventions

Every load-bearing call: substrate, boundaries, behavior, storage, naming, codes, packages,
provider contract, tooling, AOT: lives in [the settled-decisions ledger below](#settled-decisions-ledger), one line per
decision with its reason. This document does not repeat the ledger.

## Invariants this document guarantees

1. The framework's identity does not change without a settled-decision update recorded in [the settled-decisions ledger below](#settled-decisions-ledger).
2. The four claims are testable: every other artifact explains how its subject contributes to one of them.
3. The substrate-generality discipline applies to every proposal. New tables / methods carry a justification or are rejected.
4. The vocabulary is enforced at the analyzer level for compile-time names; documentation reviewers enforce it for prose.

## Settled decisions ledger
The settled-call ledger: every load-bearing decision, one line each, the call plus its reason.
Vocabulary is defined in [`concepts.md`](../guide/concepts.md); the rationale behind the model is
the principles above. Reopening an entry means writing a proposal, not editing this file.

### Identity

- **Leaderless.** Every worker is a peer and every claim is a competitive race; no leader role, election, or lock. *Reason:* removes failover and split-brain as operational concerns.
- **One work-unit shape.** All work units (one-shot, recurring, coordinating parents, system maintenance) share one shape (a `jobs` row plus its 1:1 `runtimes` row); no `JobKind` enum, no per-kind tables. *Reason:* one substrate carries claim, lease, audit, and retention without per-kind branching.
- **SQL is the operational interface; curated views are the supported query surface.** The runtime and operators query the same database; no proprietary query layer. Storage tables are not a compatibility contract. *Reason:* operators write ad-hoc production queries without coordinating with the framework.
- **No reflection on the hot path.** Source generators emit all dispatch and persistence code; AOT-clean by construction. *Reason:* trim-friendly, no reflection cost per attempt.
- **Category: durable work ledger for .NET.** Never positioned as a workflow engine, message bus, or scheduler; "workflow" appears only in non-goals, comparisons, and search metadata. *Reason:* the positive category claim stays singular.

### Substrate

- **Source code is the source of truth.** Entity classes + XML docs are canonical; `data-model.md`, `code-families.md`, and each provider's `M001_init.sql` are emit-generated and CI drift-gated. *Reason:* one declaration site, no doc drift.
- **Hot-row mutation goes through semantic store methods.** State-mutating store methods are implemented once as shared `Relational{Feature}Store` in `Acta.Relational` over `IDbSession` + `ISqlDialect`; providers own the executable SQL and dialect binds, not store classes. No production generic `InsertAsync`/`UpdateAsync`/`DeleteAsync`. *Reason:* SQL enforces atomicity while one shared C# implementation holds provider-independent policy.
- **`JobEvent` is the execution ledger AND the audit timeline.** Paired `started`/`finished` events on `(JobId, ExecutionNumber)` carry per-attempt history; no per-attempt table. *Reason:* one append-only substrate, one retention sweep.
- **`JobEvent` carries an optional `detail`/`detail_format_id` payload pair**, paired by `ck_events_detail_pair`; format `0` means no detail. Richer event-specific data still goes to OTel spans and logs; durable state lives on the entity row. *Reason:* keeps `JobEvent` frugal while still giving free-form (text) or structured (json) context beyond `ReasonCode`/`ReasonMessage` a home.
- **One merged `checkpoints` table; `steps` stays separate.** Variables, signals, timers, progress, and child latches share one `(job_id, kind_code, name)` table; steps keep their own (attempt counters, retry budget, stored results). *Reason:* one physical shape for every simple slot; only steps carry a genuinely different shape.
- **Single-tier retention on `JobEvent`.** One knob (`JobEventsRetentionDays`, default 365) sweeps every row. *Reason:* the two-tier variant added a knob nobody tuned.
- **`JobResult` is keyed by `(JobId, ExecutionNumber)`** with CASCADE FK. *Reason:* the natural identity callers already address by.
- **Timer checkpoints are arm/consume only.** `Pending → Consumed`; no `Cancelled` state; `ResetJobState` deletes rows outright. *Reason:* a state the runtime cannot reach is schema debt.
- **`Lease` carries no acquisition timestamp.** Lifecycle is `expires_at_utc` + `version` alone. *Reason:* no code path ever consulted the acquire instant.
- **`jobs` + `runtimes` split: immutable identity vs mutable state.** `jobs` is never UPDATEd; the 1:1 `runtimes` row owns every mutable column with `runtimes.version` as the CAS token, and execution ownership lives on it, not in `leases` (a lease-table variant cost ~30-40% drain throughput and was reverted). *Reason:* the hot path rewrites a narrow row, and identity is immutable by construction.
- **Reasons are events, not row state.** Why a job failed/paused lives on `JobEvent`, never on a `runtimes` reason column; snapshots and outcomes expose state only. *Reason:* a denormalized current-reason goes stale on re-arm; an append-only event cannot.

### Boundaries

- **Three-key job identity.** `JobId` internal (bigint), `JobRef` public (`job_` + Crockford Base32 over a C#-allocated UUIDv7), `DeduplicationKey` caller-defined; `events`/`alerts` denormalize `job_ref` at INSERT so it resolves past purge. *Reason:* numeric ids leak ordering/volume and exceed JavaScript safe integers.
- **`JobNamespace` is the service-owned execution boundary.** One deployable service owns one namespace; replicas share it. *Reason:* namespace identity = deployment unit, not tenant marker.
- **Workers only claim within their own namespace.** *Reason:* blast-radius firewall, a bug cannot escape its namespace; cross-namespace enqueue allowed, cross-namespace claim impossible by index seek.
- **Tenant is validated identity, not an isolation primitive.** The catalog is global per store; enqueue validates and stamps it, children inherit it, and suspension is admission control only. No tenant-aware claim fairness, per-tenant limits, data security, or failure domain; that isolation = application enforcement + separate deployments. *Reason:* an audit/query scope is a cheap nullable column; fairness would widen every hot index.
- **System jobs are namespace-local.** `sys.alerts`/`sys.recovery`/`sys.retention` live in the target's own namespace. *Reason:* a global namespace would violate the firewall.

### Behavior

- **`MaxAttempts` is the unified cap** on jobs and steps; `Reschedule`/`Suspend`/`Pause` never consume the budget. *Reason:* one retry mental model across both tiers.
- **Strict priority ordering** in the claim path; no aging, no weighted fairness. *Reason:* predictable semantics; fairness = separate namespaces.
- **`ExclusiveKey` is execution-time mutual exclusion (size 1), not rate limiting or ordering.** Enforced by a lock taken after claim; losers bounce budget-neutrally. *Reason:* claim-time gating collapsed namespace claim throughput under a hot-key backlog (~20/s vs 500-2,500/s exec-time).
- **Delayed enqueue: relative delay is DB-clock; absolute is the only caller-instant path.** The two are mutually exclusive. *Reason:* an enqueue-only frontend must not silently depend on its own clock.
- **Recurring schedules are a single slot job per `(namespace, definition)`** carrying many `schedules` rows; due schedules coalesce into one execution; cursors computed in C#, applied in SQL. *Reason:* single-cursor claim scan, no per-firing row inflation, no Cronos in SQL.
- **Misfire is a two-strategy per-schedule choice.** `Skip` (default, forward-only) or `FireOnceCatchUp`. *Reason:* forward-only avoids startup catch-up bursts; catch-up is opt-in.
- **`job.audit_level_code` is a per-job snapshot** copied from the definition at enqueue; unset defaults to `Audit`. *Reason:* hot-path read with no join.
- **`AuditLevel=Off` records no transition reasons.** No `JobEvent` rows means no persisted reason; current state and `FailureCount` remain. *Reason:* leanness is the point of `Off`; no hot-row reason columns to paper over a disabled trail.
- **`Job.Status = Failed` is terminal.** Non-terminal failures stay `Ready` with `NextRunAtUtc` advanced. *Reason:* terminal is terminal.
- **`Dispatched` stays.** The two-step `Ready → Dispatched → Executing` claim flow is kept. *Reason:* per-job cost is WAL fsync, not the transition; `Dispatched` keeps "claimed but not running" observable.
- **`ExecutionProfile` is `{ Buffered, Direct, Bulk }`.** `Buffered` (default) is two-phase durable; `Direct` combines claim-execute with the same durability; `Bulk` group-commits completions and is the one relaxed-durability (at-least-once) rung. *Reason:* durable rungs never revert a terminal result after unclean shutdown; relaxation is opt-in and scoped to re-runnable work.
- **Pause is sticky.** No `PauseUntilUtc`, no auto-resume sweep. *Reason:* bounded pause is composable by the caller.
- **Backoff is one expression column** (`backoff`, e.g. `"1m..8h x2 ±10%"`, with override/effective pair). Supersedes the earlier four-typed-columns decision (2026-07-09): the attribute is authored as a single DSL string, so storing parsed knobs was lossy and unfaithful; the shared parser validates at compile time and at override write, and SQL never computes with it.
- **System jobs are alerted like any job.** `sys.recovery`/`sys.retention` failures project alerts at `Critical` (`AlertProfile = SysCritical`) to the implicit `default` log channel unless overridden. *Reason:* maintenance failures ride the same alert substrate they maintain.

### Storage and naming

- **The migration history freezes at 1.0.0, not before.** Until then the baseline stays re-cuttable: `schema reset` is available at any time, and an `Mnnn` landed to exercise an upgrade path can be folded back into a fresh baseline later. From 1.0.0 every schema change is an additive `Mnnn` migration (new columns/tables): no renames, drops, retypes, or renumbered code values. *Reason:* closing the public, physical, and numeric vocabulary together is the 1.0 promise, so a preview build re-cuts rather than accumulating migrations nobody will run.
- **Tag scope is exact attachment, setting scope is fallback configuration.** A tag scope identifies the exact target to which searchable metadata is attached. Tag scopes do not inherit, fall back, propagate, or participate in precedence resolution. A setting scope identifies where configuration applies and participates in definition-to-namespace-to-global fallback resolution. *Reason:* independent code families prevent searchable annotations from acquiring configuration precedence semantics.
- **Database schema is an install-time option per deployment**, default `acta`. *Reason:* co-deployment per DB without dynamic routing.
- **Identifier convention:** plural, prefix-free, underscore-free tables; `lower_snake_case` columns; `ix_`/`ux_`/`pk_`/`ck_`/`fk_` prefixes; every persisted name explicit on its attribute; no quoted identifiers; CLR entities keep domain names (`Job*`). *Reason:* unquoted names work on every target DB and survive convention renames.
- **Varchar sizes come from a fixed ladder:** 16, 32, 64, 128, 256, 512, 1024, max. *Reason:* SHA-256/512 hex fit natively; no bespoke widths.
- **All datetime columns are `datetime2(3)` UTC** with a `Utc` suffix. *Reason:* one precision, one zone, self-describing names.
- **Acta names are lowercase ASCII kebab/dotted-kebab, Acta keys are normalized lowercase ASCII, and external/display values are preserved.** Name casing is rejected rather than silently folded; key equality still normalizes at write/lookup seams. *Reason:* Acta-owned identifiers survive every collation, terminal, and URL without corrupting user business values.
- **Three text tiers via `DbKind`:** `AsciiString`, `UnicodeString` (cold/mid tables only), `BinaryPayload`; never `jsonb`. *Reason:* hot tables stay narrow; validation lives at the application boundary.
- **Optimistic-concurrency token is `int Version`**, manually incremented; no `rowversion`, no `xmin`. *Reason:* uniform across providers and operator-observable.
- **Server-side defaults via `DbColumn.Default`** for audit timestamps and version columns. *Reason:* the DB clock is authoritative for "when was this row written"; no NTP-drift hazard.
- **Audit tables (`JobEvent`, `JobAlert`) carry no enforced FKs** to retention-deletable rows; they denormalize identifying facts at INSERT. *Reason:* an FK to a purgeable row collapses the analytics window.

### Codes

- **Every ordinary persisted code family is a byte-backed BCL enum + source-generated `<Name>Extensions`.** SQL Server stores it as `tinyint`; PostgreSQL uses `smallint`; SQLite uses `INTEGER`; provider checks enforce the same logical unsigned-byte contract. *Reason:* one byte of logical identity is sufficient for all current closed catalogues, including events and reasons.
- **A numeric code is identified by `(code family, id)`.** IDs are stable only within their family; enum members carry programmatic meaning and textual codes carry operator-facing meaning. Numeric grouping is a readability convention and runtime code must use explicit member switches rather than ordering, division, or range predicates. *Reason:* compact persistence must not become an encoded semantic language.
- **Canonical lifecycle values are conventional, not behavioral:** success `100`, failure/exhaustion/dead `200`, cancellation `220`, interrupted/indeterminate `230`, retired/deprecated `240`. *Reason:* visual alignment aids SQL inspection while exhaustive member logic remains authoritative.
- **`code_kind` is declared explicitly via `[CodeKind("kebab-name")]`.** *Reason:* renaming a C# enum must not silently rename an operator-facing discriminator.
- **No `codes` table and no generated per-table views (removed 2026-07).** The full decode surface is `code-families.md`; SQL operators get curated `_view` surfaces with generated name-only `CASE` decodes for common workflow fields. *Reason:* one C# code truth without a sync table, while common day-2 SQL stays readable.
- **Closed catalogues reject unknown values and permanently reserve `255`.** `FromId`, textual conversion, JSON conversion, and database checks reject unassigned values; retired ids/text codes and permanent reservations are never reused. *Reason:* schema-as-API requires an enumerable, immutable value space.
- **`JobPayloadFormat` is the sole `255` exception.** `0` means no payload, `1..127` is framework-owned, and `128..255` is consumer-owned. *Reason:* payload formats are an extensible registry rather than a closed catalogue.
- **Byte-backed enums reserve `0` unless absence, none, or off is meaningful** and otherwise number sparsely. Capacity reports count active, deprecated, retired, and permanently reserved identities separately from held reserve. *Reason:* a zero default cannot masquerade as a real value and tombstones must remain visible in capacity planning.

### Packages

- **Package split by surface:** `Acta` (public API and SDK), `Acta.Runtime` (provider-independent runtime), `Acta.Relational` (shared relational mechanics), providers, `Acta.Redis`, and `Acta.Testing`. *Reason:* an assembly boundary is a stronger API-safety guarantee than `internal` alone.
- **Relational-provider baseline.** SQL Server, PostgreSQL, and SQLite ship side-by-side against one provider-neutral entity model. *Reason:* shipping distributed and embedded providers in lockstep prevents single-provider assumptions.
- **No in-memory provider.** *Reason:* it would pass tests the real providers fail.
- **Visibility default is `internal sealed`** outside the `Acta` SDK project. *Reason:* the API boundary is grep-checkable.

### Architecture vocabulary

Reserved terms for internal structure; a generated job descriptor set is a *manifest*, never a module.

- **Module:** a cohesive capability with an exposed API, hidden implementation, owned state and invariants, and acyclic dependencies on other modules' APIs only. Target modules: Execution (the durable-work kernel), Alerting, Outbox, Operations.
- **Subdomain:** a cohesive area inside a module with no independent API or state ownership. Catalog and Scheduling are Execution subdomains because completion advances schedule state atomically.
- **Use case:** one operation's vertical slice inside a module (enqueue, claim, complete).
- **Component:** a privileged runtime helper that is not a module because it needs cross-cutting knowledge (Maintenance/retention).
- **Adapter:** a project that connects external technology to declared ports: providers, `Acta.Redis`, `Acta.AspNetCore`, `Acta.Testing`.
- **Public facade:** the consumer surface (`IJobs` and friends), deliberately simpler than the internal structure; the public API never mirrors the module graph.
- **Allowed dependency graph:** each module exposes an `Api` sub-namespace (its declared contract) and hides everything else. The gated edges are exactly Alerting, Outbox, and Operations -> `Execution.Api` (`IJobSubmission`, `IAlertSink`, `IAlertRoutingCheck`, `IExecutionQueries`, the control contract); Execution depends on no other module - alert-routing validation runs through the Execution-owned `IAlertRoutingCheck` port Alerting implements, and the operator list reads (`ListJobsAsync`/`ListJobEventsAsync`) live on `IActaOperations`, composed by Operations from `IExecutionQueries` and its own events read model. Operations is subject to the same Api-only rule as every module; nothing is an unrestricted reader. The declared graph is acyclic and a gate proves it. Hosting/composition sees everything, and `WorkerRegistration` is a hosting type for exactly that reason. No module writes another module's tables outside the per-table process-manager declarations, and no module takes or service-locates another module's store ports. *Reason:* recording the target graph makes each extraction PR checkable against a decision instead of taste; the reference gate holds the graph to zero undeclared edges.
- **Tags follow the shared-substrate model:** each target's owner owns its tag rows by scope, the physical `tags` table is one partitioned substrate, and every write goes through the Tags capability's SQL under the ownership map. There is no Tags module and no tag-metadata API in front of it. *Reason:* tags span every target type by design; a nominal module whose table everyone joins would be boundary theatre.

### Provider contract

- **Provider conformance is one shared spec suite** all three relational providers gate on. *Reason:* one definition of "provider-compliant".
- **All hot-path writes are semantic store calls** whose provider SQL writes the `JobEvent` in the same transaction. *Reason:* one round-trip; audit and state are atomic.
- **Provider SQL mirrors the module architecture:** `Sql/{Module}/{Capability}/` (single-capability modules keep files at the module root; `Schema/` and `Services/` are shared infrastructure tiers). The ownership gate runs two tiers on that layout: the capability tier catches fine-grained cross-owner writes even inside one module, and the module tier holds cross-module writes to an explicit declared list. *Reason:* the SQL tree is public-facing in a product whose pitch is inspectable SQL; the tree should teach the architecture, and the finer capability granularity catches more than a module-only rule would.
- **Core behavior depends on feature store contracts implemented once in `Acta.Relational`.** The shared `Relational{Feature}Store` classes own command building and mapping; providers own only dialects, executable SQL, and bulk binds: no per-provider `*Store.cs`. Statically gated by `ProviderStoreBindingCheck`/`ArchitectureBoundary`. *Reason:* one implementation of provider-independent policy, with SQL and dialects the only provider-specific surface.
- **No production raw-session API crosses into feature behavior.** `Acta.Testing` owns the test query DSL and raw ADO helpers. *Reason:* ad-hoc product writes would bypass store-owned invariants.

### Handler contract

- **Two handler attributes and two invocation modes.** `[Job]` and `[JobSchedule]` (namespace declared at `Run(...)`; steps have no attribute); callers use `EnqueueAsync` (fire-and-forget durable) or `ExecuteAndWaitAsync` (durable enqueue + await). *Reason:* the declarative surface stays small.
- **The handler-contract specification lives in [`handler-contract.md`](../guide/handler-contract.md)**: shapes, placement, payload inference, exception semantics, diagnostics. *Reason:* one decision record per seam.

### Tooling

- **Two source-gen surfaces.** `Acta.Generators` (Roslyn, compile-time dispatch + persistence) and `tools/Acta.Emit` (CLI, committed artifacts), split by output kind. *Reason:* Roslyn can't emit committed files; the CLI can't emit C# the compilation depends on.
- **Generated artifacts are committed**, drift-gated by `Acta.Emit check` in CI. *Reason:* reviewers see the schema delta alongside the source change.
- **Satellite DI entry points are named for the type they extend**: `{Provider}ActaBuilderExtensions`, `ActaEndpointRouteBuilderExtensions`, `ActaServiceCollectionExtensions`. *Reason:* the filename names the extension surface, so a `ServiceCollectionExtensions` suffix on an `ActaBuilder` extension would mislead.

### AOT and SQL parameter metadata

- **The runtime requires no reflection** for discovery, invocation, entity metadata, or parameter metadata; emit tooling and tests may reflect. *Reason:* AOT/trim-clean by construction.
- **Parameters bind with explicit generated metadata** (`DbParameterSpec` from `ActaSchema` specs); `AddWithValue` and value-only parameter constructors are banned. *Reason:* driver type inference bloats the plan cache and breaks max-type round-trips.
- **SQL files carry command text only**; `{{schema}}` is the sole template substitution. *Reason:* parameter metadata belongs next to the value that supplies it.

### Behavior conventions

- **Duration policy has three representations.** ISO 8601 in attributes/config, `TimeSpan` in C#, `int` seconds in the DB. *Reason:* one canonical form per layer.
- **One heartbeat rhythm:** `HeartbeatInterval` 45s, `LeaseTtlSeconds` 180s; leases and worker liveness extend on one loop. *Reason:* one tuning constant, one mental model.
- **`TimeProvider`, not `DateTime.UtcNow`**, for anything driving scheduling, lease, or claim correctness; carve-outs are DB-rendered defaults, the `DeduplicationKey.Per*` convenience overloads, and the clock-free wakeup hint. *Reason:* testability and clock-source clarity.
- **Per-execution DI scope.** Scoped services receive fresh instances per attempt. *Reason:* the attempt is the unit of work.

### Invariants

1. The framework's identity does not change without an update to this ledger.
2. Every claim about Acta in any other doc is testable: a conformance test, a generated diagnostic, or a CI gate enforces it.
3. New tables and new methods carry a substrate-generality justification or are rejected.
4. Source code is the source of truth for everything mechanically derivable; this ledger captures only what isn't.
