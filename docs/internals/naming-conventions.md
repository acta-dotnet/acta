# Naming conventions

The naming rules the 0.9.0 surface froze under. Both pre-freeze audits (the .NET surface audit and
the schema audit) found drift that a stated rule would have prevented, so the rules are stated here
once and every later name is measured against them. They govern all three contract tiers - the .NET
surface, the HTTP surface, and the SQL schema plus persisted codes - and a rename that violates one
after 1.0.0 is a breaking change, not a cleanup.

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

Related: [contract evolution](../guide/contract-evolution.md) covers how consumers evolve their own
job contracts against a running ledger; this page covers how Acta names the surface it freezes.
