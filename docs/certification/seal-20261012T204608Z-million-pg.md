# Acta certification seal

**Verdict: PASS** — the `1.0.0-rc.5` million-job run on PostgreSQL, beside the SQL Server one. 1,000,000 jobs across 1,536 slots, a real kill every five seconds for twelve minutes, then the ledger judged by `certify.sql`. The harness ran as a Native AOT executable.

## Run

| | |
|---|---|
| Captured | 2026-10-12T20:46:08Z |
| Commit | `3a170331` |
| Provider | PostgreSQL 18.6 (Debian 18.6-1.pgdg13+2, x86_64) |
| Schema | `certr5cqpg` |
| Run id | `r20261012-202734-1c46d0` |
| Workload | Anvil `CrashRecovery`, **1,000,000 jobs** plus **5,000 outbox rows** staged under chaos; 5 steps of 250 ms per slow job; 100,000 AtMostOnce charges, 20,000 laned (2,000 lanes of ten), and 10,000 metered (2,000 at 10/s, 4,000 at 50/s, 4,000 at 100/s) |
| Workers | 24 processes × 64 executors = **1,536 concurrent slots**; connection pool capped at 16 per process |
| Chaos | Real `Process.Kill` every 5s for a 12-minute window |
| Quiesce | Chaos stopped, then waited until every job this run seeded (by correlation key) reached a terminal status: 6 minutes here; 19 minutes from seed to verdict |

## Result

| Check | Result |
|---|---|
| `outbox-drained` | pending+claimed=0 quarantined=0 |
| `outbox-delivered` | staged=5000 ledger_jobs=5000 |
| `exec-event-ownership` | 0 |
| `attempt-pairing` | 0 |
| `namespace-isolation` | 0 |
| `no-stranded-work` | 0 |
| `expected-outcome` | 0 |
| `terminal-integrity` | 0 |
| `step-replay` | 0 |
| `chaos-was-real` | orphaned_attempts=8087 workers_marked_dead=126 |
| `clock-backsteps` *(note)* | backwards_writes=2317499 |
| `at-most-once` | 0 |
| `outbox-relayed-outcome` | 0 |
| `outbox-relayed` *(note)* | relayed_jobs=5000 |
| `tenant-context` | 0 |
| `lane-order` | 0 |
| `lane-drained` | 0 |
| `chaos-by-shape` *(note)* | charges_orphaned=274 metered_orphaned=283 laned_orphaned=85 |
| `rate-contract` 10/s *(note)* | admitted=2010 max_1s=20 budget_1s=30 max_10s=108 budget_10s=120 |
| `rate-contract` 50/s *(note)* | admitted=4008 max_1s=96 budget_1s=150 max_10s=546 budget_10s=600 |
| `rate-contract` 100/s *(note)* | admitted=4009 max_1s=160 budget_1s=300 max_10s=1007 budget_10s=1200 |

## What the ledger holds afterwards

| | |
|---|---|
| Jobs seeded | 1,005,000 (plus 4 recurring system slots) |
| Reached `Succeeded` | **1,004,765** |
| Reached `Failed` | **235**, every one an AtMostOnce charge a kill interrupted mid-body (`job.step-interrupted`) |
| Execution starts | 1,025,271 |
| Laned jobs | 20,000 in 2,000 lanes, **85 attempts killed mid-body and reclaimed**, every lane drained in order |
| Rate re-arms | 24,362, all `job.rate-limited` |
| Highest execution number on a seeded job outside the meters | **2** |
| Durable step rows | 4,510,000 |
| Events appended | 7,567,008 |
| Worker registrations | 150 (24 live at any moment; the rest are respawns after kills) |
| Workers reclaimed dead | 126 |
| Alerts | 0 |

An execution number above one outside the meters is an attempt a kill orphaned and recovery re-armed, and none needed a third attempt. The metered jobs reach higher numbers by design: a turn the meter booked stays valid for one second, and with 1,536 slots claiming at once a job that missed its turn is metered again, budget-neutral. No step row was recorded twice (`step-replay` 0) and no charge body ran past its guard (`at-most-once` 0).

At rest the four system slots read three Ready and `sys.alerts` Executing: a live worker claimed it eight seconds before the verdict, under a lease `no-stranded-work` accepts for a system slot, and the harness then stopped its workers. The `alerts` table held no rows: `sys.alerts` was still projecting the run's 7.6 million events, its cursor at event 372,996. The projection is not a certified property; the standard gates of the same round carry alerts end to end.

## The rate contract at scale

The contract is the upper envelope: in any window of T seconds a meter admits at most `R*(T + W) + B` handler starts, W being the one-second validity of a booked turn and B one second's worth of the rate. At 10/s the busiest second held 20 against 30 and the busiest ten seconds 108 against 120; at 50/s, 96 against 150 and 546 against 600; at 100/s, 160 against 300 and 1,007 against 1,200.

## Scope

Single namespace, single machine, one provider. Multi-namespace isolation at scale remains the ensemble gate's claim at its own shape. Checks are `anvil/Anvil/certify.sql` bound to `certr5cqpg` plus the `rate-contract` window scans in `CertifyVerdict`; re-run `anvil certify` against that schema to reproduce the verdict.
