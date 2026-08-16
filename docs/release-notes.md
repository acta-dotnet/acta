# Release notes

## 0.9.0-beta.1

The last breaking release: the next tag is 1.0.0-rc.1, and from 1.0 the .NET surface, HTTP surface,
schema, and persisted codes are additive-only. Everything here is here because it was
0.9.0-or-never. The headline is the outbox operator path — `Quarantined` was shipped vocabulary
with no exit, and now it has one — surrounded by the rename settlement three naming audits of the
frozen surfaces produced.

> **Schema note:** the `M001` baseline was re-cut (stamp `init-entity-refs-v1`). Preview
> policy applies: drop and reprovision the Acta database; the bootstrap refuses any history that
> does not carry this build's stamp row. The external-outbox staging table changed too
> (`input_data` → `input`, `job_namespace` widened to 128): producers re-run
> `{Provider}OutboxDdl.CreateScript()` against their own database.

### Upgrade actions, in order

- **Reprovision.** The baseline stamp changed; an existing Acta database will not boot this build.
- **Recheck every `INamespaces.ListAsync` call site.** It now returns
  `PagedResult<NamespaceListItem>` (the row, with status and version); the old names-only read is
  `ListNamesAsync`, same query type. Most call sites break loudly; `var` plus members both shapes
  share compiles silently and starts reading rows where it read names.
- **Walk the rename table below** for the .NET, HTTP, SQL, and persisted-code spellings.
- The naming rules these renames settled into are written down in
  [naming conventions](./internals/naming-conventions.md); post-1.0 names are measured against
  that page rather than re-litigated.

### The outbox gets its operator path

`OutboxStatusCode.Quarantined` finally has an exit, designed around one fact: the staging table
lives in the producer's database, which most operator hosts cannot reach.

- **`IActaOperations.Outbox`**: `ListSourcesAsync` (one line per relay source, composed cross-peer
  from the `sys.outbox` slot's persisted tick summary — any host with ledger access can answer, and
  `IsLocal` says where the deeper reads work), `ListQuarantinedAsync` (paged, identity and failure
  evidence only, host-local by design with a clear error elsewhere), `RequeueAsync`, and
  `DiscardAsync`.
- **Requeue and discard are accepted-then-applied.** The verb parks a durable command on the slot
  job's bounded signal inbox (two fixed names, so at most two command rows per source ever exist);
  the next relay pass applies it, writes an audit event carrying the operator identity, the
  justification, and the applied row ids, and consumes the command in the same pass. Works from any
  peer, survives owner downtime. A second command while one is pending is rejected with the pending
  command's park instant; a pending command older than the worker-dead window is superseded, with
  version-CAS keeping the overwrite race safe. `ControlAction` gains `Accepted` for exactly this
  shape. Requeue resets the failure budget and keeps `last_error` as evidence; discard deletes the
  rows with the ids preserved in the event, so proof outlives the payload.
- **HTTP**: `GET /v1/outbox/sources`, `GET /v1/outbox/{jobNamespace}/quarantined`, and
  `POST /v1/outbox/{jobNamespace}/{requeue,discard}` (202 accepted / 409 pending / 404 no slot).
  They subsume `GET /overview/outbox`, which is removed — it was also the API's only unpaged
  collection.
- **Dashboard**: the Overview gains an outbox relays panel — backlog, quarantine, last tick per
  source — with requeue-all/discard-all behind the confirmation dialog (discard types the
  namespace). The health verdict now flags standing quarantined rows beside backlog lag.
- New always-emitted events `outbox.requeued` (180) and `outbox.discarded` (181); the tick summary
  string gains `quarantine=` (current total) and, when an operator command applied, `requeued=` /
  `discarded=` tokens.
