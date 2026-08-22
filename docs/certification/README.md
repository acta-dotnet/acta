# Certification seals

A seal is the durable record of one chaos-certification run: the shape (jobs, slots, kill cadence,
processes), the commit it ran against, every asserted property, and the verdict. Seals are evidence,
not marketing — a seal that found a defect says so, and the re-run against the fix is a separate
seal. The current gate definitions live in [releasing.md](../internals/releasing.md).

Superseded seals are pruned: when a certification round is re-cut on a later commit at the same
shapes, the earlier round's seals leave the tree (git history keeps them). What remains is the
evidence chain for released tags plus the runs that proved something no later run repeats.

## Index

| Seal | Shape | Released in |
| --- | --- | --- |
| [seal-20260816T090216Z](./seal-20260816T090216Z.md) | Ensemble: 3 participants, 2 namespaces, one run id | `v0.9.0-beta.1` (release commit) |
| [seal-20260816T085003Z](./seal-20260816T085003Z.md) | SQLite standard, one WAL file, 48 slots | `v0.9.0-beta.1` (release commit) |
| [seal-20260816T083231Z](./seal-20260816T083231Z.md) | SQL Server standard, 10,000 jobs, 64 slots | `v0.9.0-beta.1` (release commit) |
| [seal-20260816T081858Z](./seal-20260816T081858Z.md) | PostgreSQL standard, 10,000 jobs, 64 slots | `v0.9.0-beta.1` (release commit) |
| [seal-20260812T182418Z](./seal-20260812T182418Z.md) | First SQL Server ensemble: 2 processes, one queue | `v0.9.0-beta.1` (pre-release commit) |
| [seal-20260812T162619Z](./seal-20260812T162619Z.md) | First ensemble: 2 processes, one run id, PostgreSQL | `v0.9.0-beta.1` (pre-release commit) |
| [seal-20260812T130035Z](./seal-20260812T130035Z.md) | 1,000,000 jobs, SQL Server | `v0.9.0-beta.1` (pre-release commit) |
| [seal-20260812T101351Z](./seal-20260812T101351Z.md) | 1,000,000 jobs, PostgreSQL | `v0.9.0-beta.1` (pre-release commit) |

The `v1.0.0-rc.1` round (four gates on the near-final commit) is added here when it runs.

The 2026-08-16 quartet is the `v0.9.0-beta.1` release evidence: all four gates on the release
commit, the SQLite gate having caught a real one-in-ten-thousand defect on its first run and passed
on the re-run against the fix. The 2026-08-12 seals are kept for what only they show: the
million-job scale runs and the first ensemble shapes, certified on commits that shipped in
`v0.9.0-beta.1`.
