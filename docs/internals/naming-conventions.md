# Naming conventions

The naming rules the 0.9.0 surface froze under. Both pre-freeze audits (the .NET surface audit and
the schema audit) found drift that a stated rule would have prevented, so the rules are stated here
once and every later name is measured against them. They govern all four contract tiers - the .NET
surface, the HTTP surface, the SQL schema plus persisted codes, and the telemetry an operator queries
- and a rename that violates one after 1.0.0 is a breaking change, not a cleanup.

## Words with one meaning

- **`reasonMessage` is the operator-authored justification on control verbs; `note` is the
  application-authored annotation** (`ctx.NoteAsync`, the `job.note-recorded` event). One word, one
  meaning: a control endpoint never takes a `note`, a handler API never records a `reasonMessage`.
- **`lock` is the object, `lease` is the grant.** A lock is the mutual-exclusion thing with an
  identity; a lease is a time-bounded, renewable ownership grant. Lease names mechanics only -
  columns, TTLs, heartbeats: the execution lease on `runtimes`, the lease on a lock hold - never a
  table or a store. `ILockStore.ExtendAsync` "extends the lease of the lock"; that layering is the
  rule.
- **`run` is the verb, `execution` is the noun.** You run a step (`RunStepAsync`,
  `RunWithLockAsync`, `builder.Run`, `RunAndWaitAsync`); the ledger records an execution
  (`ExecutionNumber`, `ExecutionStatusCode`, the Executions tab), with `attempt` as the retry-ledger
  synonym. "Run" never appears as a product noun. `NextRunAtUtc` stays: it is the verb phrase "next
  runs at". Anvil's certification "run" is internal-tool jargon, out of band.
- **`pause` stops a runnable thing's progress; `suspend` closes admission of new work.** This is
  why a job or schedule pauses while a namespace or tenant suspends, and why the outbox relay would
  pause rather than suspend.

## Identifier suffixes

- **`_ref`** is a rendered public handle: prefix + Crockford32, resolvable through the API
  (`job_ref`). A plain uuid is never a `_ref` - which is why the staging table's `outbox_id` kept
  its name in the 0.9.0 audit.
- **`_id`** is a numeric identity or a plain uuid.
- **`_key`** is a caller-supplied string identity (`deduplication_key`, `tenant_key`, `actor_key`).
- **`_token`** is opaque claim evidence (`claim_token`), proof of ownership rather than identity.

## Composition rules

- **Modifiers append to the complete base name, type marker included.** `_override` / `_effective`
  follow the full column they modify: `priority_code_override`, never `priority_override_code`.
  Composition outranks type-marker-last for modifiers; constraint-name dedupe handles any `_code`
  doubling mechanically.
- **Names at typed entry points optimize for brevity; names in inferred positions may carry full
  qualification.** `JobRequestBuilder.Create(...)` is typed at every call site and stays short;
  `JobEnqueueOptionsBuilder` appears only as an inferred lambda parameter and affords its length.
- **No `Job` prefix on types that are not jobs.** The prefix marks the job aggregate's own types,
  not "belongs to Acta".

## Read-model shapes

- **`Detail`** is a single-entity read model (`JobDetail`, `TenantDetail`).
- **`Snapshot`** is a point-in-time aggregate (`OverviewSnapshot`).
- **`ListItem`** is a paged element (`JobListItem`, `OutboxSourceListItem`).
- **`Item`** is a plain list element outside paging (`TagItem`, `OutboxQuarantinedItem` when the
  page wrapper already names the paging).

## Public identity: refs and the wire boundary

**A database integer is never a wire identity.** Surrogate keys are the engine's own bookkeeping: they
are dense, guessable, recycled by a restored backup, and meaningless to anyone outside the schema.
Every entity a caller addresses therefore carries a public ref instead, and its integer stays behind
the boundary. Ledger positions and counters are not caught by this rule - `jobEventId`,
`executionNumber`, `occurrenceCount`, `failureCount`, `version` are *values*, and a value is allowed to
be a number.

A ref is `prefix + 26 lowercase Crockford Base32 characters` encoding a UUIDv7's canonical big-endian
bytes. **The prefix is exactly three letters plus an underscore** - `job_`, `alr_`, `wrk_` - so a
pasted handle names its own entity and a ref for the wrong one fails to parse rather than resolving.
Parsing folds case and the Crockford `o`/`i`/`l` aliases; emission is always canonical lowercase. Refs
are minted in C# and passed into the write, never defaulted by the database, so a deduplicated repeat
keeps the ref its first firing minted.

The boundary has three sides, and only two of them are inside it:

- **HTTP/JSON and the CLI are inside.** No integer identity appears in a payload, a route, a query
  parameter, or a printed line. Catalog entities - definition, namespace, tenant - are addressed by
  their natural key (`{jobNamespace}/{jobName}`, `{tenantKey}`) and carry no ref at all.
