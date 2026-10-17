# Naming conventions

The naming rules the public surface is held to. The surface audits before 1.0 found drift that a
stated rule would have prevented, so the rules are stated here once and every later name is measured
against them. They govern all four contract tiers - the .NET surface, the HTTP surface, the SQL
schema plus persisted codes, and the telemetry an operator queries - and a rename that violates one
after 1.0.0 is a breaking change, not a cleanup.

## Words with one meaning

- **`reasonMessage` is the operator-authored justification on control verbs; `note` is the
  application-authored annotation** (`ctx.NoteAsync`, the `job.note-recorded` event). One word, one
  meaning: a control endpoint never takes a `note`, a handler API never records a `reasonMessage`.
- **`lock` is the object, `lease` is the grant.** A lock is the mutual-exclusion thing with an
  identity; a lease is a time-bounded, renewable ownership grant. Lease names mechanics only -
  columns, TTLs, heartbeats: the execution lease on `runtimes`, the lease on a lock hold - never a
  table or a store. `ExtendWorkerLeases` "extends the leases" of the executions and lock holds a
  worker holds; that layering is the rule.
- **`run` is the verb, `execution` is the noun.** You run a step (`RunStepAsync`,
  `RunWithLockAsync`, `builder.Run`, `RunAndWaitAsync`); the ledger records an execution
  (`ExecutionNumber`, `ExecutionStatusCode`, the Executions tab), with `attempt` as the retry-ledger
  synonym. "Run" never appears as a product noun. `NextRunAtUtc` stays: it is the verb phrase "next
  runs at". Anvil's certification "run" is internal-tool jargon, out of band.
- **`pause` stops a runnable thing's progress; `suspend` closes admission of new work.** This is
  why a job or schedule pauses while a namespace or tenant suspends, and why the outbox relay would
  pause rather than suspend.
- **An execution completes; the finalizer writes the completion.** `CompleteExecutions` is the
  operation, `ExecutionCompletion` is what an attempt leaves for it, and `WorkerFinalizer` writes it.
  "Settle" is retired for executions; alert delivery keeps its own settlement vocabulary.
- **`State` is in memory, `Code` is persisted.** An enum a running process keeps for itself takes
  `State` (`InFlightJobState`); one the ledger stores takes `Code` (`JobStatusCode`), and the two
  never share members by accident.
- **A background loop is named for its agent; a channel for what crosses it.** A worker's own agents
  take the `Worker` prefix, as its runtime and context do: `WorkerClaimer`, `WorkerExecutor`,
  `WorkerFinalizer`, `WorkerHeartbeat` (the established word, kept over an invented agent noun). A loop
  that serves a system job is named for its job's work (`OutboxRelay`). The channels between the
  worker's agents are `Claimed` and `Completed`.

## Identifier suffixes

- **`_ref`** is a rendered public handle: prefix + Crockford32, resolvable through the API
  (`job_ref`). A plain uuid is never a `_ref` - which is why the staging table's `outbox_id` kept
  its name in the 0.9.0 audit.
- **`_id`** is a numeric identity or a plain uuid.
- **`_name`** is a name: Acta's vocabulary, ASCII `dotted.kebab-case`, trimmed and lowercased on input, so it compares
  equal under any collation (`tenant_name`, `rate_name`, `lanes.name`, `definitions.name`).
- **`_key`** is a key: the caller's identity, printable ASCII without whitespace, kept exactly as given and compared
  bytewise (`deduplication_key`, `concurrency_key`, `lock_key`); `correlation_key` is a key Acta carries without
  comparing. Only a deduplication key reserves `sys.`, because the system jobs' keys share its index.
- **`display_name`, `value`, `message`, `actor`** are display text: any Unicode within a cap, never compared.
- **`_token`** is opaque claim evidence (`claim_token`), proof of ownership rather than identity.

## Composition rules

