# Coverage baseline and blind-spot list — v1.0.0-rc.2

**Captured** 2026-09-10 · **Commit** `22d74be6` · **Command** `tools/coverage.ps1`

The rc.1 page holds the blind-spot analysis; this page records what the same command reports for the
rc.2 tree and which entries moved. Same two suites, same absence of a threshold, same reason: a
coverage target invites tests written to colour lines rather than to falsify behaviour.

## Baseline

| | rc.1 | rc.2 | |
|---|---|---|---|
| **Line** | 32,003 / 36,261 · 88.2% | 32,510 / 36,773 · **88.4%** | |
| **Branch** | 10,002 / 14,216 · 70.3% | 10,213 / 14,428 · **70.7%** | |
| **Method** | 2,491 / 3,035 · 82.0% | 2,515 / 3,059 · **82.2%** | |
| **Fully covered** | 66.9% | **67.0%** | |

10 assemblies, 450 classes (448 at rc.1). The base grew by 512 coverable lines and 212 branches with
this round's code and tests, so the percentages moved against a moving denominator.

| Assembly | Line rc.1 | Line rc.2 | Branch rc.1 | Branch rc.2 |
|---|---:|---:|---:|---:|
| Acta | 71.2% | 71.3% | 55.7% | 55.8% |
| Acta.AspNetCore | 97.8% | 97.5% | 79.4% | 79.4% |
| Acta.Generators | 86.7% | 86.7% | 67.8% | 67.8% |
| Acta.Postgres | 5.7% | 5.7% | 0.0% | 0.0% |
| Acta.Redis | 60.2% | 60.2% | 32.1% | 32.1% |
| Acta.Relational | 97.8% | 98.0% | 84.4% | 84.6% |
| Acta.Runtime | 91.2% | 91.3% | 82.3% | 82.7% |
| Acta.Sqlite | 93.3% | 93.4% | 80.8% | 82.5% |
| Acta.SqlServer | 21.6% | 21.7% | 19.5% | 19.5% |
| Acta.Testing | 78.7% | 80.3% | 57.1% | 58.7% |

Three assemblies moved by more than a rounding step. `Acta.Sqlite` gained 1.7 points of branch on
the completion routine's new predicates, which the schedule-change specs drive from SQLite.
`Acta.Testing` rose on the raw-connection helpers the new specs use. `Acta.AspNetCore` fell three
tenths of a point on line, which is the `expectedVersion` work adding conflict arms to the control
endpoints faster than the endpoint tests covered them; the branch rate is unchanged, so the added
arms are exercised, not skipped.

The two provider assemblies stay where they were, and stay misleading for the reason the rc.1 page
gives: `Acta.Postgres` and `Acta.SqlServer` are mostly SQL resources executed by the conformance
suites this report does not instrument.

## Blind spots

The rc.1 list stands. One entry deserves this round's attention.

**Entry 2, worker death — still open, and it cost something.** `RecoveryJob.cs` remains **0 of 15
lines, 0 of 12 branches**: no test executes the `sys.recovery` composition. The rc.1 page states the
risk as "a change to either is caught by no test on this leg." That is what happened. The reclaim
sweep had exactly one trigger, this slot, and a slot is an ordinary job a worker can die holding, so
a crash that stranded it stopped the sweep that would have freed it. No test found that. This
release's certification round did, on SQL Server, with 571 jobs queued behind a slot whose lease had
expired 36 minutes earlier.

The fix does not move the sweep: every worker now watches the one slot the sweep depends on, from
`RecoverySlotMonitor`, and re-arms it under a guard when its lease has lapsed. The guarded repair and
the startup check are executed by a conformance spec, which is why the monitor reads **22 of 38
lines**; the sixteen uncovered lines are the hourly loop itself, which no test waits an hour for.
`WorkerRuntimeInitializer`, which runs the startup check, sits at **216 of 236 lines**.

The composition is not covered, and it is load-bearing: the monitor repairs only the slot and leaves
every sweep, including the `FailedChildren` latch loop and the stale-latch backstop, to this handler,
which is inside those 15 uncovered lines. A test that executes `RecoveryJob.Handle` end to end is
the single highest-value coverage item on this list, and it did not exist for rc.2.

`WorkerRuntimeHost.cs` is unchanged at **0 of 51 lines**, so the second half of that entry stands
exactly as written.

## Reproducing

`tools/coverage.ps1`, then read `artifacts/coverage/Summary.txt`. CI runs the same script in the
`build-test` job and uploads the merged report as the `coverage` artifact.