- **The in-process .NET API is outside, by documented exception.** A handler is already running inside
  the engine, so making it round-trip a ref would buy nothing and cost a lookup. These categories are
  the whole list, not a pattern to extend:
  - **Read-model id members behind `[JsonIgnore]`.** Every public read model keeps its integer id as a
    property and hides it from JSON, so an in-process caller can pass it straight back into a store or
    a `List*Query` while the wire never sees it. This is the largest category by count and covers the
    job, event, alert, definition, namespace, tenant, worker, schedule, lineage, and explanation read
    models alike.
  - **Ambient identity on `JobContext`**: `JobId`, `NamespaceId`, `TenantId`, `WorkerId`.
  - **Addressing and resolution**: `JobLookup.ById`, and `IJobs.GetJobIdAsync`, which exists precisely
    to turn a lookup into the internal id an advanced caller then uses.
  - **Write outcomes, failures, and their ids**: `JobEnqueueOutcome`, `JobControlResult`, the other
    `Job*Outcome` records, `JobFailedException.JobId`,
    `DuplicateDeduplicationKeyInBatchException.ParentJobId` / its `ForChild` factory, and the
    `Acta.Testing` pass-throughs of these members (`ScenarioSession<TInput>.JobId`).
  - **The child-job APIs**, which take or return a child's id: `WaitChildAsync`, `WaitChildrenAsync`,
    `GetChildResultAsync`, `ChildJobOutcome.ChildJobId`, `MapItemOutcome<TKey>.ChildJobId`, and
    `JobEnqueueOptionsBuilder.ParentJobId(long)`.
  - **The `int`/`long` filter members on the `List*Query` records**, which the HTTP edge resolves refs
    into and never binds from the wire.
- **The SQL operator views are outside.** `acta.jobs_view`, `acta.events_view`, `acta.workers_view`,
  `acta.alerts_view` and their peers (`main.*` on SQLite) exist so a DBA can join the schema in a query
  window; they keep the internal integer columns because joining on them is the point. They project the
  ref columns alongside, so a view can answer both questions, but they are not a wire surface and no
  gate treats them as one.

## Wire conventions (HTTP/JSON)

- Identities are resource-qualified: `jobRef`, `alertRef`, `workerRef`, `outboxId` - never a bare `id`.
  `outboxId` keeps the `Id` suffix because it is a stored uuid column, not a rendered ref.
- `jobNamespace` is the namespace's name everywhere, on every tier.
- Instants end in `AtUtc` (`createdAtUtc`); every persisted instant is UTC.
- Narrowing booleans end in `Only` and are nullable (`unresolvedOnly`, `liveOnly`).
- Paging is cursor-only: `pageSize` + opaque `cursor`, `nextCursor`/`hasMore` on the page; no
  offset paging anywhere.
- **`expectedVersion` is the CAS token on requests; `version` on responses is the new current
  value.** The two never swap names.

## Telemetry: metric instruments

Telemetry is a contract like the other three: a dashboard query binds to an instrument name and an
operator's index maps a log field, so both outlive the release that introduced them. The rules were
unstated until now, which is how one histogram carried a capitalized name while its nine siblings
did not.

- **Instrument names are lowercase, dot-separated, and `acta.` prefixed** - `acta.executions`,
  `acta.claims`, `acta.wakeup.publish.failures`. The meter itself is `Acta`, because a consumer types
  it into `AddMeter` as a name; everything under it is lowercase, which is what a metrics backend and
  its query language read as one namespace.
- **Segments narrow left to right**: subject, then the thing that happened to it
  (`acta.lock.release.failures`, `acta.alert.projection.skips`).
- **The unit lives in the instrument, not in the name.** `acta.duration` declares `ms` as its unit
  argument; a name never restates what the unit already carries.
- **Tags stay low-cardinality** - `namespace`, `job_name`, `outcome`, `reason_code`, `result`. Job id
  and execution number are deliberately absent from metrics and live on the log scope and traces
  instead. `JobMetricsTests` pins instrument names, tag keys, and tag values.

## Telemetry: structured log fields

**There are eleven field names, and a new log line draws from them rather than inventing a twelfth.**
Every distinct name becomes a mapped field in the operator's index, so a tail of one-off names is not
free and it grows with every line anyone adds.

| Field | Type | Holds |
|-------|------|-------|
| `Namespace` | string | The job namespace's name |
| `JobId` | int64 | The job's internal id |
| `JobName` | string | The definition's job name |
| `Ref` | string | The public ref of the entity the line is about |
| `SubjectRef` | string | The second ref, when a line legitimately carries two |
| `Operation` | string | The named action or phase being reported |
| `Outcome` | string | How it ended |
| `Reason` | string | Why it ended that way |
| `Count` | int16 / int32 / int64 | A cardinal quantity: rows, bytes, attempt number |
| `DurationMs` | int32 / int64 / double | An elapsed or configured span |
| `Detail` | string | The genuinely one-off value nobody filters or groups by |

