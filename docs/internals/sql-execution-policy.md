# SQL execution policy

PostgreSQL and SQL Server execute Acta ledger mutations through atomic routines; reads use embedded
SQL and installed views; SQLite implements the same store contracts with transactional inline SQL.

This is the rc.2 execution policy referenced by [the design decisions](design.md). The server
providers each install 56 routines: the existing 54 mutation operations plus `UpdateAlertDelivery`
and `ResolveJobAlerts`. Hot paths, control operations, catalog registration, and maintenance all
follow the same rule. Routine placement is a compatibility decision, not an inference from SQL size.

## Resources and execution

- `Operation.routine.sql` defines an installed atomic ledger operation on PostgreSQL or SQL Server.
- `Operation.sql` is executable SQL shipped with the binary. Ledger reads use this form on every
  provider; SQLite also uses it for mutations.
- `Name.view.sql` defines an installed read model. Views remain database objects, queryable with SELECT.

`SqlResourceCatalog` resolves exactly one implementation for each operation/provider. Missing or
ambiguous resources fail before a connection opens. `DbSession` uses the resource kind to configure
the command. There is no provider-wide routine switch. `SupportsBatchCompletion` describes only
native batch completion, which SQLite does not implement.

Shared stores use query methods for reads and execute methods for mutations. Stores never decide
transaction ownership from the number of SQL statements. Parameter binding stays provider-specific,
including PostgreSQL arrays and SQL Server TVPs.

**Every semantic mutation store call is atomic.** The database changes and their audit evidence
commit or roll back together. A higher-level API may orchestrate several store calls; this rule does
not make those separate calls one transaction.

## Transaction and retry ownership

| Execution path | Atomicity and ownership |
| --- | --- |
| Owned PostgreSQL ledger call | One function invocation, atomic in the database statement transaction. |
| Owned SQL Server ledger call | One procedure invocation; the procedure begins and commits its transaction inside the call. |
| Owned SQLite mutation | `DbSession` starts an immediate transaction, executes and reads the file, then commits. |
| Caller-transaction enqueue | All three providers join the supplied transaction. Acta never commits, disposes, or retries it. |
| Ordinary read | No additional write transaction. |

SQL Server procedures use `SET XACT_ABORT ON` and `TRY/CATCH`. They record `@@TRANCOUNT` at entry,
begin and commit only if they own the transaction, and roll back an owned active transaction before
rethrowing errors. An outer transaction remains the owner's responsibility. SQL Server does not
provide independent nested commits: a database error can invalidate or abort the caller's transaction.

Normal server mutations require one operation command, with no separate C# begin/commit calls.
Do not equate ADO.NET method counts with measured network round trips; driver batching and protocol
behavior matter. SQLite's immediate transaction is an in-process database operation.

The session consumes the intended business result and drains the reader, including trailing errors,
before reporting success. A C# mapping failure cannot promise rollback of an already committed server
operation. SQLite can roll back mapping failures while its owned transaction remains uncommitted.

Owned execution retries only confirmed aborted conflicts: PostgreSQL deadlock `40P01`, SQL Server
deadlock victim `1205`, and SQLite busy/locked conflicts after cleanup. Failed attempts release their
resources before another attempt opens. Binding, mapping, and disposal failures are not database-abort
evidence and never trigger replay. Connection failures or uncertain commit outcomes are not automatically
replayed. Session-owned commit and final teardown remain outside the retry region.

## Explicit exceptions and maintenance

SQL Server's `locks` table must not use `HOLDLOCK` or serializable gap probes. Acquire uses a
conditional point update followed by an insert on a miss; the primary key arbitrates competing
inserts. A duplicate-key loser rolls back its owned transaction and returns not-acquired. Errors
inside a supplied transaction propagate so its owner can roll it back. Concurrency tests hold the
insert stage open to verify that unrelated missing keys can reach it together and same-key races
return exactly one token.

External-outbox source commands operate on a producer-owned database, so they remain embedded SQL;
Acta installs no ledger routines there. PostgreSQL executes the claim batch in one implicit transaction;
SQL Server's multi-statement claim includes an ownership-aware transaction block; SQLite uses an
immediate transaction. Source reads use the query path. Schema bootstrap and object installation have
their separate migration transaction and lock.

