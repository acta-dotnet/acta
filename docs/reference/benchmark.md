# Acta 1.0.0-rc.15: benchmark and release validation

The 1.0.0-rc.15 release evidence, measured by the project on its own machine: one Acta Verify release run on `0907b3af4` on PostgreSQL, SQL Server, and SQLite. Every correctness, fault, and chaos scenario passed, every trial's ledger passed the ledger checks, and every performance floor held, with no rerun.

- The run's tree is the tagged one but for five source files and the documentation. Changed after the run: the text of SQLite's refusal of an earlier candidate's file, `DbParams.JsonIdArray` taking a list of ints with the same output, `JobRequestBuilder.Deduplicate` renamed to `IdempotencyKeyFor`, and two doc comments. Everything else changed after it is CI configuration, the page generator, tests, the how-tos and documentation, and the full gates passed on the tagged tree.
- PostgreSQL's four workers held 15,000/s offered in all three 30 s passes and in the 120 s confirmation, completing 14,841 jobs a second with p99 1.5 s. One worker, woken in process, completed 7,500 a second with p99 55 ms. SQL Server held 5,000/s on four workers (4,999 a second, p99 926 ms) and on one (4,994, p99 774 ms). SQLite held 250 a second on one worker with p99 185 ms.
- Both PostgreSQL sixteen-worker ladders held every written rate up to 15,000/s: 15,274 jobs a second on two namespaces with p99 1.2 s, and 15,368 on one with p99 877 ms.
- The crash-recovery chaos runs finished a million jobs each on PostgreSQL and SQL Server while a worker was killed every five seconds, with 79 and 80 busy kills reclaimed, and ten thousand on SQLite with 52.
- This is measurement version 3, as in rc.14, and `acta-verify compare` sets the run beside rc.14's, both on .NET 10.0.15. All 218 scenario runs the two share passed both times; the hot-lane scenario is not compared, since its rate ladder changed, and the two scenarios new in rc.15 ran only here. Of the measured cells, 16 read faster, 48 the same, and 27 slower, each from one run per release, and the tool's verdict word for that is "regression".
- Twenty-four of the twenty-seven are p99s at rates both runs held, each inside its floor, and none was rerun. Three are rates, all drains: SQLite's, 5,482 a second against 6,230; SQL Server's with one worker, 10,317 against 12,453; and SQL Server's with four workers, 28,536 against 34,482.
- The three were run again alone. SQLite's drain read 7,337 a second on rc.15 across twelve passes, above rc.14's figure. SQL Server's four-worker drain read 33,248 across four runs, within 4% of rc.14's. SQL Server's one-worker drain was run on both releases back to back, twice each: 14,363 a second on rc.14 and 13,889 on rc.15, 3.3% slower, with the runs of each release within 1% of each other. That gap is real. rc.15 adds an index on a job's retention date, which every landing job maintains, and records how long each execution waited.

# Acta Verify: PASS

The release suite on PostgreSQL, SQL Server, SQLite, commit `0907b3af4`, started 2026-11-21 14:51 UTC, 7 h 44 min, on Intel(R) Core(TM) i9-14900K.

| | PostgreSQL | SQL Server | SQLite |
|---|---|---|---|
| Correctness | pass | pass | pass |
| Crash-recovery chaos | pass, 1,000,000 jobs, 79 busy kills reclaimed | pass, 1,000,000 jobs, 80 busy kills reclaimed | pass, 10,000 jobs, 52 busy kills reclaimed |
| Ledger checks | pass, 165 trials checked | pass, 154 trials checked | pass, 130 trials checked |
| Performance floors | held | held | held |
| Drain | 85,807 jobs/s | 43,111 jobs/s | 5,508 jobs/s |
| Throughput | 14,841 jobs/s | 4,999 jobs/s | 250 jobs/s |
| Throughput, one worker | 7,500 jobs/s | 4,994 jobs/s | not written |

> **14,841 fully executed, durably completed jobs per second** on PostgreSQL: 4 workers polling for work, one job per enqueue call, held 3 × 30 s and 120 s at 15,000/s offered, p99 1.5 s, Intel(R) Core(TM) i9-14900K.

> **4,999 fully executed, durably completed jobs per second** on SQL Server: 4 workers polling for work, one job per enqueue call, held 3 × 30 s and 120 s at 5,000/s offered, p99 926 ms, Intel(R) Core(TM) i9-14900K.

> **250 fully executed, durably completed jobs per second** on SQLite: 1 worker, woken in process, one job per enqueue call, held 3 × 30 s and 120 s at 250/s offered, p99 185 ms, Intel(R) Core(TM) i9-14900K.

> **7,500 fully executed, durably completed jobs per second** on PostgreSQL: 1 worker, woken in process, one job per enqueue call, held 3 × 30 s and 120 s at 7,500/s offered, p99 55 ms, Intel(R) Core(TM) i9-14900K.

> **4,994 fully executed, durably completed jobs per second** on SQL Server: 1 worker, woken in process, one job per enqueue call, held 3 × 30 s and 120 s at 5,000/s offered, p99 774 ms, Intel(R) Core(TM) i9-14900K.

## Rates to raise

These written rates ask less than the database ran; they decide nothing in this run.

- **Enqueue, one job per call** on SQLite: the floor of 200/s also held at 800/s.
- **Enqueue in batches of 100** on PostgreSQL: the floor of 10,000/s also held at 40,000/s.
- **Enqueue in batches of 1,000** on PostgreSQL: the floor of 20,000/s also held at 80,000/s.
- **Throughput, sixteen workers** on PostgreSQL: every written rate held, up to 15,000/s, so the ladder found no ceiling.
- **Throughput, sixteen workers on one namespace** on PostgreSQL: every written rate held, up to 15,000/s, so the ladder found no ceiling.
- **Transient failure and retry** on SQLite: the floor of 160/s also held at 640/s.
- **Payloads of 4 KB** on SQLite: the floor of 120/s also held at 480/s.
- **Payloads of 256 B, 4 KB and 64 KB mixed** on SQLite: the floor of 160/s also held at 640/s.
- **Payloads of 64 KB** on SQLite: the floor of 40/s also held at 160/s.
- **A thousand lanes** on SQL Server: the floor of 1,000/s also held at 4,000/s.
- **A thousand lanes** on SQLite: the floor of 120/s also held at 480/s.
- **Operator queries during processing** on SQLite: the floor of 120/s also held at 480/s.
- **Parked work alongside** on SQL Server: the floor of 500/s also held at 2,000/s.
- **Parked work alongside** on SQLite: the floor of 160/s also held at 640/s.
- **Future high-priority work alongside** on SQL Server: the floor of 320/s also held at 1,280/s.
- **Future high-priority work alongside** on SQLite: the floor of 160/s also held at 640/s.
- **One hot concurrency key** on PostgreSQL: the floor of 1/s also held at 4/s.
- **One hot concurrency key** on SQL Server: the floor of 1/s also held at 4/s.
- **One hot concurrency key** on SQLite: the floor of 4/s also held at 16/s.