- **Durations are always milliseconds.** One name, one unit, so an average over the field means
  something; a `TimeSpan` converts at the call site rather than arriving as a second spelling.
- **`Ref` is any minted entity ref** - job, alert, worker - and the message around it names which
  (`alert {Ref}`), so there is no field per entity type. **`SubjectRef` is the second one**, for the
  line that legitimately carries two: the alert transport logs the alert under `Ref` and the job it
  concerns under `SubjectRef`.
- **What an operator filters or groups by stays distinct and typed; everything else is `Detail`.**
  Where a name was doing the explaining - a bare channel, a lock kind - the sentence says it instead,
  because the field is an index key and the message is what a human reads.
- **The categorical fields are rendered at the call site, not passed typed.** `Ref` and `SubjectRef`
  take the `alr_` / `job_` string rather than the ref struct, and `Outcome` and `Reason` take a token
  rather than the enum they often come from. Both for the same reason: a sink may serialize the
  property *object* instead of formatting it, and then a typed ref stores the wrapped uuid an operator
  cannot address, while an enum stores whichever of its name and its numeric value that sink prefers -
  making the mapped shape depend on where Acta happens to be hosted. `Count`, `DurationMs` and `JobId`
  stay numeric for the opposite reason: written as text they could not be averaged or bucketed at all,
  which is the whole point of splitting categorical from cardinal.
- **`Outcome` values are PascalCase; `Operation` and `Reason` values are kebab-case.** The split is not
  cosmetic, it follows what the value *is*. An `Outcome` reports a state C# already names - a
  `RunOnceOutcome`, a `CompleteExecutionAction` - so it mirrors the enum member exactly (`Succeeded`,
  `NothingClaimed`), and the handful of sites with no enum behind them spell their literal the same way
  (`Bounced`, `Suppressed`, `Quarantined`). `Operation` and `Reason` are invented labels rather than
  enum members - `exclusive-key-admission`, `outbox-relay`, `key-held`, `unknown-job` - so they take
  the identifier convention, which is kebab. Mirroring rather than mapping is deliberate: rendering an
  enum through a translation table would need a `_ =>` fallback that silently swallows a member added
  later, and `.ToString()` keeps the enum the single source of truth, so a new state needs no telemetry
  change at all.
- **Log `Outcome` values and metric `outcome` tags diverge on purpose, and neither should be "fixed" to
  match the other.** `JobExecution.OutcomeTag` emits `succeeded` / `failed` / `rescheduled` lowercase
  into `JobMetrics`, because metric tag values follow OpenTelemetry's lowercase convention and freeze
  at 1.0; a log field mirrors the C# code and does not freeze. Two surfaces, each internally consistent,
  beats one convention bent across both. Persisted code tokens are a third such surface and stay kebab
  for the same kind of reason - `AlertDeliveryStatusCode.Suppressed` is the C# member and `suppressed`
  is what the ledger stores, so a log line reporting that state writes `Suppressed`.
- **The vocabulary is enforced, not merely documented.** `LoggerParameterTypes.txt` at the repo root
  is the list, and Meziantou.Analyzer's `MA0135` (name not declared) and `MA0124` (argument is not a
  declared type) fail the build over `src/**/*.cs`. `.editorconfig` holds the two lines that switch
  them on and the reason for the `src` scope; `src/Directory.Build.props` holds the reference. Nothing
  outside `src` is held to it - concepts, specs and Anvil do not ship, so an index schema is not a cost
  they pay.
- **These names are not frozen the way the instrument names above are.** A metric name is a dashboard
  query's binding and breaks it when it moves; a log field can be added by agreeing it in
  `LoggerParameterTypes.txt`. What it costs is one more mapped field in every index that receives Acta's
  logs, forever, paid by whoever runs Acta rather than whoever wrote the line - so the bar is that
  filtering or grouping by the new name is worth one, not that a message reads better with it.
- **`JobLogScope` additionally stamps `ExecutionNumber`, `WorkerId` and `CorrelationKey`** as scope
  state, so every framework and handler line emitted during an attempt carries them. Those three sit
  outside the template set by necessity: scope keys must be unique across the scope stack, so they
  cannot fold into `Count` or `Detail`. They are also invisible to the gate, which reads template
  placeholders and not the key/value list a scope is built from, so `JobLogScopeTests` is what pins
  them.

Related: [contract evolution](../guide/contract-evolution.md) covers how consumers evolve their own
job contracts against a running ledger; this page covers how Acta names the surface it freezes.