`PurgeExpiredData` remains one routine identity, but each invocation purges one selected section's
bounded batch. `RetentionCoordinator` reads database time once, calculates fixed sweep cutoffs, and
visits jobs, events, settled alerts, undelivered alerts, poison-skip checkpoints, workers, and expired
locks in that order. Defaults remain 1,000 rows and 50 iterations per section. An empty batch ends its
section. Each batch is atomic; failure or cancellation later in the sweep preserves completed batches.
The coordinator never retries the whole sweep. Committed undelivered-alert deletions produce one
warning per sweep, including when a later batch fails or cancellation stops the sweep.

Every SQL Server statement that updates `runtimes` through a join carries `WITH (FORCESEEK)`. The
driving set is always a CTE or table variable, which the optimizer estimates at low cardinality;
without the hint it may source the update from a clustered scan of `runtimes`, take an update lock per
row, escalate to a table lock once the backlog passes the escalation threshold, and hold it to commit,
so every concurrent insert waits out the whole statement. `pk_runtimes` is keyed on `job_id` and every
such join predicate resolves through it, so a seek is always available and the hint cannot fail to
compile. The claim batch, the single claim, batch completion, and the stuck-job reclaim all take it.
`StartExecution` does not: it filters on a point predicate and always seeks.

Statements that touch many `runtimes` rows lock the base row first and never hold an index key while
waiting for one. `StartExecution` and `CompleteExecution` change `status_code` and
`leased_by_worker_id`, both key columns of `ix_runtimes_worker_inflight`, so they lock the clustered
row and then move the index key; a worker heartbeat that walked that index instead would hold the key
and wait for the row, which is the inversion that deadlocks. `ExtendWorkerLeases` therefore reads the
in-flight ids first and updates through a primary-key seek. It must not use `READPAST`: the caller
treats the returned set as authoritative and cancels any running attempt missing from it. On
PostgreSQL the same pair is ordered by locking `runtimes` rows in `job_id` order in both the heartbeat
and batch completion, and the buffered flush is sorted by job id before its ordinals are assigned.

Routines that lock both `checkpoints` and `runtimes` take the checkpoint slot first; `raise_signal`
and `complete_execution` both do. `arm_or_consume_sleep_timer` is the one exception, taking the
`runtimes` row as a job-level mutex before reading its slot, so a timer arming concurrently with a
signal raise on the same job acquires the two in opposite orders. No deadlock graph has ever shown
that pair, and it is not reachable while both hold row locks on different slots, so the mutex stays
until a run demonstrates otherwise; removing it would drop the serialization that two different timer
names on one job rely on.

## Provisioning, compatibility, and SQL access

Tables, columns, indexes, constraints, and durable types belong in numbered `MNNN` migrations.
Routine and view definitions are versionless objects: `SqlObjectInstaller` reapplies current bodies
after checking pending migrations, under the same migration lock, including when no migration is
pending. A body edit does **not inherently require a numbered migration**. Embedded query changes
require neither migration nor installed-object replacement.

A routine whose signature changes carries its own cleanup, because `CREATE OR REPLACE` neither
replaces across arities nor changes a return type. A retired arity is dropped after the create; a
changed return type must be dropped before it, and that leading drop names no argument list, because
the routine-body parameter gate reads the file's first parenthesis as the parameter list. The
routine layer therefore reinstalls onto a database from the previous release. Reprovisioning is
still required where a table definition moved, which for rc.2 is the SQL Server Unicode actor key.
From 1.0 onward, an installed routine remains a routine. An inline operation may be promoted
deliberately; routine-to-inline demotion is outside the 1.x policy.

During a rolling deployment all workers share the installed routine body. N+1 provisioning changes
the implementation that N workers invoke; N provisioning during rollback may reinstall N's body.
Changes must therefore preserve the supported parameter, result, and semantic contracts in both
deployment directions. Compatible table DDL alone is insufficient. Body replacement avoiding MNNN
does not remove this executable compatibility obligation.

Dashboard/API are the normal administration surface; human database access can be restricted without
making Acta opaque. Ordinary SELECT inspection and the documented direct SQL enqueue recipes remain
supported, including SSMS, DBeaver, and psql. SQL enqueue becomes visible to worker polling after commit;
it does not itself send an application transport wake-up. See [SQL recipes](../guide/sql-recipes.md).
This policy does not declare every internal routine an independently supported operator API or require
an EXECUTE-only runtime account. Installed views remain the curated query surface; storage tables and
custom payload encodings retain their existing compatibility and decoding limits.