## Performance

Each scenario's first written rate is its floor, which this suite requires to hold.

### Drain

How fast do eight workers over two namespaces run and durably complete a backlog enqueued before they start?

Setup: workers: 8 on PostgreSQL, 8 on SQL Server, 1 on SQLite; namespaces: 2 on PostgreSQL, 2 on SQL Server, 1 on SQLite; executors per worker: the product default; enqueue calls in flight: 8; jobs per call: 1,000.

| Database | Pass | Jobs | Steady rate, database clock | Whole cohort | Disk write, mean / p95 | Result |
|---|---:|---:|---:|---:|---:|---|
| PostgreSQL | 1 | 1,000,000 | 83,624 jobs/s | 14 s | 0.1 / 0.337 ms | held |
| PostgreSQL | 2 | 1,000,000 | 85,807 jobs/s | 12.8 s | 4.37 / 33.2 ms | held |
| PostgreSQL | 3 | 1,000,000 | 86,346 jobs/s | 12.1 s | 0.14 / 0.509 ms | held |
| SQL Server | 1 | 1,000,000 | 43,111 jobs/s | 26.3 s | 0.349 / 1.15 ms | held |
| SQL Server | 2 | 1,000,000 | 43,761 jobs/s | 23.5 s | 3.22 / 0.403 ms | held |
| SQL Server | 3 | 1,000,000 | 40,588 jobs/s | 24.9 s | 2.33 / 3.26 ms | held |
| SQLite | 1 | 100,000 | 5,501 jobs/s | 17.9 s | 0.0596 / 0.257 ms | held |
| SQLite | 2 | 100,000 | 5,508 jobs/s | 17.8 s | 0.0531 / 0.0929 ms | held |
| SQLite | 3 | 100,000 | 5,630 jobs/s | 17.8 s | 0.0575 / 0.127 ms | held |

### Sustained throughput

How many jobs per second, one per enqueue call, does Acta enqueue, run and durably complete with a flat backlog and p99 under 2 s?

Setup: workers: 4 on PostgreSQL, 4 on SQL Server, 1 on SQLite; namespaces: 2 on PostgreSQL, 2 on SQL Server, 1 on SQLite; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; workers learn of new work: by polling on PostgreSQL, by polling on SQL Server, in process on SQLite; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Disk write, mean / p95 | Result |
|---|---:|---:|---:|---:|---:|---|
| PostgreSQL | 10,000 | 1 | 9,958 | 1.0 s | 0.646 / 9.14 ms | held |
| PostgreSQL | 10,000 | 2 | 9,944 | 1.0 s | 0.0506 / 0.0543 ms | held |
| PostgreSQL | 10,000 | 3 | 9,929 | 1.0 s | 0.0497 / 0.053 ms | held |
| PostgreSQL | 12,500 | 1 | 12,223 | 1.0 s | 0.0459 / 0.0577 ms | held |
| PostgreSQL | 12,500 | 2 | 12,335 | 1.0 s | 0.136 / 1.73 ms | held |
| PostgreSQL | 12,500 | 3 | 12,416 | 1.0 s | 0.045 / 0.115 ms | held |
| PostgreSQL | 15,000 | 1 | 14,981 | 1.0 s | 0.0482 / 0.185 ms | held |
| PostgreSQL | 15,000 | 2 | 14,688 | 1.0 s | 0.055 / 0.207 ms | held |
| PostgreSQL | 15,000 | 3 | 14,830 | 1.0 s | 0.0464 / 0.142 ms | held |
| PostgreSQL | 17,500 | 1 | 17,429 | 1.2 s | 0.0617 / 0.0681 ms | missed: backlog grew 329 jobs/s, 175 allowed |
| PostgreSQL | 17,500 | 2 | 17,294 | 1.7 s | 0.0484 / 0.139 ms | held |
| PostgreSQL | 17,500 | 3 | 16,332 | 3.1 s | 0.199 / 2.55 ms | missed: backlog grew 1,311 jobs/s, 175 allowed |
| PostgreSQL | 20,000 | 2 | 17,547 | 5.6 s | 0.0591 / 0.0676 ms | missed: backlog grew 2,318 jobs/s, 200 allowed |
| PostgreSQL | 25,000 | | | | | not run by rule |
| PostgreSQL | 30,000 | | | | | not run by rule |
| PostgreSQL | 15,000 | confirm, 120 s | 14,841 | 1.5 s | 0.152 / 0.438 ms | held |
| SQL Server | 5,000 | 1 | 5,054 | 778 ms | 0.0269 / 0.054 ms | held |
| SQL Server | 5,000 | 2 | 4,998 | 926 ms | 0.921 / 16.5 ms | held |
| SQL Server | 5,000 | 3 | 5,000 | 813 ms | 0.822 / 17.9 ms | held |
| SQL Server | 7,500 | 1 | 6,678 | 5.2 s | 0.153 / 0.807 ms | missed: backlog grew 778 jobs/s, 75 allowed |
| SQL Server | 7,500 | 2 | 6,499 | 5.8 s | 0.249 / 0.77 ms | missed: backlog grew 991 jobs/s, 75 allowed |
| SQL Server | 7,500 | 3 | 6,542 | 5.2 s | 0.153 / 0.817 ms | missed: backlog grew 968 jobs/s, 75 allowed |
| SQL Server | 10,000 | | | | | not run by rule |
| SQL Server | 12,500 | | | | | not run by rule |
| SQL Server | 15,000 | | | | | not run by rule |
| SQL Server | 5,000 | confirm, 120 s | 4,999 | 926 ms | 0.303 / 1.33 ms | held |
| SQLite | 250 | 1 | 250 | 123 ms | 0.0325 / 0.0464 ms | held |
| SQLite | 250 | 2 | 250 | 135 ms | 0.0335 / 0.0396 ms | held |
| SQLite | 250 | 3 | 250 | 169 ms | 0.0324 / 0.0393 ms | held |
| SQLite | 500 | 1 | 500 | 99 ms | 0.026 / 0.0408 ms | held |
| SQLite | 500 | 2 | 500 | 94 ms | 0.0257 / 0.0319 ms | held |
| SQLite | 500 | 3 | 297 | 24.1 s | 0.0357 / 0.0399 ms | missed: backlog grew 204 jobs/s, 5 allowed |
| SQLite | 1,000 | 1 | 670 | 33.2 s | 0.0276 / 0.0366 ms | missed: backlog grew 310 jobs/s, 10 allowed |
| SQLite | 1,000 | 2 | 321 | 70.1 s | 0.0333 / 0.0391 ms | missed: backlog grew 700 jobs/s, 10 allowed |
| SQLite | 2,000 | | | | | not run by rule |
| SQLite | 250 | confirm, 120 s | 250 | 185 ms | 0.0338 / 0.0397 ms | held |

