<!-- engineering-lab
lab: can-sql-replace-a-job-queue
also-labs: can-recovery-work-without-a-leader
views: jobs_view, workers_view, events_view
alternatives: broker, leader-coordinator, sql-coordination, in-process-queue
-->

# Engineering Lab: SQL-backed crash recovery without a leader

## The problem

Replacing a broker means the database must handle claim races, ownership, leases, dead workers, and
replay after the process disappears at the worst moment.

## Common approaches

- Use a broker for delivery and keep execution state elsewhere.
- Elect a coordinator or run a dedicated recovery control plane.
- Let peer workers coordinate through one relational authority.
- Use an in-process queue and accept loss on restart.

## Why this design

Acta's claim, worker heartbeat, lease, and recovery transitions are SQL operations. `sys.recovery` is a
normal competitively claimed recurring job, so no worker is a permanent leader. The lab kills worker A
mid-attempt and lets worker B recover the same durable identity.

## Trade-offs

Claim and completion traffic consume database capacity and compete with application traffic. Lease
recovery is at-least-once, so handler work can repeat. This design targets application jobs, not
high-throughput event streaming.

## Run the experiment

This is intentionally a real distributed-provider lab. Set `ACTA_LOCAL_PROVIDER=postgres` or
`sqlserver` plus `ACTA_TEST_PG`/`ACTA_TEST_MSSQL`, then use separate terminals:

```bash
dotnet run --project concepts/700-topology-and-deployment/705-worker-crash-recovery -- worker-a
dotnet run --project concepts/700-topology-and-deployment/705-worker-crash-recovery -- enqueue
dotnet run --project concepts/700-topology-and-deployment/705-worker-crash-recovery -- inspect
dotnet run --project concepts/700-topology-and-deployment/705-worker-crash-recovery -- worker-b
```

Worker A intentionally exits non-zero. Worker B uses short lab-only lease timings and triggers the real
recovery schedule after expiry. Starting worker A creates a new local session token; `enqueue`,
`inspect`, and worker B reuse it. Worker A crashes only the probe carrying that token, so an unfinished
probe from an aborted older session cannot terminate the new experiment. Each `enqueue` also updates a
local current-job marker, making the sequence repeatable against the same shared database.

## Rows to inspect

Run with `--all-columns` to execute the visible `SELECT *` Explore query first. The default Notice
queries select the fields that prove the lesson, and the text below explains their meaning.

`jobs_view` moves from executing under A to done under B with a higher execution number.
`workers_view` is filtered to the current session's deployment versions and preserves its
heartbeat/liveness evidence, while `events_view` records
`job.lease-expired`. Application code should use `IJobs`; the views are curated operator surfaces.

## Break it

Start two worker-B processes before recovery and observe that only one claims the recovered row. Move a
non-idempotent side effect before worker A's crash and observe why recovery cannot promise exactly once.

## When not to use

Use a broker or log when event throughput, fan-out, replayable streams, or independent consumer offsets
are the primary problem. Use a dedicated workflow/control plane when recovery authority must be separate
from application workers.

## Source trail

- [The SQL-queue Engineering Lab](../../../docs/engineering-labs.md)
- [The leaderless-recovery Engineering Lab](../../../docs/engineering-labs.md)
- [`worker-crash-recovery.cs`](./worker-crash-recovery.cs)
- [`RecoveryJob.cs`](../../../src/Acta.Runtime/Features/Execution/RecoveryJob.cs)
- [`WorkerCrashRecoveryChaosSpec.cs`](../../../tests/Acta.Tests.Conformance/Runtime/WorkerCrashRecoveryChaosSpec.cs)
