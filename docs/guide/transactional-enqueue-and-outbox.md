# Transactional enqueue and the external outbox

Sometimes a business-data change and an Acta enqueue must commit or roll back together. Acta offers two
deliberately separate atomic-enqueue paths, and one non-goal:

- **Direct transactional enqueue** when the business data and the Acta ledger share one database and
  provider: the enqueue joins a transaction you already own.
- **The external outbox** when they do not: a producer-owned table is written atomically with business
  data on the producer's own transaction, and an Acta-owned relay ingests it into a separately committed
  Acta ledger.
- **Not** a universal exactly-once guarantee. Execution stays at-least-once, and handlers still own
  idempotency for external side effects (see [Handler contract](./handler-contract.md#execution-semantics)).

Which one fits is a short decision, covered in [Choosing Acta](../choosing-acta.md#atomic-enqueue-with-business-data)
and in the [producer paths](#producer-paths) table below.

## Direct transactional enqueue

Every fire-and-forget enqueue shape has a twin that takes your already-started `DbTransaction` as its
first argument and inserts the job through it. A same-database business mutation and the enqueue then
share one commit outcome.

```csharp
await using var transaction = await connection.BeginTransactionAsync(ct);

// ... your business writes on the same connection/transaction ...

// Raw request:
await jobs.EnqueueAsync(transaction, request, ct);

// Typed, inferred from the input contract:
await jobs.EnqueueAsync(transaction, new SendReceipt(orderId), ct: ct);

await transaction.CommitAsync(ct);
```

When the application owns an explicit **EF Core** transaction, pass its underlying `DbTransaction`
through `GetDbTransaction()`:

```csharp
await using var transaction = await dbContext.Database.BeginTransactionAsync(ct);

// ... business changes on dbContext ...
await dbContext.SaveChangesAsync(ct);

await jobs.EnqueueAsync(transaction.GetDbTransaction(), request, ct);
await transaction.CommitAsync(ct);
```

The rules that make this safe:

- **You own the transaction lifecycle.** Acta never opens, commits, rolls back, disposes, or independently
  retries the supplied transaction. It composes the enqueue command against the configured Acta schema
  and executes it on the transaction's connection.
- **No worker wakeup.** The transactional overloads publish no wakeup, because Acta cannot know whether
  or when you commit and a pre-commit wake would be wrong. Normal worker polling is the pickup path.
- **The outcome is provisional until commit.** The returned `JobEnqueueOutcome` (its `JobId` / `JobRef`,
  and a `Deduplicated` action) becomes durable only when you commit. A rollback means that identity never
  existed. Do not publish or persist a provisional outcome before the commit succeeds.
- **Any enqueue exception requires a full rollback.** Known rejections are translated through the normal
  exception taxonomy with the provider exception retained as the cause, but the cross-provider contract is
  conservative: after any transactional-enqueue exception, roll back the whole business transaction. Do
  not continue, commit, or retry only the Acta command.
- **Deduplication stays optional here.** `DeduplicationKey` is optional as in normal enqueue. If you might
  retry the whole business transaction after an ambiguous commit, supply a stable key so a re-run cannot
  create a duplicate job.

Acta validates structure before executing: a null, detached, disposed, closed-connection, or
wrong-provider transaction fails before any command runs. It does not probe database identity, so you are
responsible for pointing the transaction at the database that holds the Acta schema.

There is deliberately no transactional `ExecuteAndWaitAsync`: the job is invisible to other connections
until you commit, so waiting inside the transaction would be a misleading timeout-shaped API.

## The external outbox

When the business database is not the Acta ledger, stage an Acta-shaped row in the business database and
let an Acta-owned relay carry it across. Staging is a validated projection of a `JobEnqueueRequest` into
one INSERT on your own open transaction, so the outbox row commits or rolls back with your business writes.

Staging is **zero-configuration**: no dependency injection, no `AddActa`, no ledger connection string, no
registration of any kind. A producer references its database's provider package (`Acta.Postgres`,
`Acta.SqlServer`, or `Acta.Sqlite`), writes `using Acta;`, and calls the extension on the concrete
provider transaction. The producer never learns where the ledger lives; it addresses work only by
namespace and job name. `JobEnqueueRequest` arrives transitively from the `Acta` package.

### Producer paths

| Producer situation | Path |
| --- | --- |
| Business data and Acta ledger in the same database, explicit transaction | Direct transactional `IJobs` enqueue (no relay, no pickup floor) |
| Producer database differs from the Acta ledger database | Provider-package `AddToActaOutboxAsync` on the caller's transaction; the relay completes the handoff |
| EF Core application | The same two rows, holding the transaction via `Database.GetDbTransaction()` |
| Any ORM wrapping the provider transaction | The same two rows, once the ORM exposes or unwraps the native transaction |

Same-database producers may stage through the outbox instead of enqueueing directly; it works, but costs
the relay hop and the five-second pickup floor. No path is a universal exactly-once guarantee; the
deduplication key remains the idempotency boundary. Staging without a registered relay produces rows that
nothing drains, so run the relay (below) on the same page as any producer.

### Stage a handoff (Dapper, Dapper.AOT, raw ADO.NET)

Three steps, and no more:

1. Reference the provider package for the producer's **own** database.
2. Add the [`{Provider}OutboxDdl.CreateScript()`](#the-ddl-api) output to the producer's migration system.
3. Call `AddToActaOutboxAsync` inside the business transaction.

```csharp
await using var tx = (NpgsqlTransaction)await connection.BeginTransactionAsync(ct);

// ... your business writes on the same connection/transaction ...

await tx.AddToActaOutboxAsync(new JobEnqueueRequest(
    "orders", "send-receipt", JobPayload.Text(orderId), DeduplicationKey: orderId), cancellationToken: ct);

// the business writes and the outbox row commit or roll back together
await tx.CommitAsync(ct);
```

The extension targets the **concrete** provider transaction (`NpgsqlTransaction`, `SqlTransaction`,
`SqliteTransaction`), not `DbTransaction`, so provider dispatch is a compile-time choice. `table` defaults
to `acta_outbox` and `schema` to the provider's default; both take the same lowercase identifier
validation as the DDL API and the relay source, so producer and relay cannot disagree on the physical name.

`AddToActaOutboxAsync` is **void**: one INSERT executed immediately on your open transaction, no receipt,
no `JobId`, no `JobRef`. Build the request explicitly (through `JobRequestBuilder`, a factory in your
contracts assembly, or the `JobEnqueueRequest` constructor) and select the exact `JobPayload`.
`JobPayload.Json` provides Acta's canonical JSON defaults; binary or custom formats stay explicit. There
is no typed outbox overload family and no best-effort serialization fallback.

Staging adds two validations beyond normal enqueue:

- `DeduplicationKey` must be **non-null**. The relay reuses that caller-supplied key unchanged for every
  delivery attempt; it is both the business deduplication policy and the idempotency boundary for a relay
  crash between the Acta commit and the source-row cleanup. Acta generates no fallback transport key.
- `ParentId` must be **null**. External records may request root jobs only; a root can create child jobs
  through the normal in-Acta composition APIs after ingestion.

`DelaySeconds` and `NextRunAtUtc` keep their normal meaning and mutual exclusion. `DelaySeconds` is
resolved when the relay ingests the request, so outbox latency may make the producer-to-execution interval
longer, never shorter.

**Failure contract.** Request validation fails before any I/O with the same exception taxonomy as enqueue
validation. The transaction gets the same structural fail-fast rules as the transactional `IJobs`
overloads: a detached, completed, or connection-not-open transaction is rejected before the command runs.
A database error surfaces as the provider's exception on your transaction, and your rollback obligation is
the same as for any failed statement in your own unit of work.

### EF Core producers

There is no Acta EF Core package. EF Core applications use the same two paths as every other producer,
through `Database.GetDbTransaction()`:

```csharp
await using var efTx = await context.Database.BeginTransactionAsync(ct);
// ... business SaveChanges ...

// Same database as the Acta ledger: transactional IJobs enqueue.
await jobs.EnqueueAsync(efTx.GetDbTransaction(), request, ct);

// Different database: provider staging on the unwrapped concrete transaction.
await ((NpgsqlTransaction)efTx.GetDbTransaction()).AddToActaOutboxAsync(request, cancellationToken: ct);

await efTx.CommitAsync(ct);
```

If a team prefers to wrap the ceremony once, a small helper does it:

```csharp
public static async Task StageInBusinessTransaction(
    this DbContext context, JobEnqueueRequest request, CancellationToken ct)
{
    await using var efTx = await context.Database.BeginTransactionAsync(ct);
    await context.SaveChangesAsync(ct);
    await ((NpgsqlTransaction)efTx.GetDbTransaction()).AddToActaOutboxAsync(request, cancellationToken: ct);
    await efTx.CommitAsync(ct);
}
```

Apply the canonical table through a normal EF migration by piping the DDL API into `migrationBuilder.Sql`:

```csharp
protected override void Up(MigrationBuilder migrationBuilder) =>
    migrationBuilder.Sql(PostgresOutboxDdl.CreateScript());
```

The DDL API and the relay together are the canonical-shape authority; no EF model is authoritative for
anything.

### OrmLite and other ORMs

Any journey above works once the ORM exposes or unwraps the native provider transaction. OrmLite's
`IDbTransaction ToDbTransaction()` returns the underlying transaction, after which you cast to the
concrete provider type and call the extension. Acta does not promise compatibility with opaque transaction
wrappers: the receiver is the concrete `NpgsqlTransaction` / `SqlTransaction` / `SqliteTransaction`, never
`DbTransaction` or `IDbTransaction`.

### The DDL API

Each provider package ships a provider-named static DDL source. The names differ per provider because
identical fully qualified names would collide in the flat `Acta` namespace when an application references
more than one provider:

```csharp
PostgresOutboxDdl.CreateScript(table: "acta_outbox", schema: null);
SqlServerOutboxDdl.CreateScript(table: "acta_outbox", schema: null);
SqliteOutboxDdl.CreateScript(table: "acta_outbox");
```

The output is a plain, non-idempotent migration script (your migration system owns run-once semantics):
the canonical `CREATE TABLE` with provider-correct types and UTC-clock defaults, the primary key, the two
named claim indexes, and the named check constraints, with any table/schema override rendered correctly.
`table` and `schema` take the same lowercase identifier validation as the staging extension and the relay
source; `schema: null` uses the provider's established default. It is documentation input, not an
installer: paste or pipe it into DbUp, Flyway, `migrationBuilder.Sql(...)`, or a hand migration. Acta never
executes it.

The DDL ships **two indexes** (`ix_acta_outbox_due` and `ix_acta_outbox_claims`); keep them, claims depend
on them.

### Run the relay on a worker

Attach one external-outbox source to a worker namespace with `AddOutboxRelay`. The source provider is
selected on the builder and is **independent of the ledger provider**: a PostgreSQL-backed Acta worker can
relay a SQL Server producer outbox when the process installs both provider packages.

```csharp
services.UseActa(j =>
{
    j.UsePostgres(pg => pg.ConnectionString = ledgerConnectionString);   // the Acta ledger
    j.Run<OrdersJobs>(namespaceName: "orders", worker =>
    {
        worker.AddOutboxRelay("orders-outbox", source =>
            source.UseSqlServer(o => o.ConnectionString = businessDbConnectionString));  // the source
    });
});
```

Registration adds the `sys.outbox` job plus its `sys.recovery` and `sys.alerts` dependencies to that
namespace, even when `JobsOptions.RegisterSystemJobs` is `false`: that switch suppresses automatically
added framework jobs, not the dependencies of a relay you asked for. It does not force `sys.retention`. A
namespace registers zero or one source; a process with several `Run` namespaces can attach one source to
each, and each namespace drains its own source independently.

Nothing connects to the source at startup. Startup rejects structurally invalid registration (no provider,
two providers, a second source, an invalid name/override). Connectivity is proven inside `sys.outbox`; an
unavailable or missing source fails and alerts that relay tick, then retries without blocking unrelated
jobs. An incompatible or hand-modified table fails the claim or finalize SQL itself with the
provider's error, failing only `sys.outbox` under its normal alert.
The tested DDL API is the drift-free way to build the table; the one silent case, a deleted index degrading
claims to a scan, is the integrator's responsibility, which is why the DDL ships both indexes.

## Ambient System.Transactions scopes are rejected

`TransactionScope` is not an integration surface for Acta, and Acta-owned connections never enlist in an
ambient transaction. Enlistment cannot be delivered correctly: `System.Transactions` exposes only the
`Transaction`, never the connections in it, so an owned path must open its own connection, and a second
connection in the scope forces distributed-transaction escalation the providers cannot honor (Npgsql
supports only single-connection enlistment and throws; cross-platform .NET has no general distributed
support; SQLite participation is undefined). The explicit `DbTransaction` overload **is** the
connection-reuse path, with the handle supplied by the one party who has it.

Every Acta-owned session therefore checks `Transaction.Current` when it opens a connection and fails fast:

> An ambient System.Transactions.TransactionScope is active, and Acta-owned connections never enlist in
> one. Rewrite to one of: pass the open transaction to the transactional IJobs enqueue overload for an
> atomic commit in the same database; stage through the provider outbox primitive (AddToActaOutboxAsync)
> for a different database; or wrap this call in a TransactionScope(TransactionScopeOption.Suppress) for a
> deliberate independent Acta commit.

The two correct rewrites are the two atomic-enqueue paths on this page. A caller who deliberately wants an
independent Acta commit inside a scope wraps the call in `TransactionScope(TransactionScopeOption.Suppress)`,
visibly in their own code. The caller-transaction enqueue overloads and the staging primitives are
unaffected: their transaction was supplied explicitly and never reaches an owned connection open.

## Cadence, pickup, and crash recovery

The `sys.outbox` slot runs every **five seconds** by default. Because producer staging sends no wakeup
across databases, that cadence plus normal worker discovery is the expected pickup-latency floor. There is
no separate interval setting; cadence is managed through the existing durable schedule controls for
`sys.outbox/default`.

Distinguish the healthy cadence from crash recovery. The source claim lease reuses the worker-wide
`JobsOptions.LeaseTtlSeconds` (180 seconds by default) rather than adding an outbox-specific setting. If a
worker crashes mid-relay, its claimed source rows may stay invisible for up to that lease window (three
minutes by default) before another relay reclaims them. Healthy pickup still runs on the five-second
cadence; the lease window only bounds recovery. Expired claims are safe to repeat because finalization is
token-CAS and target enqueue is deduplicated.

The job declares `AuditLevel = Failures` and the `SysCritical` alert profile, matching the other quiet
maintenance jobs: idle and successful ticks produce no audit events, while failures and quarantine alerts
stay visible.

One tick processes at most 20 source batches of 256 rows (5,120 rows); the next tick continues any
backlog. All replicas of the namespace compete for the same durable recurring slot. When a target batch is
deterministically rejected, the relay retries each claimed group individually within the same tick budget,
so offending rows are isolated, good rows proceed, and the budget is honored.

## Priority and ordering

Among due rows the relay claims in urgent-before-FIFO order:
`COALESCE(priority_code, 50) DESC, next_attempt_at_utc ASC, created_at_utc ASC, outbox_id ASC`. A null
`priority_code` means no override: it is treated as Normal (50) only for ordering the transport queue and
stays null in the reconstructed request, leaving the effective priority to the target job definition.

## Retry and quarantine

Both `Inserted` and `Deduplicated` are successful relay outcomes. Once either safely commits in the Acta
ledger, the relay **deletes** the source row. The outbox is a transient handoff queue, not a second audit
ledger.

Rows that cannot be delivered are handled by classification:

- **Infrastructure failures** (connection loss, timeout, deadlock, target unavailability) never consume a
  quarantine budget. They retry indefinitely with capped backoff and fail the tick so the alert fires.
- **Recoverable row rejections** (routing and target-state rejections such as an unknown route or a
  suspended namespace/tenant) increment `failure_count`, reschedule with increasing backoff, and
  quarantine at a configurable threshold (default **five**). Below the threshold they are logged and
  rescheduled without failing or alerting the tick. Row retries reuse `Backoff.Default` (one minute
  growing exponentially to eight hours with ten percent jitter), so a continuously invalid route reaches
  quarantine after roughly fifteen minutes.
- **Malformed or oversize rows** quarantine **immediately**. A structurally unreadable row (for example an
  invalid stored tag shape) and a payload over the target's `JobsOptions.MaxInlinePayloadBytes` are
  deterministic and are not worth retrying.

The **payload-limit boundary** is deliberate: staging validates payload format/data consistency but does
**not** duplicate the target worker's `MaxInlinePayloadBytes`. The relay applies that existing hard enqueue
cap. An oversized row is a deterministic rejection, is quarantined, and raises the normal alert; the hard
cap is never softened into a warning.

Quarantined rows are excluded from normal claims, retained in the same table under `status_code = 90`, and
surfaced through the `sys.outbox` alert path. The tick logs each quarantined `outbox_id`, then fails once
with a bounded summary (source, count, a sample of ids). A recoverable rejection below the threshold does
not fail the tick; only an infrastructure failure or an actual transition to Quarantined does. An operator
must explicitly requeue or delete a quarantined row: see
[SQL recipes · quarantined outbox rows](./sql-recipes.md#quarantined-outbox-rows).

## Looking up the resulting job

Staging returns no identity, so the producer's durable lookup handle is `(JobNamespace, DeduplicationKey)`.
After relay, resolve the public job identity by that key:

```csharp
var jobId = await jobs.ResolveJobIdAsync(
    JobLookup.ByDeduplicationKey("orders", orderId), ct);
```

A deduplication-key lookup matches root jobs only. It returns `null` until the relay has ingested the
request, so treat a null as "not yet relayed" rather than "lost".

## The canonical table

The external table is primarily relational: stable root-enqueue fields mirror the raw enqueue contract as
typed columns, and six operational columns carry relay status, lease, retry, failure, and error state. It
is producer-owned and therefore not part of the generated Acta ledger [data model](../reference/data-model.md);
Acta versions its shape here, and the [DDL API](#the-ddl-api) emits it.

| Column | Required | Contract |
| --- | --- | --- |
| `outbox_id` | yes | Client-generated GUID primary key; internal transport identity. |
| `job_namespace` | yes | ASCII, 64-character public cap; canonical user namespace. |
| `job_name` | yes | ASCII, 128 characters; canonical user job name. |
| `input_format_id` | yes | Byte-sized format id; `0` is None. |
| `input_data` | no | Provider binary payload; null exactly when the format id is `0`. |
| `deduplication_key` | yes | ASCII, 128 characters; target root-job identity within the namespace. |
| `correlation_key` | no | ASCII, 64 characters. |
| `exclusive_key` | no | ASCII, 128 characters. |
| `priority_code` | no | Byte-sized target priority override; null leaves it to the definition. |
| `next_run_at_utc` | no | Absolute target earliest-run instant. |
| `delay_seconds` | no | Non-negative delay resolved at target ingestion. |
| `tenant_key` | no | ASCII, 128 characters. |
| `meta` | no | Provider-native JSON/text root object; initially only `tags`. |
| `created_at_utc` | yes | Immutable producer-database UTC default. |
| `status_code` | yes | Relay state; default `10` (Pending). `20` Claimed, `90` Quarantined. |
| `failure_count` | yes | Non-negative row-rejection count; default `0`. |
| `next_attempt_at_utc` | yes | Producer-database UTC default; retry eligibility. |
| `claim_token` | no | GUID CAS token owned by the current claim. |
| `claim_until_utc` | no | Source-database UTC lease expiry. |
| `last_error` | no | Most recent bounded diagnostic, truncated to 512 characters. |

Two canonical claim indexes carry fixed short names so provider truncation never changes their identity:
`ix_acta_outbox_due` leads with `(status_code, next_attempt_at_utc, priority_code, created_at_utc, outbox_id)`
for the due predicate, and `ix_acta_outbox_claims` is `(status_code, claim_until_utc)` for expired claims.
Check constraints enforce the payload format/data pair, non-negative delay and failure count, the mutual
exclusion of `next_run_at_utc` and `delay_seconds`, the allowed priority and status codes, valid
root-object JSON when `meta` is non-null, and the status/claim-field invariant (Claimed requires both
claim fields; Pending and Quarantined require both null).

`meta.tags` is an ordered JSON array of `{"name": ..., "value": ...}` objects (`value` is a string or JSON
`null`), for example `{"tags":[{"name":"tenant","value":"acme"},{"name":"urgent","value":null}]}`. A field
that has its own relational column is never duplicated in `meta`.

## Dashboard visibility

The dashboard exposes the `sys.outbox` job, its events, and its alerts, the same as any other system job.
It does not connect to the producer database or render external-outbox rows directly in v1; direct row
management remains a compatible future feature. Inspect and manage rows with the
[SQL recipes](./sql-recipes.md#quarantined-outbox-rows) against the producer database until then.