Highest rate held in every pass: PostgreSQL 15,000/s; SQL Server 5,000/s; SQLite 250/s.

### Sustained throughput, woken through Redis

With Redis carrying each enqueue's wake to the worker processes, how many jobs per second, one per enqueue call, does Acta enqueue, run and durably complete with a flat backlog and p99 under 2 s?

Setup: workers: 4 on PostgreSQL, 4 on SQL Server, 1 on SQLite; namespaces: 2 on PostgreSQL, 2 on SQL Server, 1 on SQLite; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; workers learn of new work: through Redis on PostgreSQL, through Redis on SQL Server, in process on SQLite; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Disk write, mean / p95 | Result |
|---|---:|---:|---:|---:|---:|---|
| PostgreSQL | 10,000 | 1 | 10,002 | 101 ms | 0.0368 / 0.0503 ms | held |
| PostgreSQL | 12,500 | 1 | 12,506 | 99 ms | 0.037 / 0.0387 ms | held |
| PostgreSQL | 15,000 | 1 | 15,014 | 103 ms | 0.0518 / 0.072 ms | held |
| PostgreSQL | 17,500 | 1 | 16,273 | 3.1 s | 0.0646 / 0.0676 ms | missed: backlog grew 1,206 jobs/s, 175 allowed |
| PostgreSQL | 20,000 | | | | | not run by rule |
| PostgreSQL | 25,000 | | | | | not run by rule |
| PostgreSQL | 30,000 | | | | | not run by rule |
| SQL Server | 5,000 | 1 | 4,998 | 201 ms | 0.0342 / 0.0562 ms | held |
| SQL Server | 7,500 | 1 | 6,440 | 6.9 s | 0.0759 / 0.458 ms | missed: backlog grew 984 jobs/s, 75 allowed |
| SQL Server | 10,000 | | | | | not run by rule |
| SQL Server | 12,500 | | | | | not run by rule |
| SQL Server | 15,000 | | | | | not run by rule |
| SQLite | | | | | | SQLite runs its one worker in the controller's process, where an enqueue wakes it without a transport. |

Highest rate held in every pass: PostgreSQL 15,000/s; SQL Server 5,000/s.

### Sustained throughput, one worker

How many jobs per second does one worker of default executors, in the process that enqueues, run and durably complete, one job per enqueue call, with a flat backlog and p99 under 2 s?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; workers learn of new work: in process; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Disk write, mean / p95 | Result |
|---|---:|---:|---:|---:|---:|---|
| PostgreSQL | 5,000 | 1 | 5,001 | 51 ms | 0.0342 / 0.0375 ms | held |
| PostgreSQL | 5,000 | 2 | 5,001 | 54 ms | 0.0358 / 0.039 ms | held |
| PostgreSQL | 5,000 | 3 | 4,998 | 147 ms | 0.0418 / 0.145 ms | held |
| PostgreSQL | 7,500 | 1 | 7,501 | 42 ms | 0.0339 / 0.0346 ms | held |
| PostgreSQL | 7,500 | 2 | 7,501 | 51 ms | 0.0419 / 0.0434 ms | held |
| PostgreSQL | 7,500 | 3 | 7,493 | 50 ms | 0.0369 / 0.0434 ms | held |
| PostgreSQL | 10,000 | 1 | 9,207 | 2.4 s | 0.0429 / 0.048 ms | missed: backlog grew 823 jobs/s, 100 allowed |
| PostgreSQL | 10,000 | 2 | 10,217 | 1.5 s | 0.0391 / 0.0481 ms | held |
| PostgreSQL | 10,000 | 3 | 10,189 | 1.9 s | 0.0417 / 0.0495 ms | held |
| PostgreSQL | 12,500 | 2 | 10,726 | 4.7 s | 0.0383 / 0.0542 ms | missed: backlog grew 1,833 jobs/s, 125 allowed |
| PostgreSQL | 12,500 | 3 | 7,369 | 13.7 s | 0.145 / 0.0801 ms | missed: backlog grew 5,088 jobs/s, 125 allowed |
| PostgreSQL | 15,000 | | | | | not run by rule |
| PostgreSQL | 7,500 | confirm, 120 s | 7,500 | 55 ms | 0.0536 / 0.0948 ms | held |
| SQL Server | 2,500 | 1 | 2,500 | 92 ms | 0.0355 / 0.377 ms | held |
| SQL Server | 2,500 | 2 | 2,502 | 94 ms | 0.244 / 4.81 ms | held |
| SQL Server | 2,500 | 3 | 2,499 | 91 ms | 0.138 / 5.36 ms | held |
| SQL Server | 5,000 | 1 | 5,002 | 297 ms | 0.146 / 4.03 ms | held |
| SQL Server | 5,000 | 2 | 5,012 | 148 ms | 0.145 / 3.35 ms | held |
| SQL Server | 5,000 | 3 | 5,000 | 153 ms | 0.107 / 0.571 ms | held |
| SQL Server | 7,500 | 1 | 5,833 | 11.2 s | 0.163 / 0.66 ms | missed: backlog grew 1,551 jobs/s, 75 allowed |
| SQL Server | 7,500 | 2 | 5,939 | 9.6 s | 0.205 / 0.713 ms | missed: backlog grew 1,432 jobs/s, 75 allowed |
| SQL Server | 7,500 | 3 | 5,926 | 9.3 s | 0.4 / 2.97 ms | missed: backlog grew 1,546 jobs/s, 75 allowed |
| SQL Server | 10,000 | | | | | not run by rule |
| SQL Server | 5,000 | confirm, 120 s | 4,994 | 774 ms | 0.357 / 1.68 ms | held |
| SQLite | | | | | | The scenario writes no rate for sqlite. |

Highest rate held in every pass: PostgreSQL 7,500/s; SQL Server 5,000/s.