- **Certification learned the seam**: Anvil now stages producer rows throughout the chaos window
  and checks *drained* (staging empty after quiesce) and *delivered* (every committed producer
  operation's job exists in the ledger) from the only place that can see both databases, while
  `certify.sql` carries the ledger-reachable half (every relayed job Succeeded, plus the relayed
  count as evidence).

### Breaking: one M001 re-cut

Four audit findings were unfixable after 1.0 and bundled into a single reprovision:

- **`leases` → `locks`.** The table only ever stored named locks; the real execution lease lives on
  `runtimes`. The one-member `kind_code` column retires with its persisted-code family, and the
  recycling `version int` becomes a C#-minted `hold_token uuid` — the int had an ABA window where a
  zombie holder surviving a steal → release → reacquire cycle could delete its successor's hold.
- **`alerts.dedupe_key` stays** — reversed during execution by the column-width parity gate (one
  name, two widths is the drift the gate exists to block); the operator-facing spelling comes from
  `AlertsView` aliasing it to `deduplication_key`.
- Check-constraint names lose their doubled `_code` segments (emitter fix), and the two worker
  index names spell `last_seen`.
- Staging table: `input_data` → `input` (parity with `jobs.input`), `job_namespace` widened to the
  model's 128. `outbox_id` and `next_attempt_at_utc` keep their names — the audit's renames were
  reversed on inspection (`_ref` is reserved for rendered public handles, and the column covers the
  *first* delivery attempt, not a retry).

### Breaking: persisted codes (ids unchanged, strings move)

Same destructive-class precedent as 0.7.0:

- Slugs: `priority` → `job-priority` (symmetric with `job-status`), and the deadline-behavior slug
  drops its `job-` prefix. Event strings: `job.note` → `job.note-recorded`, and the dead-worker
  event becomes `worker.died` — the two of 34 event strings that broke `noun.past-participle`.
- CLR names (contract-free, the snapshot keys by kind): `JobEventCode` → `EventCode`,
  `JobActorCode` → `ActorCode` — both families are ledger-wide, so their short slugs were right all
  along.
- Every retired string joined the canonical-vocabulary gate so it cannot come back meaning
  something else.

### Breaking: the .NET surface settlement

- **The `Job` prefix comes off every type that is not literally a job**: `WorkerListItem`,
  `WorkerDetail`, `AlertListItem`, `ScheduleListItem`, `ScheduleLookup`, `NamespaceStatusCode`, the
  `List*Query` types, and peers. Tenant, already clean, was the template.
- **`note` → `reasonMessage` on every operator parameter** (`note` is now reserved for the
  application-authored `ctx.NoteAsync`); `JobSnapshot` → `JobDetail`, `SettingSnapshot` →
  `SettingDetail` (`Detail` = single-entity read, `Snapshot` = point-in-time aggregate;
  `OverviewSnapshot` stays); `JobControlAction` → `ControlAction`; `DefinitionOverrideResult` →
  `DefinitionControlResult`; `ExecuteAndWaitAsync` → `RunAndWaitAsync`; `Search` → `NameContains`;
  worker timestamps unified on `LastHeartbeatAtUtc`/`StartedAtUtc`; the five duplicate
  `JobLineage*` types collapse into their `JobExplain*` twins.
- **Gap closures that had to precede the freeze**: `ITags` mutations gain `actorKey` and
  `reasonMessage`; `ISettings.SetAsync` gains `expectedVersion` (its `VersionConflict` member could
  never previously occur); `IAlerts.GetAsync` exists; `JobDefinitionListItem` carries `Version`.
- `Acta.Testing` joins the public-API gate, its snapshot records become init-only property records
  so 1.x additions are additive, and `ScenarioSession<TInput>` is sealed.

### Breaking: the HTTP surface

- Namespace identity unified on `jobNamespace` (routes, fields); every other route noun names what it
  addresses (`alertRef`, `workerRef`, `{jobNamespace}/{jobName}` for a definition, `{signalName}`).
  No entity's DB integer is a wire identity any more: an alert, worker, or job is addressed by its
  public ref, and a definition, namespace, or tenant by its natural key.
- **Every control verb now names its target in the route.** The schedule verbs move from
  body-addressed `POST /schedules/{action}` to
  `POST /schedules/{jobNamespace}/{jobName}/{scheduleName}/{action}` (and the preview follows:
  `GET .../preview?limit=`) — the same natural-key triple the schedule tag routes already used, so
  a reader of the contract sees what an operation targets instead of an opaque verb. Resume,
  trigger, and the outbox verbs now take an optional body (a bare POST acts with defaults).
- The `/jobs/{jobRef}/detail` aggregate's job row rides as `job` (was `snapshot` — `Snapshot` is
  reserved for point-in-time aggregates, and `OverviewSnapshot` is the only one).
