# Acta certification seal

**Verdict: PASS** — the `1.0.0-rc.5` million-job run on SQL Server, beside the PostgreSQL one. 1,000,000 jobs across 1,536 slots, a real kill every five seconds for twelve minutes, then the ledger judged by `certify.sql`. The harness ran as a Native AOT executable.

## Run

| | |
|---|---|
| Captured | 2026-10-12T09:51:54Z |
| Commit | `4f410f68` |
| Provider | SQL Server 2022 16.0.4295.3 (RTM-CU27), in Docker, memory limit raised to 16 GB for this run |
| Schema | `certr5qms` |
| Run id | `r20261012-091459-c5c0d6` |
| Workload | Anvil `CrashRecovery`, **1,000,000 jobs** plus **5,000 outbox rows** staged under chaos; 5 steps of 250 ms per slow job; 100,000 AtMostOnce charges, 20,000 laned (2,000 lanes of ten), and 10,000 metered (2,000 at 10/s, 4,000 at 50/s, 4,000 at 100/s) |
| Workers | 24 processes × 64 executors = **1,536 concurrent slots**, Direct profile; connection pool capped at 16 per process |
| Chaos | Real `Process.Kill` every 5s for a 12-minute window |
| Quiesce | Chaos stopped, then waited until every job this run seeded (by correlation key) reached a terminal status: 25 minutes here; 37 minutes from seed to verdict |

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
| `chaos-was-real` | orphaned_attempts=8476 workers_marked_dead=126 |
| `clock-backsteps` *(note)* | backwards_writes=1092383 |
| `at-most-once` | 0 |
| `outbox-relayed-outcome` | 0 |
| `outbox-relayed` *(note)* | relayed_jobs=5000 |
| `tenant-context` | 0 |
| `lane-order` | 0 |
| `lane-drained` | 0 |
| `chaos-by-shape` *(note)* | charges_orphaned=1177 metered_orphaned=855 laned_orphaned=292 |
| `rate-contract` 10/s *(note)* | admitted=2017 max_1s=16 budget_1s=30 max_10s=102 budget_10s=120 |
| `rate-contract` 50/s *(note)* | admitted=4039 max_1s=87 budget_1s=150 max_10s=483 budget_10s=600 |
| `rate-contract` 100/s *(note)* | admitted=4059 max_1s=132 budget_1s=300 max_10s=926 budget_10s=1200 |

## What the ledger holds afterwards

| | |
|---|---|
| Jobs seeded | 1,005,000 (plus 5 recurring system slots) |
| Reached `Succeeded` | **1,004,609** |
| Reached `Failed` | **391**, every one an AtMostOnce charge a kill interrupted mid-body (`job.step-interrupted`) |
| Execution starts | 1,031,443 |
| Laned jobs | 20,000 in 2,000 lanes, **292 attempts killed mid-body and reclaimed**, every lane drained in order |
| Rate re-arms | 34,894, all `job.rate-limited` |
| Highest execution number on a seeded job outside the meters | **2** |
| Durable step rows | 4,510,000 |
| Events appended | 7,580,306 |
| Worker registrations | 150 (24 live at any moment; the rest are respawns after kills) |
| Workers reclaimed dead | 126 |
| Alerts | 2,821 |

An execution number above one outside the meters is an attempt a kill orphaned and recovery re-armed, and none needed a third attempt. The metered jobs reach higher numbers by design: a turn the meter booked stays valid for one second, and with 1,536 slots claiming at once a job that missed its turn is metered again, budget-neutral. No step row was recorded twice (`step-replay` 0) and no charge body ran past its guard (`at-most-once` 0).

At rest the five system slots read Ready.

## The rate contract at scale

The contract is the upper envelope: in any window of T seconds a meter admits at most `R*(T + W) + B` handler starts, W being the one-second validity of a booked turn and B one second's worth of the rate. At 10/s the busiest second held 16 against 30 and the busiest ten seconds 102 against 120; at 50/s, 87 against 150 and 483 against 600; at 100/s, 132 against 300 and 926 against 1,200.

## Scope

Single namespace, single machine, one provider, Direct profile only. Multi-namespace isolation at scale remains the ensemble gate's claim at its own shape. The checks are `anvil/Anvil/certify.sql` bound to `certr5qms` plus the `rate-contract` window scans in `CertifyVerdict`; re-run `anvil certify` against that schema to reproduce the verdict.