### Enqueue, one job per call

How fast are single-job enqueue calls acknowledged, each in its own transaction, before any worker runs?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; workers learn of new work: by polling; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Disk write, mean / p95 | Result |
|---|---:|---:|---:|---:|---:|---|
| PostgreSQL | 10,000 | 1 | 0 | 27 ms | 0.0453 / 0.0482 ms | held |
| SQL Server | 5,000 | 1 | 0 | 30 ms | 0.0281 / 0.0546 ms | held |
| SQLite | 200 | 1 | 0 | 37 ms | 0.0629 / 0.109 ms | held |

Highest rate held in every pass: PostgreSQL 10,000/s, every written rate, so the ceiling is above the ladder; SQL Server 5,000/s, every written rate, so the ceiling is above the ladder; SQLite 200/s, every written rate, so the ceiling is above the ladder.

### Enqueue in batches of 100

How fast are batches of 100 jobs acknowledged, before any worker runs?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 100; workers learn of new work: by polling; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Disk write, mean / p95 | Result |
|---|---:|---:|---:|---:|---:|---|
| PostgreSQL | 10,000 | 1 | 0 | 34 ms | 0.0556 / 0.0383 ms | held |
| SQL Server | 5,000 | 1 | 0 | 49 ms | 3.62 / 0.203 ms | held |
| SQLite | 5,000 | 1 | 0 | 81 ms | 0.0385 / 0.0529 ms | held |

Highest rate held in every pass: PostgreSQL 10,000/s, every written rate, so the ceiling is above the ladder; SQL Server 5,000/s, every written rate, so the ceiling is above the ladder; SQLite 5,000/s, every written rate, so the ceiling is above the ladder.

### Enqueue in batches of 1,000

How fast are batches of 1,000 jobs acknowledged, before any worker runs?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1,000; workers learn of new work: by polling; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Disk write, mean / p95 | Result |
|---|---:|---:|---:|---:|---:|---|
| PostgreSQL | 20,000 | 1 | 0 | 95 ms | 0.523 / 0.151 ms | held |
| SQL Server | 10,000 | 1 | 0 | 209 ms | 1.11 / 0.169 ms | held |
| SQLite | 10,000 | 1 | 0 | 116 ms | 0.0615 / 0.128 ms | held |

Highest rate held in every pass: PostgreSQL 20,000/s, every written rate, so the ceiling is above the ladder; SQL Server 10,000/s, every written rate, so the ceiling is above the ladder; SQLite 10,000/s, every written rate, so the ceiling is above the ladder.

### Drain, one worker

How fast does one worker run and durably complete a backlog enqueued before it starts?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 8; jobs per call: 1,000.

| Database | Pass | Jobs | Steady rate, database clock | Whole cohort | Disk write, mean / p95 | Result |
|---|---:|---:|---:|---:|---:|---|
| PostgreSQL | 1 | 200,000 | 19,942 jobs/s | 10.5 s | 0.134 / 0.51 ms | held |
| SQL Server | 1 | 200,000 | 11,310 jobs/s | 18.2 s | 3.03 / 27.8 ms | held |
| SQLite | 1 | 50,000 | 7,764 jobs/s | 6.68 s | 0.064 / 0.429 ms | held |

### Drain, four workers

How fast do four workers over two namespaces drain a backlog enqueued before they start?

Setup: workers: 4; namespaces: 2; executors per worker: the product default; enqueue calls in flight: 8; jobs per call: 1,000.

| Database | Pass | Jobs | Steady rate, database clock | Whole cohort | Disk write, mean / p95 | Result |
|---|---:|---:|---:|---:|---:|---|
| PostgreSQL | 1 | 200,000 | 51,200 jobs/s | 4.12 s | 0.0575 / 0.119 ms | held |
| SQL Server | 1 | 200,000 | 33,218 jobs/s | 6.01 s | 1.6 / 15.7 ms | held |
| SQLite | | | | | | The scenario writes no cohort for sqlite. |

### Drain, sixteen workers

How fast do sixteen workers over two namespaces drain a backlog enqueued before they start?

Setup: workers: 16; namespaces: 2; executors per worker: the product default; enqueue calls in flight: 8; jobs per call: 1,000.

| Database | Pass | Jobs | Steady rate, database clock | Whole cohort | Disk write, mean / p95 | Result |
|---|---:|---:|---:|---:|---:|---|
| PostgreSQL | 1 | 1,000,000 | 72,918 jobs/s | 14.6 s | 0.36 / 2.42 ms | held |
| SQL Server | 1 | 1,000,000 | 44,165 jobs/s | 25.2 s | 6.41 / 1.48 ms | held |
| SQLite | | | | | | The scenario writes no cohort for sqlite. |

### Throughput, sixteen workers

Do sixteen worker processes over two namespaces hold a high offered rate with p99 under 2 s?

Setup: workers: 16; namespaces: 2; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; workers learn of new work: by polling; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Disk write, mean / p95 | Result |
|---|---:|---:|---:|---:|---:|---|
| PostgreSQL | 12,500 | 1 | 12,588 | 699 ms | 0.0564 / 0.0639 ms | held |
| PostgreSQL | 15,000 | 1 | 15,274 | 1.2 s | 0.178 / 1.49 ms | held |
| SQL Server | 4,000 | 1 | 4,020 | 1.2 s | 0.0873 / 2.58 ms | held |
| SQLite | | | | | | The scenario writes no rate for sqlite. |

Highest rate held in every pass: PostgreSQL 15,000/s, every written rate, so the ceiling is above the ladder; SQL Server 4,000/s, every written rate, so the ceiling is above the ladder.

### Throughput, sixteen workers, woken through Redis

With Redis carrying each enqueue's wake, do sixteen worker processes over two namespaces hold a high offered rate with p99 under 2 s?

Setup: workers: 16; namespaces: 2; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; workers learn of new work: through Redis; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Disk write, mean / p95 | Result |
|---|---:|---:|---:|---:|---:|---|
| PostgreSQL | 12,500 | 1 | 12,554 | 165 ms | 0.0793 / 0.606 ms | held |
| SQL Server | 4,000 | 1 | 4,020 | 476 ms | 0.146 / 0.798 ms | held |
| SQLite | | | | | | SQLite is one process's database; sixteen worker processes are not a SQLite topology. |

Highest rate held in every pass: PostgreSQL 12,500/s, every written rate, so the ceiling is above the ladder; SQL Server 4,000/s, every written rate, so the ceiling is above the ladder.