- `note` → `reasonMessage` on the six control request types; `version` → `expectedVersion` on the
  two override requests; `lastSeenAtUtc` → `lastHeartbeatAtUtc`; `search` → `nameContains`;
  `parentRef` → `parentJobRef`; preview `count` → `limit`; `format` → `formatName` where the value
  is a name; `/events` gains `includeTotal`.
- **The ~45 query parameters that were absent from `openapi.json` are now documented and
  drift-checked**, so the frozen contract actually protects the filter surface.

### Breaking: entity refs replace integer identities

`job_` had a public ref since 0.8.0; alerts and workers did not, so half the surface still addressed
rows by a dense integer that a restored backup can recycle. 0.9.0 finishes the job — the rule is now
*a database integer is never a wire identity*, written down in
[naming conventions](./internals/naming-conventions.md) and enforced by three gates rather than by
review.

- **`alr_` and `wrk_` join `job_`.** Both are UUIDv7 values minted in C# and passed into the write, so
  the database never defaults one; both get a unique index. `alerts.alert_ref` is applied on the
  INSERT arm of the dedupe upsert only, so **an alert that re-fires inside its window keeps the ref
  its first firing minted** — a delivered notification's link never goes stale.
- **Routes re-keyed.** `GET /alerts/{alertRef}` (new) plus the acknowledge / resolve verbs and the
  alert tag routes; `GET /workers/{workerRef}` and the worker tag routes;
  `GET|PATCH /definitions/{jobNamespace}/{jobName}` plus its `/events` and tag routes. `/events`
  filters are now `jobRef`, `lineageRootJobRef`, `workerRef` and `tenantKey`; a malformed ref answers
  400 and a well-formed one that names no row answers 404, matching the sibling endpoints.
- **Every remaining integer identity left the payloads.** `jobId`, `parentJobId`, `lineageRootId`,
  `definitionId`, `tenantId`, `namespaceId`, `workerId`, `leasedByWorkerId`, `alertId` and
  `jobScheduleId` are no longer serialized; the .NET records keep them for in-process callers behind
  `[JsonIgnore]`. Values stay numeric and are unaffected: `jobEventId`, `executionNumber`,
  `occurrenceCount`, `failureCount`, `version`, page sizes and counts.
- **Events reads gained `tenantKey` and `workerRef`**, and worker-actor rows changed what `actor_key`
  holds: it now stores the acting worker's ref as canonical lowercase uuid text (previously the worker
  id), and read projections render it as `wrk_...`. The denormalized copy is what makes the timeline
  outlive its worker — after retention reaps the row the joined `workerRef` reads null while
  `actorKey` still names the worker an operator saw.
- **CLI output is ref-based**, plain and `--json` alike: control, status, events, snapshot, explain and
  debug all print `job_...` / `wrk_...` and no internal id, and the events continuation hint hands back
  `jobs events <job_...> --after <cursor>`. Numeric-id *input* is still accepted (`jobs info 42`) as the
  documented advanced path. One behavior change: a control verb whose target no longer exists now
  prints a uniform `job not found` on stderr instead of a control block reading `action: NotFound` —
  same exit code 2, one message across all five not-found paths.
- **Reprovisioning in place leaves stale PostgreSQL overloads.** `start_worker`, `raise_job_alert`,
  `acknowledge_job_alert` and `resolve_job_alert_manual` all changed arity or parameter types, and the
  installer issues `CREATE OR REPLACE FUNCTION` without a `DROP`. The old overload therefore lingers
  beside the new one on a database provisioned by an earlier build. It is harmless — every call is
  positional and the arity plus parameter types disambiguate — but a fresh provision (the documented
  0.9.0 path) has exactly one of each, and reprovisioning is what the upgrade actions above ask for
  anyway.

### Execution correctness: a lost step CAS no longer terminalizes a healthy job

A `complete_step` version CAS that matches no row usually means another execution re-claimed the
job, but the signal can be spurious — a transient-retry re-run of a batch whose first attempt
committed reads zero changes and looks identical. The executor previously answered with a terminal
Failed carrying no reason; a job the worker in fact still owned died silently. It now submits a
retryable `job.attempt-aborted` failure instead: a row another execution owns no-ops at the
completion CAS exactly as before, while a row still owned re-arms under the failure budget and
retries, replaying its recorded steps. Found by this release's SQLite certification gate — one
job in ten thousand under kill-every-5s chaos.