- **Modifiers append to the complete base name, type marker included.** `_override` / `_effective`
  follow the full column they modify: `priority_code_override`, never `priority_override_code`.
  Composition outranks type-marker-last for modifiers; constraint-name dedupe handles any `_code`
  doubling mechanically.
- **Names at typed entry points optimize for brevity; names in inferred positions may carry full
  qualification.** `JobRequestBuilder.Create(...)` is typed at every call site and stays short;
  `JobEnqueueOptionsBuilder` appears only as an inferred lambda parameter and affords its length.
- **No `Job` prefix on types that are not jobs.** The prefix marks the job aggregate's own types and
  what describes one job (`JobStatusCode`, `JobLogScope`, `InFlightJob`); never a type that merely
  belongs to Acta or acts on many jobs, which a worker's agents do.
- **An `Operations/` group is plural, an `Engine/` box is singular.** A group is the aggregate
  (`Jobs`, `Workers`, `Alerts`); a box is named for its capability (`Worker` holds one worker's runtime,
  `Alerting` is the process, `Catalog` is what a deployment declares). A system job's class is
  `<Sys>Job` (`OutboxJob`, `RetentionJob`), named for the job, not for what it relays or sweeps.
- **Verb first, plural for a set.** An operation, its routine, and a SQL Server table type read as the
  action they carry, with a plural noun when they act on a set: `ClaimJob` and `ClaimJobs` (`claim_job`,
  `claim_jobs`), `CompleteExecutions`. A table type is named for the routine that takes it plus `_batch`
  (`enqueue_jobs_batch`, `complete_executions_batch`); `Batch` is never the noun.

## Type names: one suffix per role

A type is named `{Subject}{Role}`, and each role has exactly one suffix. An operation is named `VerbNoun` (`PauseJob`, `ListJobs`), and that name is also its folder, its SQL file stem, and its method on the group's operations class without `Async` ([source layout](source-layout.md)).

| Role | Suffix | Example |
|---|---|---|
| Input to a write operation (internal) | `Command` | `PauseJobCommand` |
| Input to a read operation, including public list filters | `Query` | `ListJobsQuery` |
| Executes one command or query | `Handler` | `PauseJobHandler` |
| What an operation returns | `Response` | `PauseJobResponse : IJobControlResponse`, `GetJobResponse` |
| Bound straight from a reader, internal, no public twin | `Row` | `ClaimJobsRow` |
| The column values one batch write binds, internal | `Values` | `JobEnqueueValues` |
| Element of an operation's response | `Item` | `ListJobsItem`, `GetTagsItem` |
| A group's operations class | the group's name plus `Operations` | `JobsOperations : IJobs`, `ExecutionOperations` |
| HTTP request body of a route | `Request` | `PauseJobRequest`, `UpsertJobTagsRequest` |
| Route table of a group / one route | `Endpoints`, `Endpoint` | `JobsEndpoints`, `EnqueueJobEndpoint` |
| Table model, one class per table, named by the table's singular | `Entity` | `JobEntity`, `RuntimeEntity` |
| Persisted enum, options, fluent builder, error, attribute | `Code`, `Options`, `Builder`, `Exception`, `Attribute` | `JobStatusCode` |
| How a job, an attempt, or a wait ended, for handler code only | `Outcome` | `JobOutcome`, `ChildJobOutcome`, `SignalWaitOutcome` |
| An in-memory registry, built at startup or kept by a running worker | `Registry` | `JobDescriptorRegistry`, `IJobPayloadSerializerRegistry` |