### Throughput, sixteen workers on one namespace

Do sixteen worker processes sharing one namespace hold a high offered rate with p99 under 2 s?

Setup: workers: 16; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; workers learn of new work: by polling; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Disk write, mean / p95 | Result |
|---|---:|---:|---:|---:|---:|---|
| PostgreSQL | 12,500 | 1 | 12,756 | 741 ms | 0.0458 / 0.0662 ms | held |
| PostgreSQL | 15,000 | 1 | 15,368 | 877 ms | 0.0444 / 0.0492 ms | held |
| SQL Server | 4,000 | 1 | 4,101 | 569 ms | 0.127 / 4.13 ms | held |
| SQLite | | | | | | The scenario writes no rate for sqlite. |

Highest rate held in every pass: PostgreSQL 15,000/s, every written rate, so the ceiling is above the ladder; SQL Server 4,000/s, every written rate, so the ceiling is above the ladder.

### Throughput, 32 executors

Does one worker with 32 executors hold the stream its default configuration holds?

Setup: workers: 1; namespaces: 1; executors per worker: 32; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; workers learn of new work: in process; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Disk write, mean / p95 | Result |
|---|---:|---:|---:|---:|---:|---|
| PostgreSQL | 5,000 | 1 | 5,078 | 115 ms | 0.0321 / 0.0355 ms | held |
| SQL Server | 2,500 | 1 | 2,504 | 86 ms | 0.0261 / 0.0434 ms | held |
| SQLite | 160 | 1 | 160 | 102 ms | 0.0397 / 0.0541 ms | held |

Highest rate held in every pass: PostgreSQL 5,000/s, every written rate, so the ceiling is above the ladder; SQL Server 2,500/s, every written rate, so the ceiling is above the ladder; SQLite 160/s, every written rate, so the ceiling is above the ladder.

### Three persisted steps

Do jobs that commit three checkpoints each hold their offered rate with p99 under 5 s?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; workers learn of new work: in process; p99 target: 5.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Disk write, mean / p95 | Result |
|---|---:|---:|---:|---:|---:|---|
| PostgreSQL | 1,000 | 1 | 1,001 | 107 ms | 0.026 / 0.0418 ms | held |
| SQL Server | 1,000 | 1 | 1,001 | 111 ms | 0.0294 / 0.151 ms | held |
| SQLite | 40 | 1 | 40 | 112 ms | 0.0404 / 0.0552 ms | held |

Highest rate held in every pass: PostgreSQL 1,000/s, every written rate, so the ceiling is above the ladder; SQL Server 1,000/s, every written rate, so the ceiling is above the ladder; SQLite 40/s, every written rate, so the ceiling is above the ladder.

### Transient failure and retry

With one job in four failing twice before it succeeds, does the stream hold its rate with p99 under 10 s?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; workers learn of new work: in process; p99 target: 10.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Disk write, mean / p95 | Result |
|---|---:|---:|---:|---:|---:|---|
| PostgreSQL | 5,000 | 1 | 5,004 | 2.1 s | 0.0363 / 0.0489 ms | held |
| SQL Server | 2,500 | 1 | 2,499 | 2.2 s | 0.6 / 4.73 ms | held |
| SQLite | 160 | 1 | 160 | 2.3 s | 0.0388 / 0.0564 ms | held |

Highest rate held in every pass: PostgreSQL 5,000/s, every written rate, so the ceiling is above the ladder; SQL Server 2,500/s, every written rate, so the ceiling is above the ladder; SQLite 160/s, every written rate, so the ceiling is above the ladder.

### Fixed CPU work

Do jobs that each hash for a fixed number of rounds hold their offered rate with p99 under 2 s?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; workers learn of new work: in process; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Disk write, mean / p95 | Result |
|---|---:|---:|---:|---:|---:|---|
| PostgreSQL | 4,000 | 1 | 4,002 | 65 ms | 0.0367 / 0.0386 ms | held |
| SQL Server | 2,500 | 1 | 2,499 | 96 ms | 0.149 / 1.25 ms | held |
| SQLite | 160 | 1 | 160 | 95 ms | 0.0425 / 0.0767 ms | held |

Highest rate held in every pass: PostgreSQL 4,000/s, every written rate, so the ceiling is above the ladder; SQL Server 2,500/s, every written rate, so the ceiling is above the ladder; SQLite 160/s, every written rate, so the ceiling is above the ladder.

### Payloads of 256 B

Do jobs carrying a 256-byte payload hold their offered rate with p99 under 2 s?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; workers learn of new work: in process; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Disk write, mean / p95 | Result |
|---|---:|---:|---:|---:|---:|---|
| PostgreSQL | 4,000 | 1 | 4,003 | 58 ms | 0.0442 / 0.11 ms | held |
| SQL Server | 2,500 | 1 | 2,498 | 88 ms | 0.0247 / 0.031 ms | held |
| SQLite | 120 | 1 | 120 | 98 ms | 0.0445 / 0.215 ms | held |

Highest rate held in every pass: PostgreSQL 4,000/s, every written rate, so the ceiling is above the ladder; SQL Server 2,500/s, every written rate, so the ceiling is above the ladder; SQLite 120/s, every written rate, so the ceiling is above the ladder.

### Payloads of 4 KB

Do jobs carrying a 4 KB payload hold their offered rate with p99 under 2 s?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; workers learn of new work: in process; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Disk write, mean / p95 | Result |
|---|---:|---:|---:|---:|---:|---|
| PostgreSQL | 5,000 | 1 | 5,000 | 51 ms | 0.0371 / 0.0478 ms | held |
| SQL Server | 2,500 | 1 | 2,502 | 91 ms | 0.104 / 2.89 ms | held |
| SQLite | 120 | 1 | 120 | 95 ms | 0.0438 / 0.0578 ms | held |

Highest rate held in every pass: PostgreSQL 5,000/s, every written rate, so the ceiling is above the ladder; SQL Server 2,500/s, every written rate, so the ceiling is above the ladder; SQLite 120/s, every written rate, so the ceiling is above the ladder.

### Payloads of 256 B, 4 KB and 64 KB mixed

Do jobs carrying payloads of three sizes in turn hold their offered rate with p99 under 2 s?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; workers learn of new work: in process; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Disk write, mean / p95 | Result |
|---|---:|---:|---:|---:|---:|---|
| PostgreSQL | 2,500 | 1 | 2,499 | 81 ms | 0.0556 / 0.0589 ms | held |
| SQL Server | 1,000 | 1 | 1,001 | 103 ms | 0.388 / 4.18 ms | held |
| SQLite | 160 | 1 | 160 | 90 ms | 0.039 / 0.0573 ms | held |

