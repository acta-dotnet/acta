# Troubleshooting

Use this page when a job, worker, dashboard, or local sample is not behaving as expected. The
operator guide has fuller SQL examples; this page points to the first checks.

For consequence-first answers: worker crash, database outage, early signal, removed handler,
schedule downtime, purge, or restore: see [What happens if…](./failure-modes.md).

## Start With `jobs explain`

For any single stuck, waiting, or failed job, run `<app> jobs explain <job-ref|id>` first. It reads
the same `runtimes` / lease / worker / checkpoint / step / event rows the checks below describe, in
one consistent snapshot, and states in plain English where the work is, why it moved there, and the
next action (raise a signal, wait for `sys.recovery`, resume, restart, or inspect the timeline). The
manual checks below are for triaging many jobs at once, confirming what `explain` reports, or when
you only have SQL access. The dashboard's per-job Explain panel renders the same account.

`jobs explain` diagnoses Acta's durable state. It can tell you which durable slots completed, which
wait is active, what the latest recorded reason was, and what operator actions are available. It
cannot predict arbitrary user code, external system state, or whether a non-idempotent side effect
already happened outside Acta.

Common outputs:

```text
Suspended, waiting for signal "fraud-review".

Next actions:
- Raise signal "fraud-review".
- Cancel the job.
```

```text
Executing, but its lease expired 2m ago.

Worker:
- Worker payments-v17 (17), lease expired at 2026-07-04T11:58:00.0000000Z.
- Last heartbeat at 2026-07-04T11:56:00.0000000Z.
- Recovery should return it to Ready on the next maintenance tick.
```

```text
Failed.

Reason:
- Report store unavailable.

Next actions:
- Inspect the timeline with 'jobs events'.
- Restart the job only after the underlying cause is fixed.
```

```text
Succeeded.

Durable work:
- Step "reserve-stock" succeeded and will not rerun.
```

## A Job Did Not Start

Start with `acta.jobs_view` so names and status values are decoded:

- `status`: only `ready` jobs are claimable.
- `next_run_at_utc`: delayed, slept, rescheduled, and recurring jobs may be ready but not due yet.
- `namespace`: workers only claim inside their own namespace.
- `job_name`: the worker must have registered the matching definition in that namespace.

If you need raw storage state, `jobs_view` is built from the job's `runtimes` row (1:1 with `jobs` by `job_id`).

Then check workers:

- `acta.workers_view.status`
- `acta.workers_view.last_seen_at_utc`
- the worker process logs
- whether the process used `Run<TManifest>(...)` instead of only `Reference<TManifest>(...)`

