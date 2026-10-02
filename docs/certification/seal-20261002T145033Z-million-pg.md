# Acta certification seal

**Verdict: PASS** — **1,000,000 jobs** on PostgreSQL, every asserted property held. The `1.0.0-rc.4`
million-job seal at production concurrency: 24 worker processes, 1,536 concurrent executor slots, a
real kill every five seconds for twelve minutes, a hundred thousand AtMostOnce charges, three rate
meters, and two thousand lanes running through it. The harness ran as a Native AOT executable.

## Run

| | |
|---|---|
| Captured | 2026-10-02T14:50:33Z |
| Commit | `d616c72b` |
| Provider | PostgreSQL 18.4 (Debian 18.4-1.pgdg13+1, x86_64) |
| Schema | `certv1zqpg` |
| Run id | `r20261002-143403-d4b5a0` |
| Workload | Anvil `CrashRecovery`, **1,000,000 jobs** plus **5,000 outbox rows** staged under chaos; 5 steps of 250 ms per slow job; 100,000 AtMostOnce charges, 20,000 laned (2,000 lanes of ten), and 10,000 metered (2,000 at 10/s, 4,000 at 50/s, 4,000 at 100/s) |
| Workers | 24 processes × 64 executors = **1,536 concurrent slots**, Direct profile, **153 registrations** across the run; connection pool capped at 16 per process |
| Chaos | Real `Process.Kill` every 5s for a 12-minute window |
| Quiesce | Chaos stopped, then waited until every job this run seeded (by correlation key) reached a terminal status |
| Wall clock | 16m 29s from seed to verdict |

**Timing — framework production defaults, unmodified**: `LeaseTtlSeconds` 180, `HeartbeatInterval`
45s, `WorkerDeadAfter` 5min, `SafetyPollInterval` 1s. Every property below is timing-dependent, so a
run with different values certifies that configuration, not the shipped one.

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
| `chaos-was-real` | orphaned_attempts=7868, workers_marked_dead=113 |
| `clock-backsteps` *(note)* | backwards_writes=2345414 |
| `at-most-once` | 0 |
| `outbox-relayed-outcome` | 0 |
| `outbox-relayed` *(note)* | relayed_jobs=5000 |
| `tenant-context` | 0 |
| `lane-order` | 0 |
| `lane-drained` | 0 |
| `chaos-by-shape` *(note)* | charges_orphaned=422 metered_orphaned=209 laned_orphaned=111 |
| `rate-contract` 10/s *(note)* | admitted=2004 max_1s=20 budget_1s=30 max_10s=110 budget_10s=120 |
| `rate-contract` 50/s *(note)* | admitted=4007 max_1s=97 budget_1s=150 max_10s=546 budget_10s=600 |
| `rate-contract` 100/s *(note)* | admitted=4005 max_1s=168 budget_1s=300 max_10s=1031 budget_10s=1200 |

## What the ledger holds afterwards

| | |
|---|---|
| Jobs seeded | 1,005,000 (plus 5 recurring system slots) |
| Reached `Succeeded` | **1,004,615** |
| Reached `Failed` | **385**, every one an AtMostOnce charge a kill interrupted mid-body (`job.step-interrupted`) |
| Execution starts | 1,027,756 |
| Laned jobs | 20,000 in 2,000 lanes, **111 attempts killed mid-body and reclaimed**, every lane drained in order |
| Rate re-arms | 14,886, all `job.rate-limited` |
| Highest execution number on a seeded job outside the meters | **2** |
| Durable step rows | 4,510,000 |
| Events appended | 7,574,611 |
| Worker registrations | 153 (24 live at any moment; the rest are respawns after kills) |
| Workers reclaimed dead | 113 |

An execution number above one outside the meters is an attempt a kill orphaned and recovery re-armed,
and none needed a third attempt. The metered jobs reach higher numbers by design: a turn the meter
booked stays valid for one second, and with 1,536 slots claiming at once a job that missed its turn is
metered again, budget-neutral. Of the 422 charges a kill reached, 385 were inside the guarded step and
ended Failed, as the AtMostOnce contract requires; the other 37 were killed outside it, before it began
or after its outcome was recorded, and finished on retry with the step recorded once. No step row was recorded twice (`step-replay` 0) and no charge body ran past its
guard (`at-most-once` 0).

The `alerts` table held no rows when the verdict read the ledger: `sys.alerts` was still projecting
the run's 7.6 million events. The projection is not a certified property; the standard gates of the
same round carry alerts end to end.

## The rate contract at scale

The contract is the upper envelope: in any window of T seconds a meter admits at most
`R*(T + W) + B` handler starts, W being the one-second validity of a booked turn and B one second's
worth of the rate. At 10/s the busiest second held 20 against 30 and the busiest ten seconds 110
against 120; at 50/s, 97 against 150 and 546 against 600; at 100/s, 168 against 300 and 1,031 against
1,200.

## Scope

Single namespace, single machine, one provider, Direct profile only. Multi-namespace isolation at
scale remains the ensemble gate's claim at its own shape. The checks are `anvil/Anvil/certify.sql`
bound to `certv1zqpg` plus the `rate-contract` window scans in `CertifyVerdict`; re-run
`anvil certify` against that schema to reproduce the verdict.