Highest rate held in every pass: PostgreSQL 2,500/s, every written rate, so the ceiling is above the ladder; SQL Server 1,000/s, every written rate, so the ceiling is above the ladder; SQLite 160/s, every written rate, so the ceiling is above the ladder.

### Payloads of 64 KB

Do jobs carrying a 64 KB payload hold their offered rate with p99 under 2 s?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; workers learn of new work: in process; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Disk write, mean / p95 | Result |
|---|---:|---:|---:|---:|---:|---|
| PostgreSQL | 1,000 | 1 | 1,000 | 83 ms | 0.0416 / 0.044 ms | held |
| SQL Server | 500 | 1 | 500 | 110 ms | 17.2 / 137 ms | held |
| SQLite | 40 | 1 | 40 | 25 ms | 0.047 / 0.0687 ms | held |

Highest rate held in every pass: PostgreSQL 1,000/s, every written rate, so the ceiling is above the ladder; SQL Server 500/s, every written rate, so the ceiling is above the ladder; SQLite 40/s, every written rate, so the ceiling is above the ladder.

### A thousand lanes

Spread over a thousand ordered lanes, does the stream hold its rate with p99 under 2 s?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; workers learn of new work: in process; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Disk write, mean / p95 | Result |
|---|---:|---:|---:|---:|---:|---|
| PostgreSQL | 4,000 | 1 | 4,003 | 58 ms | 0.0379 / 0.0408 ms | held |
| SQL Server | 1,000 | 1 | 1,000 | 95 ms | 0.71 / 5.81 ms | held |
| SQLite | 120 | 1 | 120 | 95 ms | 0.0433 / 0.0761 ms | held |

Highest rate held in every pass: PostgreSQL 4,000/s, every written rate, so the ceiling is above the ladder; SQL Server 1,000/s, every written rate, so the ceiling is above the ladder; SQLite 120/s, every written rate, so the ceiling is above the ladder.

### Operator queries during processing

While the stream runs, do the operator list queries answer quickly, and does the stream keep its rate?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; workers learn of new work: in process; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Disk write, mean / p95 | Result |
|---|---:|---:|---:|---:|---:|---|
| PostgreSQL | 5,000 | 1 | 5,001 | 55 ms | 0.0379 / 0.0463 ms | held |
| SQL Server | 2,500 | 1 | 2,498 | 88 ms | 0.0774 / 0.583 ms | held |
| SQLite | 120 | 1 | 120 | 84 ms | 0.0428 / 0.174 ms | held |

Highest rate held in every pass: PostgreSQL 5,000/s, every written rate, so the ceiling is above the ladder; SQL Server 2,500/s, every written rate, so the ceiling is above the ladder; SQLite 120/s, every written rate, so the ceiling is above the ladder.

### Parked work alongside

With a large population of jobs parked on signals, does the stream hold its rate with p99 under 2 s?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; workers learn of new work: in process; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Disk write, mean / p95 | Result |
|---|---:|---:|---:|---:|---:|---|
| PostgreSQL | 5,000 | 1 | 5,001 | 54 ms | 0.0368 / 0.0427 ms | held |
| SQL Server | 500 | 1 | 500 | 101 ms | 0.25 / 3.31 ms | held |
| SQLite | 160 | 1 | 160 | 85 ms | 0.0399 / 0.0498 ms | held |

Highest rate held in every pass: PostgreSQL 5,000/s, every written rate, so the ceiling is above the ladder; SQL Server 500/s, every written rate, so the ceiling is above the ladder; SQLite 160/s, every written rate, so the ceiling is above the ladder.

### Future high-priority work alongside

With a large population of high-priority jobs due tomorrow, does the stream hold its rate with p99 under 2 s?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; workers learn of new work: in process; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Disk write, mean / p95 | Result |
|---|---:|---:|---:|---:|---:|---|
| PostgreSQL | 5,000 | 1 | 5,001 | 54 ms | 0.332 / 3.46 ms | held |
| SQL Server | 320 | 1 | 320 | 98 ms | 0.0418 / 0.22 ms | held |
| SQLite | 160 | 1 | 160 | 85 ms | 0.0379 / 0.11 ms | held |

Highest rate held in every pass: PostgreSQL 5,000/s, every written rate, so the ceiling is above the ladder; SQL Server 320/s, every written rate, so the ceiling is above the ladder; SQLite 160/s, every written rate, so the ceiling is above the ladder.

### Ten children and a join

Do parents that fan out ten children and join them hold their offered rate with p99 under 10 s?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; workers learn of new work: in process; p99 target: 10.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Disk write, mean / p95 | Result |
|---|---:|---:|---:|---:|---:|---|
| PostgreSQL | 250 | 1 | 249 | 338 ms | 0.0276 / 0.0458 ms | held |
| SQL Server | 200 | 1 | 200 | 347 ms | 0.0883 / 0.482 ms | held |
| SQLite | 8 | 1 | 8 | 213 ms | 0.0328 / 0.139 ms | held |

Highest rate held in every pass: PostgreSQL 250/s, every written rate, so the ceiling is above the ladder; SQL Server 200/s, every written rate, so the ceiling is above the ladder; SQLite 8/s, every written rate, so the ceiling is above the ladder.

### Controlled I/O through an idempotent sink

Do jobs that each call an external idempotent sink for 5 ms hold their offered rate with p99 under 5 s?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; workers learn of new work: in process; p99 target: 5.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Disk write, mean / p95 | Result |
|---|---:|---:|---:|---:|---:|---|
| PostgreSQL | 250 | 1 | 250 | 135 ms | 0.0215 / 0.0241 ms | held |
| SQL Server | 200 | 1 | 200 | 299 ms | 0.0247 / 0.0259 ms | held |
| SQLite | 80 | 1 | 80.6 | 351 ms | 0.0455 / 0.0642 ms | held |

Highest rate held in every pass: PostgreSQL 250/s, every written rate, so the ceiling is above the ladder; SQL Server 200/s, every written rate, so the ceiling is above the ladder; SQLite 80/s, every written rate, so the ceiling is above the ladder.

### Short and long work mixed

Do no-op, CPU, sink, timer and fan-out jobs mixed in turn hold their offered rate with p99 under 5 s?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; workers learn of new work: in process; p99 target: 5.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Disk write, mean / p95 | Result |
|---|---:|---:|---:|---:|---:|---|
| PostgreSQL | 600 | 1 | 600 | 1.1 s | 0.0295 / 0.0789 ms | held |
| SQL Server | 500 | 1 | 500 | 1.1 s | 0.0464 / 1.46 ms | held |
| SQLite | 80 | 1 | 82.7 | 3.5 s | 0.0321 / 0.054 ms | held |

