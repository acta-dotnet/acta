# Coverage baseline — v1.0.0-rc.4

**Captured** 2026-10-02 · **Commit** `d616c72b` · **Command** `tools/coverage.ps1`

What the same command reports for the `1.0.0-rc.4` tree, beside the rc.2 page it replaces as current. Same
two suites, the unit suite and the SQLite conformance suite, and still no threshold: a coverage target
invites tests written to colour lines rather than to falsify behaviour.

## Baseline

| | rc.2 | 1.0.0-rc.4 |
|---|---|---|
| **Line** | 32,510 / 36,773 · 88.4% | 35,534 / 39,757 · **89.3%** |
| **Branch** | 10,213 / 14,428 · 70.7% | 11,525 / 15,754 · **73.1%** |
| **Method** | 2,515 / 3,059 · 82.2% | 2,687 / 3,207 · **83.7%** |
| **Fully covered** | 67.0% | **69.2%** |

10 assemblies, 464 classes (450 at rc.2). The base grew by 2,984 coverable lines and 1,326 branches,
most of them lanes, the claim changes, and the ownership work, and the rates rose with it.

| Assembly | Line rc.2 | Line rc.4 | Branch rc.2 | Branch rc.4 |
|---|---:|---:|---:|---:|
| Acta | 71.3% | 72.4% | 55.8% | 58.2% |
| Acta.AspNetCore | 97.5% | 97.6% | 79.4% | 79.6% |
| Acta.Generators | 86.7% | 85.7% | 67.8% | 67.4% |
| Acta.Postgres | 5.7% | 13.7% | 0.0% | 1.3% |
| Acta.Redis | 60.2% | 60.1% | 32.1% | 32.1% |
| Acta.Relational | 98.0% | 98.0% | 84.6% | 84.9% |
| Acta.Runtime | 91.3% | 92.8% | 82.7% | 85.1% |
| Acta.Sqlite | 93.4% | 93.0% | 82.5% | 83.8% |
| Acta.SqlServer | 21.7% | 21.5% | 19.5% | 18.3% |
| Acta.Testing | 80.3% | 82.2% | 58.7% | 61.8% |

`Acta.Runtime` gained two points of branch, where the worker loop, the executor, and the recovery
paths took most of this release's changes and their specs. The provider assemblies stay misleading
for the reason the rc.1 page gives: `Acta.Postgres` and `Acta.SqlServer` are mostly SQL executed by
the conformance suites this report does not instrument. `Acta.Postgres` rose only because a unit test
now runs the outbox DDL builder, which refuses table names whose derived names PostgreSQL would truncate.

## Blind spots

The rc.2 page named the single highest-value item as a test that executes `RecoveryJob.Handle` end
to end, and it has one: `RecoveryJob` reads **100%**, up from 0 of 15 lines. `WorkerRuntimeHost`, 0
of 51 lines at rc.2, reads **83.3%**. `RecoverySlotMonitor` reads 78.8%, the rest still the hourly
loop no test waits for. `ScheduleWalker` reads 100% and `SchedulesApi` 97.9%, the paths this release's
recurring-pause fix changed. The rc.1 list otherwise stands.

## Reproducing

`tools/coverage.ps1`, then read `artifacts/coverage/Summary.txt`. CI runs the same script in the
`build-test` job and uploads the merged report as the `coverage` artifact.