### Certification: four gates, four seals

All four ran on the release commit on 2026-08-16 and file under `docs/certification/`:

- **`seal-20260816T081858Z.md` — PostgreSQL standard** (10,000 jobs, 64 slots, 7-minute kill
  window): PASS, with the outbox handoff certified across its ownership seam — 5,000 producer rows
  staged during the chaos, every one drained and delivered.
- **`seal-20260816T083231Z.md` — SQL Server standard**: PASS at the same shape, 574 orphaned
  attempts against PostgreSQL's 579 and identical job, step, and receipt totals to the row.
- **`seal-20260816T085003Z.md` — SQLite**: PASS at 48 slots on one WAL file, reduced scope stated
  on its face (single machine is the engine's deployment model; multi-machine claims are
  permanently out of scope there). This ends the standing contradiction with releasing.md's "one
  run per provider".
- **`seal-20260816T090216Z.md` — the 3-participant / 2-namespace ensemble** both 0.8.0 ensemble
  seals named as "one flag away and unrun": PASS. `namespace-isolation` was falsifiable for the
  first time (a second namespace live and being killed beside the first) and held with zero rows,
  and the run carries the release's non-vacuous `AtMostOnce` evidence: 44 of 400 charges
  interrupted mid-body, none ever run twice.

The SQLite gate's first run of this round is why the round exists: it failed `expected-outcome` on
one row out of fifteen thousand, and that row was the lost step CAS described above — so all four
seals are the re-run on the commit that fixed it.

## 0.8.0-beta.1

Hardening. An operator can see saturation before it becomes an incident, a pause survives a fire that
was already planned, and the settings surface loses everything nobody could set correctly.

> **Schema note:** no schema change. The `M001` baseline is unchanged.

### Configuration is smaller on purpose

- **The coordination triple is one setting.** `HeartbeatInterval` is settable; `LeaseTtlSeconds`
  (x4) and `WorkerDeadAfter` (x7) now derive from it and are read-only. All three must agree across
  every worker or the reclaim math desyncs, and nothing can verify that agreement at runtime, so the
  ratio is held by construction instead of checked afterwards. A shorter beat shortens the whole
  triple in proportion, which is what makes a crash demo watchable in seconds.
- `WorkerDeadAfter`'s default moves from 300s to 315s. Both are round numbers satisfying "comfortably
  past the lease"; nothing was encoded in the old one.
- **Eight settings are gone**: `MinPollFloor`, `ClaimIdleJitterMax`, `ExclusiveKeyBounceDelaySeconds`,
  `AlertDedupeWindow`, `AlertDeliveryMaxRetries`, and the three `BatchCompletion*` thresholds. They
  were engine tuning with no operator-legible meaning, so they were ways to break a deployment rather
  than ways to shape one. Their values are unchanged; they are simply no longer yours to set.
- Setting any of the removed values is a compile error, not a silent change: Acta binds `JobsOptions`
  from code, never from a configuration section.

### Operator-visible pressure

- **Executor saturation.** The overview's Executing card now carries its denominator, read from
  `workers.max_concurrency` across live workers: `16 / 64` instead of `16`. Without it a full
  executor pool and a broken claim loop looked identical, and they call for opposite actions.
- **Schedule lag.** How far past due the most overdue live schedule is. A timetable that stops firing
  moves no other number on the overview (nothing is enqueued, so ready and failed stay flat and the
  verdict reads clean), which made it the one failure with no signal at all.
- Both are bounded-cost reads with no schema change, and worker liveness now keys on the lease window
  rather than the dead-worker window everywhere: past the lease a worker's jobs are already
  reclaimable, so its slots are not capacity.

### Chaos and the lab

- **An operator pause survives a fire that was already planned.** A slot plans its advances from a
  snapshot taken at claim and applies them at completion; the advance write had no status guard, so a
  pause landing inside that window was cleared and the timeline recorded `schedule.pause-expired`
  when nothing had expired. Guarded on all three providers, with the audit insert sharing the
  predicate.