Highest rate held in every pass: PostgreSQL 600/s, every written rate, so the ceiling is above the ladder; SQL Server 500/s, every written rate, so the ceiling is above the ladder; SQLite 80/s, every written rate, so the ceiling is above the ladder.

### Throughput, one executor

Does a worker with a single executor keep up with a modest stream, so the result shows its serial cost?

Setup: workers: 1; namespaces: 1; executors per worker: 1; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; workers learn of new work: in process; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Disk write, mean / p95 | Result |
|---|---:|---:|---:|---:|---:|---|
| PostgreSQL | 150 | 1 | 150 | 29 ms | 0.0228 / 0.0468 ms | held |
| SQL Server | 150 | 1 | 151 | 108 ms | 0.0637 / 0.536 ms | held |
| SQLite | 100 | 1 | 99.9 | 45 ms | 0.0359 / 0.0956 ms | held |

Highest rate held in every pass: PostgreSQL 150/s, every written rate, so the ceiling is above the ladder; SQL Server 150/s, every written rate, so the ceiling is above the ladder; SQLite 100/s, every written rate, so the ceiling is above the ladder.

### One hot lane

How fast does one ordered lane hand its jobs over, each waiting for the previous completion to commit?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; workers learn of new work: in process; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Disk write, mean / p95 | Result |
|---|---:|---:|---:|---:|---:|---|
| PostgreSQL | 60 | 1 | 60 | 24 ms | 0.0267 / 0.0593 ms | held |
| PostgreSQL | 120 | 1 | 120 | 90 ms | 0.0229 / 0.0261 ms | held |
| PostgreSQL | 180 | 1 | 168 | 8.7 s | 0.0224 / 0.0252 ms | missed: backlog grew 14.8 jobs/s, 1.8 allowed |
| PostgreSQL | 220 | | | | | not run by rule |
| PostgreSQL | 260 | | | | | not run by rule |
| SQL Server | 100 | 1 | 100 | 32 ms | 0.17 / 1.6 ms | held |
| SQL Server | 150 | 1 | 150 | 35 ms | 0.022 / 0.0246 ms | held |
| SQL Server | 200 | 1 | 200 | 258 ms | 0.0221 / 0.0269 ms | held |
| SQL Server | 250 | 1 | 117 | 29.8 s | 0.0506 / 0.246 ms | missed: backlog grew 144 jobs/s, 2.5 allowed |
| SQL Server | 300 | | | | | not run by rule |
| SQLite | 80 | 1 | 80 | 63 ms | 0.0421 / 0.114 ms | held |
| SQLite | 120 | 1 | 97.5 | 6.7 s | 0.0368 / 0.0428 ms | missed: backlog grew 23 jobs/s, 1.2 allowed |
| SQLite | 160 | | | | | not run by rule |
| SQLite | 200 | | | | | not run by rule |
| SQLite | 240 | | | | | not run by rule |

Highest rate held in every pass: PostgreSQL 120/s; SQL Server 200/s; SQLite 80/s.

### One hot concurrency key

With every job sharing one concurrency key of limit one, does a stream of one a second keep a flat backlog with p99 under 30 s?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; workers learn of new work: in process; p99 target: 30.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Disk write, mean / p95 | Result |
|---|---:|---:|---:|---:|---:|---|
| PostgreSQL | 1 | 1 | 1 | 39 ms | 0.668 / 2.79 ms | held |
| SQL Server | 1 | 1 | 1 | 50 ms | 0.724 / 2.92 ms | held |
| SQLite | 4 | 1 | 4 | 46 ms | 0.13 / 0.807 ms | held |

Highest rate held in every pass: PostgreSQL 1/s, every written rate, so the ceiling is above the ladder; SQL Server 1/s, every written rate, so the ceiling is above the ladder; SQLite 4/s, every written rate, so the ceiling is above the ladder.

### Five-minute mixed soak

Does the mixed workload hold a steady rate for five minutes with p99 under 5 s and a flat backlog?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; workers learn of new work: in process; p99 target: 5.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Disk write, mean / p95 | Result |
|---|---:|---:|---:|---:|---:|---|
| PostgreSQL | 700 | 1 | 700 | 1.2 s | 0.09 / 0.479 ms | held |
| SQL Server | 350 | 1 | 350 | 1.1 s | 0.0936 / 0.408 ms | held |
| SQLite | 56 | 1 | 55.9 | 1.6 s | 0.0412 / 0.0534 ms | held |

Highest rate held in every pass: PostgreSQL 700/s, every written rate, so the ceiling is above the ladder; SQL Server 350/s, every written rate, so the ceiling is above the ladder; SQLite 56/s, every written rate, so the ceiling is above the ladder.

### Overload, then recovery

Offered more than it can run, does the backlog grow, and does it fall back once the offered rate drops?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Disk write, mean / p95 | Result |
|---|---:|---:|---:|---:|---:|---|
| PostgreSQL | 13,000, then 2,500 | 1 | 7,749 | 9.7 s | 0.139 / 0.0524 ms | pass: the backlog grew, then fell back |
| SQL Server | 8,000, then 1,000 | 1 | 4,500 | 11.6 s | 0.0323 / 0.0672 ms | pass: the backlog grew, then fell back |
| SQLite | | | | | | SQLite's one writer serves the enqueues and the worker alike, so an offer past what it absorbs queues in the caller, not in the database. |

## Correctness

| Scenario | PostgreSQL | SQL Server | SQLite |
|---|---|---|---|
| Checkpoint depths of 1, 10 and 100 steps | pass | pass | pass |
| Wide fan-out of 1000 children | pass | pass | pass |
| Nested tree five levels deep | pass | pass | pass |
| One failing child among ten | pass | pass | pass |
| Children in another namespace | pass | pass | pass |
| Signal sent before the wait | pass | pass | pass |
| Signal sent after the wait | pass | pass | pass |
| Same signal sent twice | pass | pass | pass |
| Poison jobs beside healthy ones | pass | pass | pass |
| Order across 1000 lanes | pass | pass | pass |
| Order in one hot lane | pass | pass | pass |
| One-slot concurrency key | pass | pass | pass |
| Priority across tenants | pass | pass | pass |
| Rate limit shared by two workers | pass | pass | pass |
| New work beside retained history | pass | pass | pass |
| Retention purges during traffic | pass | pass | pass |
| Retention of a waiting parent's children | pass | pass | pass |
| Enqueue in a committed transaction | pass | pass | pass |
| Enqueue in a rolled-back transaction | pass | pass | pass |
| Compatible step result change | pass | pass | pass |
| Incompatible input type change | pass | pass | pass |
| Schema upgrade from a supported release | skipped | skipped | skipped |

