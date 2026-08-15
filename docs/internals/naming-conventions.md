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

## Wire conventions (HTTP/JSON)

- Ids are resource-qualified: `jobRef`, `alertId`, `outboxId` - never a bare `id`.
- `jobNamespace` is the namespace's name everywhere, on every tier.
- Instants end in `AtUtc` (`createdAtUtc`); every persisted instant is UTC.
- Narrowing booleans end in `Only` and are nullable (`unresolvedOnly`, `liveOnly`).
- Paging is cursor-only: `pageSize` + opaque `cursor`, `nextCursor`/`hasMore` on the page; no
  offset paging anywhere.
- **`expectedVersion` is the CAS token on requests; `version` on responses is the new current
  value.** The two never swap names.

Related: [contract evolution](../guide/contract-evolution.md) covers how consumers evolve their own
job contracts against a running ledger; this page covers how Acta names the surface it freezes.
