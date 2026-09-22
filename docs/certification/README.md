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

The 2026-09-21 quartet is the `1.0.0-rc.3` round: all four gates on the certified commit
`d3def975`, the first round whose workload carries the `metered` shape, so every seal reports the
rate contract beside the chaos figures. Every seal from the rc.2, rc.1, and 0.9.0-beta.1 rounds left
the tree when this round replaced them, as the policy above describes; the two 2026-08-12 seals stay
for what only they show, the million-job scale runs on PostgreSQL and SQL Server.

## Index

| Seal | Shape | Released in |
| --- | --- | --- |
| [seal-20260922T002305Z](./seal-20260922T002305Z.md) | Ensemble: 3 participants, 2 namespaces, one run id, `metered` shape | `v1.0.0-rc.3` (certified commit `d3def975`) |
| [seal-20260922T001241Z](./seal-20260922T001241Z.md) | SQLite reduced, one WAL file, 48 slots, `metered` shape | `v1.0.0-rc.3` (certified commit `d3def975`) |
| [seal-20260921T235527Z](./seal-20260921T235527Z.md) | SQL Server standard, 10,000 jobs, 64 slots, `metered` shape | `v1.0.0-rc.3` (certified commit `d3def975`) |
| [seal-20260921T234152Z](./seal-20260921T234152Z.md) | PostgreSQL standard, 10,000 jobs, 64 slots, `metered` shape | `v1.0.0-rc.3` (certified commit `d3def975`) |
| [seal-20260812T130035Z](./seal-20260812T130035Z.md) | 1,000,000 jobs, SQL Server | `v0.9.0-beta.1` (pre-release commit) |
| [seal-20260812T101351Z](./seal-20260812T101351Z.md) | 1,000,000 jobs, PostgreSQL | `v0.9.0-beta.1` (pre-release commit) |

What this round shows: the quartet holding on the tree that changed the claim, registration, and
admission paths, with orphaned attempts in the hundreds and dead workers in the dozens on every
gate; the ensemble's at-most-once and namespace-isolation checks holding with 772 orphaned attempts
across 162 killed workers; and the rate meter admitting at its declared rate on every provider while
the workers holding its turns were killed, never past twenty-one in a second against a budget of
thirty.

[coverage-baseline-rc2.md](./coverage-baseline-rc2.md) is the current coverage page: the unit and
SQLite suites at 88.4% line and 70.7% branch, and the blind-spot entry the rc.2 round's own defect
landed in.
