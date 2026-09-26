# Certification seals

A seal is the durable record of one chaos-certification run: the shape (jobs, slots, kill cadence,
processes), the commit it ran against, every asserted property, and the verdict. Seals are evidence,
not marketing — a seal that found a defect says so, and the re-run against the fix is a separate
seal. The current gate definitions live in [releasing.md](../internals/releasing.md). The ensemble
gate's workload carries a rate-limited shape (`metered`, declared `600/m`), so its seal also reports
the rate contract: how many handler starts the meter admitted, the busiest one-second and ten-second
windows, and the budget each was measured against.

Superseded seals are pruned: when a certification round is re-cut on a later commit at the same
shapes, the earlier round's seals leave the tree (git history keeps them). What remains is the
evidence chain for released tags plus the runs that proved something no later run repeats.

Not every page here is a seal. [coverage-baseline-rc1.md](./coverage-baseline-rc1.md) is the other
kind of evidence: line and branch coverage for the unit and SQLite suites, recorded with no gate and
no target, and the blind-spot list that reading it produced. A seal says what held under chaos; that
page says what nothing has ever run. [burst-rc1.md](./burst-rc1.md) is a third kind: the alert
burst certification — five runs proving a 10,000-event backlog projects in one invocation and
drains in seconds, and a 100,000 backlog drains under bounded memory.

The 2026-09-26 quartet is the `1.0.0` round: all four gates on the certified commit `984eb036`,
the first round run from a Native AOT build of the harness, and the first on the tree whose start
and completion writes repeat and reconcile against the row, whose heartbeat returns a claim with a
lost answer to Ready, and whose executor and orphan release take a per-execution owner entry
before touching a row, and whose rate meter reads its clock after the bucket row's lock. The rc.3
round's seals and the 2026-08-12 million-job seals left the tree when this round replaced them,
as the policy above describes: the round carries its own million-job runs on PostgreSQL and SQL
Server, 1,005,000 jobs each through 1,536 slots with a worker killed every five seconds.

## Index

| Seal | Shape | Released in |
| --- | --- | --- |
| [seal-20260926T111524Z](./seal-20260926T111524Z.md) | Ensemble: 3 participants, 2 namespaces, one run id, `metered` shape | `v1.0.0` (certified commit `984eb036`) |
| [seal-20260926T110350Z](./seal-20260926T110350Z.md) | SQLite reduced, one WAL file, 48 slots, `metered` shape | `v1.0.0` (certified commit `984eb036`) |
| [seal-20260926T104657Z](./seal-20260926T104657Z.md) | SQL Server standard, 10,000 jobs, 64 slots, `metered` shape | `v1.0.0` (certified commit `984eb036`) |
| [seal-20260926T103342Z](./seal-20260926T103342Z.md) | PostgreSQL standard, 10,000 jobs, 64 slots, `metered` shape | `v1.0.0` (certified commit `984eb036`) |
| [seal-20260926T145156Z](./seal-20260926T145156Z.md) | 1,000,000 jobs, PostgreSQL, 1,536 slots, `metered` shape | `v1.0.0` (certified commit `984eb036`) |
| [seal-20260926T133553Z](./seal-20260926T133553Z.md) | 1,000,000 jobs, SQL Server, 1,536 slots, `metered` shape | `v1.0.0` (certified commit `984eb036`) |

What this round shows: the quartet holding on the tree that closed the ownership holes four
reviews found between claim, start, heartbeat, and release, with orphaned attempts in the hundreds
and dead workers in the dozens on every gate; the ensemble's at-most-once and namespace-isolation
checks holding with 771 orphaned attempts across 185 killed workers; and the rate meter admitting
at its declared rate on every provider while the workers holding its turns were killed, never past
twenty in a second against a budget of thirty.

[coverage-baseline-rc2.md](./coverage-baseline-rc2.md) is the current coverage page: the unit and
SQLite suites at 88.4% line and 70.7% branch, and the blind-spot entry the rc.2 round's own defect
landed in.