- Anvil's throughput readout is measured over a trailing ten seconds rather than averaged across the
  whole two-minute sample window, where a burst was smeared flat and a change took two minutes to
  appear.

## 0.7.0-beta.1

Every breaking change in this line is here and only here: the .NET API settlement and the HTTP
surface freeze. It also carries both million-job certification seals.

> **Schema note:** no schema change. The `M001` baseline is unchanged from 0.5.0.

### Breaking: the HTTP API is versioned and frozen

- **Every operator route moves under `/v1`.** The segment is Acta's, not the caller's, so a host
  mounted at `/internal/acta` serves `/internal/acta/api/v1/jobs`. The unversioned path now 404s, and
  a test pins that.
- `docs/reference/openapi.json` is generated from the real endpoint graph and drift-checked in CI,
  the same shape as the persisted-code and schema gates. The OpenAPI package is test-only, so
  `Acta.AspNetCore` carries no new dependency.
- **`POST /jobs` answers 201 for an insert and 200 for a deduplicated match**, and carries `Location`
  either way, built from the request's own path. It previously answered 201 with no `Location`, even
  when the enqueue created nothing.
- **`/namespaces/admin` is removed.** `/namespaces` returns the row and takes the status filter;
  `INamespaces.ListAsync` still serves callers that want only names.
- Tenant routes said `{key}` in controls and `{tenantKey}` in tags. One name now.
- `JobEnqueueAction` serialized PascalCase while its two siblings were camelCase.

### Breaking: the .NET surface

- **`Done` becomes `Succeeded` everywhere.** `JobStatusCode` never had a `Done` member, yet 79 places
  said it, including `jobs explain`, which printed "Done." for a succeeded job while printing
  "Failed." and "Cancelled." for the others.
- **The public `StepOptions` constructor is removed.** It could build exactly the `AtMostOnce`
  plus retry-override combination `StepOptionsBuilder.Build()` rejects, so the invariant had a public
  bypass. The record keeps its `init` properties, so `Inherit with { ... }` still works.
- **`JobContext` gains `ExecutionNumber` and `WorkerId`.** A handler could not name its own attempt or
  the worker running it, which is what a note, a log correlation, and an external idempotency key all
  want.

### Certification and dependencies

- **1,000,000 jobs, PASS on PostgreSQL and on SQL Server**, both with real kills and non-zero
  reclaims.
- Off the preview SQLite pin: SQLitePCLRaw 2.1.12 carries the fix for GHSA-2m69-gcr7-jv3q, so stable
  `Microsoft.Data.Sqlite` resolves clean.
- The dashboard's scope selector moved onto the shared `Dropdown`, gaining typeahead and
  close-on-focusout; the homepage reordered around getting started; an external audit's eight
  incorrect claims corrected.

## 0.6.0-beta.1

The accurate public face, plus the evidence behind it. A consumer on 0.5.0 upgrades by fixing one
renamed setting.

> **Schema note:** no schema change. The `M001` baseline is unchanged from 0.5.0. Event code 90
> (`job.note`) is additive and needs no migration: routines are idempotent objects reinstalled by
> `SqlObjectInstaller`, and the DBA-runnable `docs/reference/schema-*.sql` carries the new routine.

### Breaking and behavioural

- **`JobsOptions.RegisterFrameworkJobs` becomes `RegisterSystemJobs`.** All three jobs it governs are
  `sys.` jobs, and the old name said nothing about the fact that turning it off silently disables
  crash recovery. The runtime now warns at startup when it is off.
- **The retry default's cap stretches from eight hours to one day** (`1m..1d x2 ~10%`, about 4.4 days
  over 15 attempts). The old ~48.5-hour horizon meant a dependency that broke Friday evening was
  terminally `Failed` before anyone read the alert on Monday. The outbox keeps its own `1m..8h`: a
  stuck outbox row is undelivered product, sized for the sink rather than for a human.

### Handlers can write to the ledger

- **`ctx.NoteAsync` puts an application-authored line on the job's own timeline**, event code 90. It
  is the only event an application can author and one the runtime never emits, so every other event
  stays provably system-written. No new table and no new store: the line lands in `acta.events`
  beside the transitions it explains, visible in the dashboard and reaped by the same retention. It
  is annotation, not logging, and the inline payload ceiling applies.

