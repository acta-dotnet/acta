# Certification seals

A seal is the durable record of one chaos-certification run: the shape (jobs, slots, kill cadence,
processes), the commit it ran against, every asserted property, and the verdict. Seals are evidence,
not marketing — a seal that found a defect says so, and the re-run against the fix is a separate
seal. The current gate definitions live in [releasing.md](../internals/releasing.md). Every gate's
workload carries three rate meters side by side (10/s, 50/s, and 100/s), so each seal reports the rate
contract per meter: how many handler starts it admitted, the busiest one-second and ten-second windows,
and the budget each was measured against. Every gate also carries laned jobs, ten to a lane, and
reports how many laned attempts the kills interrupted beside the lane-order and lane-drained checks.

Superseded seals are pruned: when a certification round is re-cut on a later commit at the same
shapes, the earlier round's seals leave the tree (git history keeps them). What remains is the
evidence chain for released tags plus the runs that proved something no later run repeats.

Not every page here is a seal. [coverage-baseline-rc1.md](./coverage-baseline-rc1.md) is the other
kind of evidence: line and branch coverage for the unit and SQLite suites, recorded with no gate and
no target, and the blind-spot list that reading it produced. A seal says what held under chaos; that
page says what nothing has ever run. [burst-rc1.md](./burst-rc1.md) is a third kind: the alert
burst certification — five runs proving a 10,000-event backlog projects in one invocation and
drains in seconds, and a 100,000 backlog drains under bounded memory.

The 2026-10-12 round is the `1.0.0-rc.5` round, all on the certified commit `4f410f68`: the three
standard gates and the ensemble side by side, then the two million-job runs side by side, under the
release protocol in [releasing.md](../internals/releasing.md). It certifies the rc.5 engine (one
claimer, the executors, one finalizer writing batched completions through the single
`complete_executions` routine) under the same shapes as the rc.4 round, and its SQLite gate is the first
in which Direct syncs every commit. Every gate runs its workers on the Direct profile; Buffered is not
certified under chaos. The rc.4 round's seals left the tree when this round replaced them, as the
policy above describes.

## Index

| Seal | Shape | Released in |
| --- | --- | --- |
| [seal-20261012T090718Z-pg](./seal-20261012T090718Z-pg.md) | PostgreSQL standard, 10,000 jobs, 64 slots | `v1.0.0-rc.5` (certified commit `4f410f68`) |
| [seal-20261012T090719Z-mssql](./seal-20261012T090719Z-mssql.md) | SQL Server standard, 10,000 jobs, 64 slots | `v1.0.0-rc.5` (certified commit `4f410f68`) |
| [seal-20261012T091118Z-sqlite](./seal-20261012T091118Z-sqlite.md) | SQLite reduced, one WAL file, 48 slots | `v1.0.0-rc.5` (certified commit `4f410f68`) |
| [seal-20261012T090718Z-ensemble](./seal-20261012T090718Z-ensemble.md) | Ensemble: 3 participants, 2 namespaces, one run id | `v1.0.0-rc.5` (certified commit `4f410f68`) |
| [seal-20261012T093200Z-million-pg](./seal-20261012T093200Z-million-pg.md) | 1,000,000 jobs, PostgreSQL, 1,536 slots | `v1.0.0-rc.5` (certified commit `4f410f68`) |
| [seal-20261012T095154Z-million-mssql](./seal-20261012T095154Z-million-mssql.md) | 1,000,000 jobs, SQL Server, 1,536 slots | `v1.0.0-rc.5` (certified commit `4f410f68`) |

What this round shows: every gate holding with orphaned attempts in the hundreds (576 to 713) and dead
workers in the dozens, and both million-job runs, with 8,098 and 8,476 orphaned attempts, bringing every
one of 1,005,000 jobs to its expected end with no step recorded twice and no charge body run past its
guard. A charge interrupted inside its guarded step ends Failed with `job.step-interrupted` rather than
running again: 33 on PostgreSQL, 38 on SQL Server, 61 on SQLite, 27 in the ensemble, 265 and 391 in the
two million-job runs, each one counted by `expected-outcome`. Lanes held their order and drained on
every provider while the workers running their heads were killed: 52 laned attempts interrupted and
re-run in place on PostgreSQL, 49 on SQL Server, 69 on SQLite, 34 in the ensemble, 97 and 292 in the
two million-job runs. The three meters stayed inside their budgets everywhere, the busiest million-job
window at 1,042 starts in ten seconds against 1,200 on the 100/s meter.

[coverage-baseline-rc4.md](./coverage-baseline-rc4.md) is the current coverage page: the unit and
SQLite suites at 89.3% line and 73.1% branch, and the two rc.2 blind spots, `RecoveryJob` and
`WorkerRuntimeHost`, now executed by tests.