- **A `Get` returns its `GetXResponse`, and a `List` returns its page.** `GetX` returns `GetXResponse`, and `null` when nothing is found; a value it answers is the response's one member, as a command's is (`GetJobStatusResponse(JobStatusCode Status)`). `ListX` always returns its own `ListXResponse : IPagedResponse<ListXItem>`, never an unpaged list, and its element `ListXItem` sits in its folder beside the query and the response. `IPagedResponse<T>` is the one paging contract, the interface every list response implements. A response's parts are records of their own beside it, named `<Operation><Part>` (`GetJobExplanationWait`) and never shared with another operation, and a `Get` that answers a list carries it as `Items`, of `GetXItem` (`GetTagsResponse`). The typed `<T>` facade methods (`GetResultAsync<T>`, `RaiseSignalAsync<T>`) are conveniences over the operation, not operations of their own.
- **Every operation has its own input and its own response.** Responses that belong together share an interface (`IJobControlResponse`), never a class, so changing one operation's response changes no other operation. A subject's `GetXResponse` and its list's `ListXItem` stay two types for the same reason, even where they match today.
- **Every value a command answers is its `XResponse`, and a command that answers nothing returns nothing.** A flag, a count, a token, or a list becomes the response's one member, named for what it means (`ReleaseLockResponse(bool Released)`, `ClaimOutboxResponse(IReadOnlyList<ClaimOutboxRow> Rows)`); a row the handler returns unchanged is the response itself, so no wrapper only forwards its members. No command returns an empty record or a bare row.
- **`EnqueueJobRequest` and `EnqueueJobResponse` are the SDK's enqueue contract**, the pair behind every `IJobs.EnqueueAsync` overload, named verb first like every operation's response; `EnqueueBatchAsync` answers `EnqueueJobsResponse`, one `EnqueueJobResponse` per request in request order. It is the one `Request` outside `Acta.AspNetCore`; `POST /jobs` reads its own `EnqueueJobRequest` there, and the SDK record never travels as JSON.
- **A route's bodies are named for its operation.** A route that reads a JSON body reads its own `XRequest`, declared in `src/Acta.AspNetCore/Operations/<Group>/X/`, and no body serves two routes; bodies that belong together share an interface (`IControlRequest`, `IUpsertTagsRequest`). A route answers with the SDK's `XResponse` whenever the wire shape is the SDK's. When the wire renders or composes what the facades answer, the route is its own operation, with its own `XResponse` in its folder: `GetJobDetail` composes the job screen, `GetJobInputBody` renders a payload, and `GetCapabilities` reports the host.
- **`Row` means the database loaded it.** Every `*Row` carries `[DbProjection]`. A model the public API exposes is never a row; SQL fills it directly. A row is declared in its operation's folder as `<Operation>Row`, or `<Operation><Part>Row` when one operation reads several shapes, and no other operation names it.
- **A type takes the subject of the table it describes.** `StepStatusCode` maps `steps.status_code`, `DefinitionAuditLevelCode` maps `definitions.audit_level_code`, and `EventReasonCode` maps `events.reason_code`. `Job` prefixes only what describes a job itself (`JobStatusCode`, `JobRef`).
- **`Outcome` is a handler-code noun, never an operation's return.** It names how a job, a child, a map, or a wait ended inside handler code; what an operation returns is a `Response`.
- **Banned suffixes:** `Result`, `Dto`, `Data`, `Info`, `Model`, `Entity` outside table models, `Record`, `Item` outside an operation response's element, `ListItem`, `Detail`, `Snapshot`, `Set`, `Store`, `Service`, `Api`, `Page`, `PageRequest`, `Decision`, `HttpRequest`, `HttpResponse`, and `Request` outside `Acta.AspNetCore` but for the SDK's `EnqueueJobRequest`.
- **`TypeNamingTests` enforces the banned suffixes, the `Row` rule, and the `Entity` rule** over every shipped assembly, and **`HandlerResponseTests` holds `XHandler.HandleAsync` to taking its own `XCommand` or `XQuery`, `HandleAsync(XCommand)` to returning `XResponse` or nothing, `HandleAsync(ListXQuery)` to returning `ListXResponse`, a `GetX` handler to returning `GetXResponse`, and every row, command, query, response, and response element to its slice, with every other type in a slice, enums included, named for its operation.** **`RouteBodyTests` holds every mapped route's body to its own `XRequest`, on no other route, its answer to an `XResponse`, and every request and response in `Acta.AspNetCore` to the folder named for it.**

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
  their natural key (`{jobNamespace}/{jobName}`, `{tenantName}`) and carry no ref at all.
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
  - **Write outcomes, failures, and their ids**: `EnqueueJobResponse`, `IJobControlResponse.JobId`, the other
    `Job*Outcome` records, `JobFailedException.JobId`,
    `DuplicateDeduplicationKeyInBatchException.ParentJobId` / its `ForChild` factory, and the
    `Acta.Testing` host helpers that take a job id (`IActaTestHost.RunOnceAsync(long)` and the `Force*` helpers).
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
- **Tags stay low-cardinality** - `namespace`, `job_name`, `outcome`, `reason_code`, `result`, `state`. Job id
  and execution number are deliberately absent from metrics and live on the log scope and traces
  instead. `ActaMetricsTests` pins instrument names, tag keys, and tag values.