### Cold start and accuracy

- **`README.md` and the quickstart now open with `dotnet add package`** and a twenty-line
  `Program.cs` in the reader's own app; `git clone` is demoted to "explore the 88 concepts".
- The site's stale 256 KB payload cap becomes the real 1 MiB, benchmark links point at the newest
  committed baseline, and three orphaned docs are linked from the index.
- New pages: `/from-hangfire`, `/from-quartz`, `/from-tickerq`, a Temporal comparison, and
  `docs/support.md` with the support matrix and a patch policy a solo maintainer can keep.

### Gates

- **`PublicApiContractTests` pins the whole public surface to an approved baseline file**, mirroring
  the persisted-code gate, so an API change has to move a visible diff.
- Every shipped library sets `IsAotCompatible` for `net10.0`; the solution builds with zero trim
  warnings.
- CI now builds `demos/` and runs the Playwright dashboard smoke.

### Anvil becomes the certification point

- `certify.sql` holds assert-zero property checks over `acta.events` and the job rows: execution-event
  ownership, attempt pairing, namespace isolation, no stranded work, expected outcome per shape,
  terminal integrity, step replay, and an inverted check that the chaos was real, because a run with
  no reclaims proves nothing. Being SQL, they double as the evidence a skeptic re-runs.
- A certification run locks the cockpit and says which phase it is in, so one click cannot seed
  100,000 jobs into a run about to be sealed.

## 0.5.0-beta.1 (preview)

The first beta: the data model held, and the operator surface catches up. Find anything with the
dashboard's quick-search palette; understand exactly what happened with per-execution history on
job detail.

> **Schema note:** no schema change. The `M001` baseline is unchanged from 0.4.0
> (`init-extensible-status-v1`), the server is untouched, and upgrading from a 0.4.0 preview needs
> no reprovisioning - which is what earns this release the beta label. Preview policy otherwise
> still applies until 1.0: a future release may still re-cut the baseline.

### Quick search

- `Ctrl`/`Cmd+K` (or `/`) opens a palette anywhere in the dashboard. Paste a `job_` ref or `id:123`
  to jump to the job; `corr:<key>` lists every job carrying that correlation key; `key:<dedup>`
  resolves a deduplication key in the scoped namespace; a `name:value` token (or `tag:<name>` for
  bare tags) fans out to all six tag-bearing lists.
- Typing a fragment searches definitions, namespaces, tenants, and pages; `ns:<name>` (or picking a
  namespace hit) switches the working scope in place, with the scope shown as a removable chip.
- The empty palette teaches its own grammar with clickable prefix chips and lists recent
  selections; a correlation query offers its `jobs_view` SQL via Copy SQL.

### Executions

- Job detail gains an Executions tab: one row per handler invocation, derived from the event
  ledger with the outcome, start time, true retry gap (previous finish to next start), duration,
  worker, and failure reason; repeated reasons dim.
- The header counts against the runtime's claim counter and states gaps explicitly: audit level or
  event retention are only blamed once the full history is loaded.
- Rows drill into the timeline pre-filtered to that attempt; the tab and a specific execution
  deep-link via the URL hash (`?tab=executions&execution=N`), auto-loading older history when the
  target sits past the first page.

### Dashboard polish

- Tables share one skin: hairline row separators replace zebra, uppercase micro-headers, one
  placeholder glyph, state rails on failed/paused rows, and mobile card layouts on four more pages.
- A `System` theme choice follows the OS light/dark preference and is the default for fresh
  installs; existing stored choices are untouched.
- Overview verdict reasons link to their filtered views; job refs in lists carry a hover-revealed
  copy button; the wordmark links home preserving scope; the background grid and glow no longer
  read as artifacts on sparse pages.

### Site and docs

- useacta.net: the quickstart moved above the fold, the hero cards deep-link to their own anchors,
  and the dashboard capture is refreshed from a live run.
- README: one quickstart, deduplicated preamble, NuGet and website badges, the dashboard capture,
  and the Node.js 20+ note for the Anvil dashboard step.

## 0.4.0 (preview)

The data model is finished and verified: one status vocabulary, one payload ceiling, scoped durable
settings, DBA-runnable provisioning scripts, and a tenant-aware dashboard.

