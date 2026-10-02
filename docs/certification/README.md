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

The 2026-10-02 round is the `1.0.0-rc.4` round, all on the certified commit `d616c72b`: the three
standard gates and the ensemble side by side, then the two million-job runs one after the other, under
the release protocol in [releasing.md](../internals/releasing.md). It is the first round that
certifies lanes under chaos, the first with three meters, the first that counts the attempts kills
interrupted in each shape (`chaos-by-shape`), and the first whose million-job runs use five steps of
250 ms and a twelve-minute chaos window. Every gate runs its workers on the Direct profile; Buffered
and Bulk are not certified under chaos. The machine reset after the round and its SQL Server database
did not survive crash recovery, so the two SQL Server seals carry their verdicts and every check but
no ledger counts. The rc.3 round's seals and the 2026-08-12 million-job seals left the tree when this
round replaced them, as the policy above describes.

## Index

| Seal | Shape | Released in |
| --- | --- | --- |
| [seal-20261002T142720Z-pg](./seal-20261002T142720Z-pg.md) | PostgreSQL standard, 10,000 jobs, 64 slots | `v1.0.0-rc.4` (certified commit `d616c72b`) |
| [seal-20261002T142720Z-mssql](./seal-20261002T142720Z-mssql.md) | SQL Server standard, 10,000 jobs, 64 slots | `v1.0.0-rc.4` (certified commit `d616c72b`) |
| [seal-20261002T143121Z-sqlite](./seal-20261002T143121Z-sqlite.md) | SQLite reduced, one WAL file, 48 slots | `v1.0.0-rc.4` (certified commit `d616c72b`) |
| [seal-20261002T142719Z-ensemble](./seal-20261002T142719Z-ensemble.md) | Ensemble: 3 participants, 2 namespaces, one run id | `v1.0.0-rc.4` (certified commit `d616c72b`) |
| [seal-20261002T145033Z-million-pg](./seal-20261002T145033Z-million-pg.md) | 1,000,000 jobs, PostgreSQL, 1,536 slots | `v1.0.0-rc.4` (certified commit `d616c72b`) |
| [seal-20261002T154223Z-million-mssql](./seal-20261002T154223Z-million-mssql.md) | 1,000,000 jobs, SQL Server, 1,536 slots | `v1.0.0-rc.4` (certified commit `d616c72b`) |

What this round shows: every gate holding with orphaned attempts in the hundreds and dead workers in
the dozens, and both million-job runs bringing every one of 1,005,000 jobs to its expected end with no
step recorded twice and no charge body run past its guard. Kills now land inside the AtMostOnce
charges, and a charge interrupted inside its guarded step ends Failed with `job.step-interrupted`
rather than running again: 47 on PostgreSQL, 60 on SQLite, 37 in the ensemble, 385 in the PostgreSQL
million-job run, each one counted by `expected-outcome`. Lanes held their order and drained on every
provider while the workers running their heads were killed: 63 laned attempts interrupted and re-run
in place on PostgreSQL, 58 on SQL Server, 60 on SQLite, 31 in the ensemble, 111 and 181 in the two
million-job runs. The three meters stayed inside their budgets everywhere, the busiest million-job
window at 1,075 starts in ten seconds against 1,200 on the 100/s meter.

[coverage-baseline-rc4.md](./coverage-baseline-rc4.md) is the current coverage page: the unit and
SQLite suites at 89.3% line and 73.1% branch, and the two rc.2 blind spots, `RecoveryJob` and
`WorkerRuntimeHost`, now executed by tests.