## Telemetry: structured log fields

**There are twelve field names, and a new log line draws from them rather than inventing a thirteenth.**
Every distinct name becomes a mapped field in the operator's index, so a tail of one-off names is not
free and it grows with every line anyone adds.

| Field | Type | Holds |
|-------|------|-------|
| `Namespace` | string | The job namespace's name |
| `JobId` | int64 | The job's internal id |
| `JobName` | string | The definition's job name |
| `Attempt` | int32 | An attempt or pass number |
| `Ref` | string | The public ref of the entity the line is about |
| `SubjectRef` | string | The second ref, when a line legitimately carries two |
| `Operation` | string | The named action or phase being reported |
| `Outcome` | string | How it ended |
| `Reason` | string | Why it ended that way |
| `Count` | int16 / int32 / int64 | A cardinal quantity: rows, bytes |
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
  `RunOnceStatus`, a `CompleteExecutionsAction` - so it mirrors the enum member exactly (`Succeeded`,
  `NothingClaimed`), and the handful of sites with no enum behind them spell their literal the same way
  (`Bounced`, `Suppressed`, `Quarantined`). `Operation` and `Reason` are invented labels rather than
  enum members - `concurrency-key-admission`, `outbox-relay`, `key-held`, `unknown-job` - so they take
  the identifier convention, which is kebab. Mirroring rather than mapping is deliberate: rendering an
  enum through a translation table would need a `_ =>` fallback that silently swallows a member added
  later, and `.ToString()` keeps the enum the single source of truth, so a new state needs no telemetry
  change at all.
- **A string value is delimited by parentheses in the message text - `({Detail})`, `({Reason})`,
  `({Outcome})`, `({Operation})`, `({Namespace})`, `({Ref})`, `({SubjectRef})`, `({JobName})` - rather
  than recased to stand out.** A kebab token sitting bare in an English sentence has no visible
  boundary. Recasing the value to give it one makes the spelling do a delimiter's job, which is why the
  spelling rules above and this one are separate: the parens make the boundary unambiguous and the
  value greppable, and the value keeps whatever spelling its own nature earns it.
  Numeric fields (`{JobId}`, `{Count}`, `{DurationMs}`) are never parenthesized - a number already reads
  as a number - and a placeholder already delimited by a `key=value` pair, such as `ns={Namespace}` in
  the alert transport's line, is left alone rather than double-delimited. This one rule replaces the
  single quotes some templates used around a value (`'{Detail}'`), so the vocabulary has one delimiter
  instead of three.
- **Log `Outcome` values and metric `outcome` tags diverge on purpose, and neither should be "fixed" to
  match the other.** `WorkerFinalizer.OutcomeTag` emits `succeeded` / `failed` / `rescheduled` lowercase
  into `ActaMetrics`, because metric tag values follow OpenTelemetry's lowercase convention and freeze
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