> **Schema note:** the `M001` baseline was re-cut in this release (extensible code catalogs,
> schedules cleanup, the status vocabulary below; baseline stamp `init-extensible-status-v1`),
> and migration history bookkeeping changed: `migrations` records each migration under its plain
> name (`1 = init`) with the baseline stamp in a dedicated version-0 row. Preview compatibility
> policy applies, including for databases built from an earlier 0.4.0 preview (their history
> lacks the version-0 row): drop and reprovision the Acta database; the bootstrap refuses any
> history that does not carry this build's stamp row.

### One vocabulary

- Terminal success is `succeeded` everywhere (`done` is gone); in-flight execution is `executing`;
  substrate lifecycle columns are `status_code` like every other entity; event names are uniformly
  two-segment kebab.
- The "metadata" notion is retired: you update the entity (`UpdateAsync`, `tenant.updated`,
  `namespace.updated`), and `updated` is the one modification verb.

### One payload ceiling

- `MaxInlinePayloadBytes` (1 MiB) is the single knob for ledger payloads and HTTP request bodies,
  with `PayloadTooLargeException` as the single error. An oversized handler result no longer
  persists silently: the job succeeds, the body is dropped, `job.result-oversized` records why, and
  a typed wait throws instead of returning a default result.

### Durable settings

- `IActaOperations.Settings`: get/set named settings at global, namespace, or definition scope,
  with `setting.updated` evidence events. New setting names in newer Acta versions cost no
  migration.

### DBA-runnable provisioning

- `docs/reference/schema-{pg,mssql,sqlite}.sql`: generated, drift-checked, execution-proven
  scripts carrying the full schema, views, and routines - for deployments where the application
  principal is never allowed DDL.
- Install and upgrade are the same file: every statement is individually guarded, and `BEGIN`/`END`
  banners mark where each migration starts and ends, with section banners for the views and
  routines that are always rewritten.
- The bootstrap rejects an applied migration whose recorded name differs from the shipped one, so
  a migration amended after a database applied it fails loudly instead of being silently skipped.

### Dashboard

- Job rows show and link the tenant key; jobs and events filter by tenant key; the tenant page
  links into both pre-filtered.

### Compatibility

- Open code catalogs (`event`, `job-event-reason`, `alert-kind`): an older build renders codes from
  a newer build as `unspecified` instead of refusing to start.
- Persisted event code renames (destructive-class, final before 1.0): the `*.metadata-changed`
  names became `tenant.updated` / `namespace.updated`; ids unchanged.

## 0.3.0-alpha.1 (preview)

The runtime is reorganized into explicit modules, the release pipeline hardens, and the Bulk
execution profile gets correctness fixes for aborted attempts and batched completions.

> **Schema note:** the `M001` baseline was re-cut in this release (the completion batch TVP is now
> keyed by request ordinal and its job column is named `job_id`). Preview compatibility policy
> applies: drop and reprovision the Acta database; the bootstrap refuses a mismatched baseline.

### Modular architecture

- One flat `Acta` namespace for the whole SDK; module boundaries (Jobs, Execution, Ledger, ...)
  are formalized behind `IActaOperations`, with dependency-graph and SQL-ownership gates in CI.
- Provider SQL trees mirror the module layout; relational store registrations are shared across
  providers; concrete schema migrators are internal.

### Execution correctness

- An attempt aborted mid-flight (lease renewal at risk, or a held handler lock lost) now retries
  under the failure budget with the new `job.attempt-aborted` reason instead of landing terminal
  Failed while the row was still recoverable.
- SQL Server batched completions accept two attempts of the same job in one batch (correlation is
  by request ordinal) and bind failure reason codes correctly, so a terminal failure no longer
  fails the whole flush batch.
- Bulk records the `acta.executions` metric at durable finalization, matching Direct/Buffered
  semantics, and a swallowed fallback completion result is now logged.

### Security and release hardening

- The dashboard/API HTTP ingress perimeter is closed by default; unknown API faults return 500 and
  only the documented transient family returns 503.
- Workflow actions are pinned to commit SHAs with automated pin updates; packages publish to
  nuget.org via Trusted Publishing on release tags; the package-consumer smoke covers every
  shippable package.