## Faults

| Scenario | PostgreSQL | SQL Server | SQLite |
|---|---|---|---|
| Crash after a checkpoint commits | pass | pass | pass |
| Crash after an effect, before its checkpoint | pass | pass | pass |
| Crash inside a step with a check | pass | pass | pass |
| Crash inside an at-most-once step | pass | pass | pass |
| Parent dies during fan-out | pass | pass | pass |
| Last child against the join | pass | pass | pass |
| Cancelling a parent during fan-out | pass | pass | pass |
| Durable timer across a worker restart | pass | pass | pass |
| Signal sent during wait registration | pass | pass | pass |
| Signal against a wait timeout | pass | pass | pass |
| Timed-out attempt that keeps running | pass | pass | pass |
| Kill a busy worker | pass | pass | pass |
| Kill every worker | pass | pass | pass |
| Worker paused past its lease | pass | pass | pass |
| Database connections cut | pass | pass | skipped |
| Database process crash | pass | pass | skipped |
| Lost reply to a completion | pass | pass | pass |
| Lost reply to an enqueue | pass | pass | pass |
| Wake signals dropped | pass | pass | pass |
| Outbox relay crash | pass | pass | pass |
| Keyless outbox records delivered again | pass | pass | pass |
| Non-idempotent effect, crash before completion | pass | pass | pass |
| Idempotent effect, crash before its checkpoint | pass | pass | pass |
| Rolling restart | pass | pass | pass |

## Chaos

| Scenario | PostgreSQL | SQL Server | SQLite |
|---|---|---|---|
| Worker kills under mixed load | pass | pass | pass |

## How this run was made

- Command: `acta-verify release --db all`.
- Commit `0907b3af454e00a32d9f97172dba93dbdb609350`, measurement version 3, written standard `3eb98b78633c`.
- Machine: Intel(R) Core(TM) i9-14900K, 24 cores, 32 logical processors, Microsoft Windows 10.0.26300, .NET 10.0.15.
- Docker VM: 32 CPUs, 46.9 GB; measured containers capped at PostgreSQL 32031 MB, SQL Server 32031 MB.
- PostgreSQL: peak 483 database connections; disk before measuring: quiet before 28 of 33 cells.
- SQL Server: peak 518 database connections; disk before measuring: quiet before 6 of 33 cells.
- SQLite: peak 0 database connections; disk before measuring: quiet before 23 of 25 cells.
- Wall time: performance on PostgreSQL 2 h 20 min, correctness on PostgreSQL 6 min, chaos on PostgreSQL 20 min, performance on SQL Server 2 h 31 min, correctness on SQL Server 11 min, chaos on SQL Server 47 min, performance on SQLite 1 h 24 min, correctness on SQLite 4 min, chaos on SQLite 7 min; correctness runs on every database at once, chaos one database at a time.
- Evidence: 30.1 MB of a 50 MB budget.
- A rate holds when every scheduled arrival reached an outcome and none was refused, the backlog trend stayed within 1% of the rate, p99 stayed within the target, at most 0.1% of arrivals were picked up late by the harness, and every job reconciled. These are the rc.6 criteria.
- p99 runs from an arrival's scheduled instant to its job's durable completion, or to the enqueue call's answer in an enqueue-only scenario. A scenario that streams arrivals to one worker in one namespace runs that worker in the process that enqueues, as a single Acta host does: it shares that process's heap and thread pool and the enqueue calls' connection pool, and each enqueue wakes it at once. A worker process claims again while its claims return jobs; after an empty claim it waits up to its 1 s safety poll, or until Redis wakes it where the setup says so.

| Disk, by phase and drive | Windows counters |
|---|---|
| before \| 0 c: | write 1.82 ms mean, 7.84 ms p95, 9 writes/s, 99% idle, 14 samples |
| before \| 1 d: | write 5.65 ms mean, 7.77 ms p95, 31 writes/s, 99% idle, 14 samples |
| chaos mssql \| 0 c: | write 2.80 ms mean, 8.13 ms p95, 7 writes/s, 99% idle, 1422 samples |
| chaos mssql \| 1 d: | write 0.19 ms mean, 0.85 ms p95, 560 writes/s, 72% idle, 1422 samples |
| chaos pg \| 0 c: | write 2.65 ms mean, 7.98 ms p95, 5 writes/s, 99% idle, 614 samples |
| chaos pg \| 1 d: | write 0.06 ms mean, 0.07 ms p95, 801 writes/s, 52% idle, 614 samples |
| chaos sqlite \| 0 c: | write 3.09 ms mean, 8.26 ms p95, 6 writes/s, 99% idle, 214 samples |
| chaos sqlite \| 1 d: | write 0.39 ms mean, 2.13 ms p95, 1242 writes/s, 47% idle, 214 samples |
| correctness \| 0 c: | write 3.00 ms mean, 7.73 ms p95, 6 writes/s, 99% idle, 331 samples |
| correctness \| 1 d: | write 0.34 ms mean, 1.98 ms p95, 459 writes/s, 83% idle, 331 samples |
| mssql \| 0 c: | write 2.40 ms mean, 7.58 ms p95, 36 writes/s, 99% idle, 4524 samples |
| mssql \| 1 d: | write 1.39 ms mean, 6.88 ms p95, 1219 writes/s, 69% idle, 4524 samples |
| pg \| 0 c: | write 2.62 ms mean, 7.69 ms p95, 18 writes/s, 99% idle, 4188 samples |
| pg \| 1 d: | write 1.10 ms mean, 6.77 ms p95, 689 writes/s, 72% idle, 4188 samples |
| sqlite \| 0 c: | write 2.07 ms mean, 7.67 ms p95, 12 writes/s, 99% idle, 2533 samples |
| sqlite \| 1 d: | write 0.66 ms mean, 6.30 ms p95, 716 writes/s, 80% idle, 2533 samples |
| after \| 0 c: | write 2.87 ms mean, 7.87 ms p95, 7 writes/s, 99% idle, 15 samples |
| after \| 1 d: | write 4.26 ms mean, 7.25 ms p95, 5 writes/s, 100% idle, 15 samples |