For query shapes, see [`operator-guide.md`](./operator-guide.md#common-queries).

## A Job Is Stuck In Dispatched Or Executing

`dispatched` and `executing` are lease-owned states. Check `acta.jobs_view.leased_by_worker_id`
and `lease_expires_at_utc`, then check `acta.workers_view.last_seen_at_utc`.

If the worker is gone, system maintenance reclaims expired leases through `sys.recovery` and moves
the job back to `Ready` while retry budget remains. If the worker is alive, inspect handler logs and
the job's event timeline.

For the live crash/reclaim path, run [`../anvil/Anvil/`](../../anvil/Anvil/).

## A Job Is Suspended

`Suspended` usually means the handler is waiting on `ctx.WaitSignalAsync(...)`. Check
pending signal `checkpoints` slots (kind `signal`) and raise the expected signal through `IJobs.RaiseSignalAsync`, the
CLI, or the HTTP signal endpoint if controls are enabled.

Sleep and reschedule waits are timer-based. Check timer `checkpoints` (kind `timer`) and `next_run_at_utc` for the due
instant.

## A Job Is Paused

Pause is sticky. A paused job does not auto-resume unless the paused object is a schedule pause with
an `until` timestamp. Resume a paused job with `IJobs.ResumeAsync`, the CLI, or an enabled dashboard
control endpoint.

Schedules have their own control surface through `operations.Schedules`; pausing a schedule is distinct
from pausing the job itself.

## A Job Keeps Retrying Or Failed

Read the event timeline. Job status tells you where the row landed; `events` explains why it
moved. For step-specific failures, inspect `steps` as well.

Common causes:

- the handler threw and retry budget remains
- `MaxAttempts` was exhausted
- `ExecutionTimeout` cancelled the attempt
- a handler called `ctx.FailAsync(...)`
- a worker lost its lease and maintenance reclaimed the attempt
- an external cancel cascaded from a parent

The code tables in [`code-families.md`](../reference/code-families.md) define status, event, reason, and
execution outcome codes.

## A Signal Did Not Release A Job

Check the signal name and target identity:

- Signal names are kebab-case and scoped to one job.
- A terminal job rejects new signals.
- A paused job records the signal but stays paused.
- HTTP signal endpoints require controls to be enabled and the same `X-Acta-Control` confirmation
  header as other control requests. The header is an anti-accident guard, not authentication.

The handler resumes when it is claimed again, so also check worker liveness and `next_run_at_utc`.

## An Outbox Row Is Not Reaching Acta

If a producer staged an `acta_outbox` row and no Acta job appeared, check the relay before the row:

- A worker must register the source with `worker.AddOutboxRelay(...)`; without it, `sys.outbox` never
  runs and no row is claimed.
- `sys.outbox` runs on a five-second cadence and sends no cross-database wakeup, so a few seconds of delay
  is normal. After a worker crash a claimed row can stay invisible until its lease (`LeaseTtlSeconds`,
  180 s default) expires and another relay reclaims it.
- Connectivity and table-shape problems fail inside `sys.outbox` and raise a `SysCritical` alert rather
  than blocking unrelated jobs. Check `acta.alerts_view` and the `sys.outbox` event timeline.
- A row the relay could not deliver is quarantined in place (`status_code = 90`) and excluded from claims.
  Inspect, requeue, or delete it with the
  [quarantine SQL recipes](./sql-recipes.md#quarantined-outbox-rows). Recoverable rejections (for example
  an unknown route) quarantine after the failure threshold; malformed or oversize rows quarantine at once.
- Resolve the resulting job by `(namespace, deduplication key)` with `IJobs.ResolveJobIdAsync`; a null
  result means the request has not been relayed yet, not that it was lost.

Background: [Transactional enqueue and the external outbox](./transactional-enqueue-and-outbox.md).

## An Enqueue Throws About An Ambient TransactionScope

An Acta-owned call (a normal enqueue, a maintenance write, any owned path) opening a connection inside an
active `System.Transactions.TransactionScope` fails fast:

```text
An ambient System.Transactions.TransactionScope is active, and Acta-owned connections never enlist in one.
```

Acta-owned connections never enlist in an ambient transaction, because a second connection in the scope
would force distributed-transaction escalation the providers cannot honor. Rewrite to one of the two atomic
paths: pass the open transaction to the transactional `IJobs` enqueue overload (same database), or stage
through `AddToActaOutboxAsync` on your provider transaction (different database). If you deliberately want
an independent Acta commit inside a scope, wrap the call in `TransactionScope(TransactionScopeOption.Suppress)`.
The caller-transaction overloads and the staging primitives are unaffected: their transaction is supplied
explicitly. See [Transactional enqueue and the external outbox](./transactional-enqueue-and-outbox.md#ambient-systemtransactions-scopes-are-rejected).

## The Dashboard Or API Is Not Reachable Remotely

`MapActa(...)` and `MapActaApi(...)` are local-only by default. Remote clients receive 403 until the
host sets `LocalOnly = false` and adds ASP.NET Core authorization through `ConfigureEndpoints`.

Mutating controls are also disabled by default. Enable them explicitly with `EnableControls = true`
and keep authorization on the mapped endpoint group.

## The CLI Did Not Run A Worker

When an Acta host starts with `jobs` as the first argument, it runs the built-in CLI verb and exits.
No worker loop starts. This is intentional.

Use `j.DisableCli()` only for applications that own their own command-line surface. The CLI expects
an already-configured provider and schema; the normal worker owns migration and catalog setup.

## Local Environment Setup Fails

Run `dotnet run --project tools/Acta.Doctor` first: it checks the SDK, the SQLite path, env vars,
Docker, and ports, and prints the fix for most of the rows below.

| Symptom | Cause | Fix |
|---|---|---|
| A concept/Anvil demands `ACTA_TEST_PG` though you wanted SQLite | Stale `ACTA_LOCAL_PROVIDER=postgres` in your shell or a copied `.env` | Unset `ACTA_LOCAL_PROVIDER` (SQLite is the default when it is unset) |
| `docker compose up` fails: cannot connect to the daemon | Docker Desktop / dockerd not running | Start it, or skip Docker entirely; the SQLite path needs none |
| Compose fails: port 5432/1433/6379 already allocated | You already run a server on that port | Reuse yours via `ACTA_TEST_PG` / `ACTA_TEST_MSSQL` / `ACTA_TEST_REDIS`, or set `ACTA_PG_PORT` / `ACTA_MSSQL_PORT` / `ACTA_REDIS_PORT` in `.env` |
| SQL Server container unhealthy or login fails right after start | ~20–30 s startup; password must satisfy complexity policy; memory capped at `MSSQL_MEMORY_LIMIT_MB: 4096` | Wait for `healthy` in `docker compose ps`; keep any `ACTA_MSSQL_SA_PASSWORD` override complex |
| Postgres `28P01: password authentication failed` | Connection string password differs from the one the volume was initialized with | Match `.env.example`, or re-init: `docker compose down -v` then `up -d` (destroys the container's data) |
| Redis unavailable | Container/service not running | Optional: rung 903 degrades to the poll floor without it. Redis is a latency accelerator, not a correctness dependency |
| Anvil exits at start: port 5059 taken | Another Anvil (or app) is listening on 5059 | Close it, then rerun `dotnet run --project anvil/Anvil` |
| Reset local SQLite state | One temp-file database per machine | Delete `acta-local*.db` in `%TEMP%` (Windows) / `$TMPDIR` or `/tmp` (macOS/Linux) |

## A Concept Or Demo Cannot Connect Locally

Most concepts, demos, and Anvil use the shared `UseLocalDatabase(...)` helper. Provider selection is:

1. explicit provider argument
2. `Acta:Provider`
3. `ACTA_LOCAL_PROVIDER`
4. SQLite as the zero-setup embedded default

SQLite writes a temp-file database and needs no server. To run against Postgres or SQL Server, set
`ACTA_LOCAL_PROVIDER` and provide `ConnectionStrings:acta`, `ACTA_TEST_PG`, or `ACTA_TEST_MSSQL`.
The first concept rung (`001-hello-acta`) intentionally spells the SQLite setup out in full so the
provider wiring is visible once; it needs no server either.

## Generated Docs Or Migrations Drifted

Do not edit generated files by hand. Change source and regenerate:

```bash
dotnet run --project tools/Acta.Emit -- check
dotnet run --project tools/Acta.Emit -- docs          # if docs 97/98 drifted
dotnet run --project tools/Acta.Emit -- schema add    # if the model changed without a migration
```

`docs/reference/conformance-contracts.md` is emitted from conformance attributes:

```bash
ACTA_EMIT_DOCS=1 dotnet test tests/Acta.Tests/Acta.Tests.csproj --filter DocsContractTests
```