### Operator polish

- Job and tenant panels surface the retry budget; the scope selector uses a themed popover;
  automatic retention is lineage-safe and purge sections are uniformly bounded.

## 0.2.0 (preview)

Multi-tenancy lands as a first-class part of the ledger, the external outbox gains ledger-native
observability, and the dashboard grows its operator depth surface.

### Multi-tenancy

- Registration is insert-or-return-existing: `ITenants.RegisterAsync(tenantKey, ...)` is idempotent
  and returns the existing tenant on a repeat call; lifecycle changes go through Suspend/Resume only.
- Tenant reads: `ITenants.GetAsync(tenantKey)` and `GET /tenants/{key}`; job snapshots carry
  `TenantKey`, and handlers see the executing job's tenant via `JobContext.TenantKey`.
- Suspension is admission control with the commit boundary as the guarantee: new work for a
  suspended tenant is rejected at enqueue, while children inherited inside an already-admitted
  lineage still land. Suspension does not stop or cancel work already in the ledger.
- Definitions can require or forbid a tenant: `[Job(TenantRequirement = ...)]` (Optional, Required,
  Forbidden) is enforced at the enqueue boundary on every provider, and `Required` combined with a
  schedule is a startup error.
- Cross-tenant child enqueues need an explicit `OverrideParentTenant` opt-in; new enqueue rejection
  reasons name the tenant failures (required, forbidden, parent mismatch).
- `DeduplicationKey.ForTenant(tenantKey, businessKey)` builds tenant-relative keys.
- The curated `acta.jobs_view` resolves `tenant_key` beside the raw `tenant_id`, so tenant-scoped
  SQL reads no longer need a join.
- The tenant catalog is global per store and a tenant is a routing and reporting dimension, not a
  security or isolation boundary; see [concepts](./guide/concepts.md).

### Outbox observability

- `sys.outbox` records each successful tick's accounting as its job result
  (`claimed=.. relayed=.. dedup=.. quarantined=0 backlog=..`), retained newest-only on the
  recurring slot, so the dashboard job detail shows the last tick at a glance.
- The overview health verdict reports a lagging source ("outbox lagging N rows") once the backlog
  exceeds what one relay tick can move. Everything is read from the ledger; the dashboard never
  opens producer databases.

### Operator depth

- The dashboard reads and amends payloads, enqueues and clones jobs from input templates, drives
  schedule controls, filters the event ledger, and copies any view as SQL; control endpoints sit
  behind an authorization seam and an explicit confirmation header.

### Behavior fixes

- Recurring jobs never terminalize on consecutive failures; the slot keeps rescheduling and the
  failures alert.
- Recurring slots are claimed at their definition's priority.
- Cancellation-shaped provider exceptions surface as `OperationCanceledException`.
- Wait-timeout overshoot and sub-second backoff collapse are fixed; retiring a definition cancels
  its parked jobs; worker catalog and jobs options validate together at startup.

## 0.1.x (early preview)

First public preview of Acta: the SQL-native durable work ledger for .NET.

- Durable jobs: fire-and-forget, delayed, and recurring under one model.
- Durable execution: named run-once steps, `AtMostOnce()` step policy, checkpoint slots, durable
  sleeps, signals, child jobs with lineage, exclusive keys.
- Failure and recovery: worker leases with heartbeats, leaderless reclaim, Explain, restart with
  original input, failure alerts.
- Visibility: SQL-visible state with curated operator views, an append-only event ledger, the
  embedded dashboard and JSON API, the embedded CLI including `jobs debug`.
- Providers: PostgreSQL, SQL Server, SQLite with one operational model; source-generated dispatch;
  NativeAOT support; deterministic test host.
- Atomic enqueue with business data: transactional `IJobs` enqueue overloads that join a caller-owned
  `DbTransaction` (same database), and provider-package outbox staging (`AddToActaOutboxAsync` on the
  caller's own transaction) plus an Acta-owned `sys.outbox` relay for a different database. Neither is a
  universal exactly-once guarantee.
  See [transactional enqueue and the external outbox](./guide/transactional-enqueue-and-outbox.md).

APIs, schema, and behavior may change without deprecation during the preview. Known gaps are
tracked in [known limitations](./technical/known-limitations.md).
