# Acta certification seal

**Verdict: PASS** — **1,000,000 jobs** on SQL Server, every asserted property held. The `1.0.0-rc.4`
million-job seal at production concurrency: 24 worker processes, 1,536 concurrent executor slots, a
real kill every five seconds for twelve minutes, a hundred thousand AtMostOnce charges, three rate
meters, and two thousand lanes running through it. The harness ran as a Native AOT executable.

## Run

| | |
|---|---|
| Captured | 2026-10-02T15:42:23Z |
| Commit | `d616c72b` |
| Provider | SQL Server 2022 16.0.4265.3 (RTM-CU26), memory limit raised to 16 GB for this run |
| Schema | `certv1zqms` |
| Run id | `r20261002-145106-1df2d6` |
| Workload | Anvil `CrashRecovery`, **1,000,000 jobs** plus **5,000 outbox rows** staged under chaos; 5 steps of 250 ms per slow job; 100,000 AtMostOnce charges, 20,000 laned (2,000 lanes of ten), and 10,000 metered (2,000 at 10/s, 4,000 at 50/s, 4,000 at 100/s) |
| Workers | 24 processes × 64 executors = **1,536 concurrent slots**, Direct profile; connection pool capped at 16 per process |
| Chaos | Real `Process.Kill` every 5s for a 12-minute window |
| Quiesce | Chaos stopped, then waited until every job this run seeded (by correlation key) reached a terminal status: 39 minutes here |
| Wall clock | 51m 16s from seed to verdict, alone on the machine |

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
| `chaos-was-real` | orphaned_attempts=7505, workers_marked_dead=128 |
| `clock-backsteps` *(note)* | backwards_writes=80879 |
| `at-most-once` | 0 |
| `outbox-relayed-outcome` | 0 |
| `outbox-relayed` *(note)* | relayed_jobs=5000 |
| `tenant-context` | 0 |
| `lane-order` | 0 |
| `lane-drained` | 0 |
| `chaos-by-shape` *(note)* | charges_orphaned=573 metered_orphaned=304 laned_orphaned=181 |
| `rate-contract` 10/s *(note)* | admitted=2009 max_1s=17 budget_1s=30 max_10s=106 budget_10s=120 |
| `rate-contract` 50/s *(note)* | admitted=4004 max_1s=92 budget_1s=150 max_10s=529 budget_10s=600 |
| `rate-contract` 100/s *(note)* | admitted=4019 max_1s=179 budget_1s=300 max_10s=1075 budget_10s=1200 |

## The ledger afterwards

Not recorded. The machine reset 52 minutes after this verdict, while a benchmark was writing to the
same database, and SQL Server's crash recovery marked the database suspect on torn pages. The verdict
above was read from the intact ledger before that; the counts the PostgreSQL million-job seal carries
(execution starts, re-arms, step rows, events) could not be read back.

## The rate contract at scale

The contract is the upper envelope: in any window of T seconds a meter admits at most
`R*(T + W) + B` handler starts, W being the one-second validity of a booked turn and B one second's
worth of the rate. At 10/s the busiest second held 17 against 30 and the busiest ten seconds 106
against 120; at 50/s, 92 against 150 and 529 against 600; at 100/s, 179 against 300 and 1,075 against
1,200.

## Scope

Single namespace, single machine, one provider, Direct profile only. Multi-namespace isolation at
scale remains the ensemble gate's claim at its own shape. The checks are `anvil/Anvil/certify.sql`
bound to `certv1zqms` plus the `rate-contract` window scans in `CertifyVerdict`. The schema is gone
with its database, so this verdict cannot be re-run.
