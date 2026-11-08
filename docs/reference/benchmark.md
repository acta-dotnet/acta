# Acta 1.0.0-rc.9: benchmark and release validation

The 1.0.0-rc.9 release evidence, measured by the project on its own machine: one Acta Verify release run on `096ea16e5` on PostgreSQL, SQL Server, and SQLite, and four reruns of the one cell it missed. Every correctness, fault, and chaos scenario passed, every trial's ledger passed the ledger checks, and every performance floor held but one: sixteen SQL Server workers over two namespaces at 4,000/s offered.

- That cell missed in the run below (p99 2.2 s against a 2.0 s target) and in the first rerun (the backlog grew 62.8 jobs/s, 40 allowed), then held in the three reruns after it: 4,062/s with p99 1.9 s, 4,127/s with p99 1.0 s, and 4,001/s with p99 999 ms. Every pass completed about 4,000 jobs a second; what differs between them is short stalls, up to about 0.4 s, in which enqueues wait, sampled mostly on SQL Server's throttle for inserts into the newest page of `jobs` and `jobs_state`. The known limitations name it.
- The reruns ran `acta-verify run throughput-16-workers --suite release --db mssql` from the same clean worktree of `096ea16e5`. The commits after it change the site and how Acta Verify frames the site's benchmark page, not the engine, so this evidence stands for the certified commit `7c4dae04c`.
- This is measurement version 3, as in rc.8. `acta-verify compare` sets no scenario beside rc.8's run, because the .NET runtime differs; the headline rates moved within a few percent: PostgreSQL 12,527 jobs/s against 12,438, its drain 73,864 against 73,211, SQL Server 4,999 against 4,972.

# Acta Verify: FAIL

The release suite on PostgreSQL, SQL Server, SQLite, commit `096ea16e5`, started 2026-11-08 09:38 UTC, 7 h 1 min, on Intel(R) Core(TM) i9-14900K.

| | PostgreSQL | SQL Server | SQLite |
|---|---|---|---|
| Correctness | pass | pass | pass |
| Crash-recovery chaos | pass, 1,000,000 jobs, 84 busy kills reclaimed | pass, 1,000,000 jobs, 81 busy kills reclaimed | pass, 10,000 jobs, 32 busy kills reclaimed |
| Ledger checks | pass, 163 trials checked | pass, 147 trials checked | pass, 125 trials checked |
| Performance floors | held | missed 1 | held |
| Drain | 73,864 jobs/s | 42,768 jobs/s | 8,267 jobs/s |
| Throughput | 12,527 jobs/s | 4,999 jobs/s | 250 jobs/s |
| Throughput, one worker | 7,500 jobs/s | 5,007 jobs/s | not written |

> **12,527 fully executed, durably completed jobs per second** on PostgreSQL: 4 workers polling for work, one job per enqueue call, held 3 × 30 s and 120 s at 12,500/s offered, p99 1.0 s, Intel(R) Core(TM) i9-14900K.

> **4,999 fully executed, durably completed jobs per second** on SQL Server: 4 workers polling for work, one job per enqueue call, held 3 × 30 s and 120 s at 5,000/s offered, p99 972 ms, Intel(R) Core(TM) i9-14900K.

> **250 fully executed, durably completed jobs per second** on SQLite: 1 worker, woken in process, one job per enqueue call, held 3 × 30 s and 120 s at 250/s offered, p99 108 ms, Intel(R) Core(TM) i9-14900K.

> **7,500 fully executed, durably completed jobs per second** on PostgreSQL: 1 worker, woken in process, one job per enqueue call, held 3 × 30 s and 120 s at 7,500/s offered, p99 49 ms, Intel(R) Core(TM) i9-14900K.

> **5,007 fully executed, durably completed jobs per second** on SQL Server: 1 worker, woken in process, one job per enqueue call, held 3 × 30 s and 120 s at 5,000/s offered, p99 1.1 s, Intel(R) Core(TM) i9-14900K.

## Rates to raise

These written rates ask less than the database ran; they decide nothing in this run.

- **Enqueue, one job per call** on SQLite: the floor of 200/s also held at 800/s.
- **Enqueue in batches of 100** on PostgreSQL: the floor of 10,000/s also held at 40,000/s.
- **Enqueue in batches of 1,000** on PostgreSQL: the floor of 20,000/s also held at 80,000/s.
- **Payloads of 4 KB** on SQLite: the floor of 120/s also held at 480/s.
- **Payloads of 256 B, 4 KB and 64 KB mixed** on SQLite: the floor of 160/s also held at 640/s.
- **Payloads of 64 KB** on SQLite: the floor of 40/s also held at 160/s.
- **A thousand lanes** on SQL Server: the floor of 1,000/s also held at 4,000/s.
- **Parked work alongside** on SQL Server: the floor of 500/s also held at 2,000/s.
- **Parked work alongside** on SQLite: the floor of 160/s also held at 640/s.
- **Future high-priority work alongside** on SQL Server: the floor of 320/s also held at 1,280/s.
- **Future high-priority work alongside** on SQLite: the floor of 160/s also held at 640/s.
- **Ten children and a join** on SQLite: the floor of 8/s also held at 32/s.
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
| PostgreSQL | 1 | 1,000,000 | 88,017 jobs/s | 12 s | 0.0903 / 0.989 ms | held |
| PostgreSQL | 2 | 1,000,000 | 73,864 jobs/s | 14.5 s | 5.64 / 28.5 ms | held |
| PostgreSQL | 3 | 1,000,000 | 70,663 jobs/s | 14.8 s | 8.38 / 34.5 ms | held |
| SQL Server | 1 | 1,000,000 | 43,741 jobs/s | 25.6 s | 0.935 / 0.626 ms | held |
| SQL Server | 2 | 1,000,000 | 42,768 jobs/s | 24.7 s | 1.15 / 1.31 ms | held |
| SQL Server | 3 | 1,000,000 | 39,439 jobs/s | 27.4 s | 5.07 / 1.69 ms | held |
| SQLite | 1 | 100,000 | 8,279 jobs/s | 12.3 s | 0.0396 / 0.181 ms | held |
| SQLite | 2 | 100,000 | 8,267 jobs/s | 12.3 s | 0.0398 / 0.135 ms | held |
| SQLite | 3 | 100,000 | 6,258 jobs/s | 16 s | 0.041 / 0.061 ms | held |

### Sustained throughput

How many jobs per second, one per enqueue call, does Acta enqueue, run and durably complete with a flat backlog and p99 under 2 s?

Setup: workers: 4 on PostgreSQL, 4 on SQL Server, 1 on SQLite; namespaces: 2 on PostgreSQL, 2 on SQL Server, 1 on SQLite; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; workers learn of new work: by polling on PostgreSQL, by polling on SQL Server, in process on SQLite; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Disk write, mean / p95 | Result |
|---|---:|---:|---:|---:|---:|---|
| PostgreSQL | 10,000 | 1 | 9,775 | 1.0 s | 0.042 / 0.0556 ms | held |
| PostgreSQL | 10,000 | 2 | 10,018 | 1.0 s | 0.0378 / 0.0495 ms | held |
| PostgreSQL | 10,000 | 3 | 10,175 | 1.0 s | 0.244 / 3.64 ms | held |
| PostgreSQL | 12,500 | 1 | 12,713 | 999 ms | 0.058 / 0.341 ms | held |
| PostgreSQL | 12,500 | 2 | 12,555 | 1.0 s | 0.112 / 1.39 ms | held |
| PostgreSQL | 12,500 | 3 | 12,341 | 1.0 s | 0.0398 / 0.0744 ms | held |
| PostgreSQL | 15,000 | 1 | 15,275 | 983 ms | 0.281 / 4.83 ms | held |
| PostgreSQL | 15,000 | 2 | 15,102 | 988 ms | 0.271 / 4.59 ms | held |
| PostgreSQL | 15,000 | 3 | 15,011 | 1.0 s | 0.0402 / 0.0415 ms | held |
| PostgreSQL | 17,500 | 1 | 17,815 | 1.0 s | 0.152 / 1.52 ms | held |
| PostgreSQL | 17,500 | 2 | 17,649 | 1.3 s | 0.103 / 0.813 ms | held |
| PostgreSQL | 17,500 | 3 | 17,356 | 1.0 s | 0.383 / 6.41 ms | missed: backlog grew 250 jobs/s, 175 allowed |
| PostgreSQL | 20,000 | 1 | 17,577 | 5.6 s | 0.366 / 0.0664 ms | missed: backlog grew 2,550 jobs/s, 200 allowed |
| PostgreSQL | 20,000 | 2 | 17,909 | 5.0 s | 0.0625 / 0.0476 ms | missed: backlog grew 2,190 jobs/s, 200 allowed |
| PostgreSQL | 25,000 | | | | | not run by rule |
| PostgreSQL | 30,000 | | | | | not run by rule |
| PostgreSQL | 15,000 | confirm, 120 s | 14,756 | 2.2 s | 0.162 / 0.599 ms | missed: p99 2.2 s against a 2.0 s target |
| PostgreSQL | 12,500 | confirm, 120 s | 12,527 | 1.0 s | 0.0808 / 0.122 ms | held |
| SQL Server | 5,000 | 1 | 5,016 | 951 ms | 0.0426 / 0.22 ms | held |
| SQL Server | 5,000 | 2 | 5,006 | 901 ms | 0.292 / 12 ms | held |
| SQL Server | 5,000 | 3 | 4,998 | 906 ms | 0.296 / 9.93 ms | held |
| SQL Server | 7,500 | 1 | 6,431 | 5.6 s | 0.101 / 0.455 ms | missed: backlog grew 1,056 jobs/s, 75 allowed |
| SQL Server | 7,500 | 2 | 6,536 | 5.6 s | 0.158 / 0.516 ms | missed: backlog grew 923 jobs/s, 75 allowed |
| SQL Server | 7,500 | 3 | 6,893 | 4.0 s | 0.156 / 1.34 ms | missed: backlog grew 554 jobs/s, 75 allowed |
| SQL Server | 10,000 | | | | | not run by rule |
| SQL Server | 12,500 | | | | | not run by rule |
| SQL Server | 15,000 | | | | | not run by rule |
| SQL Server | 5,000 | confirm, 120 s | 4,999 | 972 ms | 0.458 / 1.43 ms | held |
| SQLite | 250 | 1 | 250 | 106 ms | 0.0321 / 0.0531 ms | held |
| SQLite | 250 | 2 | 250 | 108 ms | 0.0321 / 0.0332 ms | held |
| SQLite | 250 | 3 | 250 | 104 ms | 0.0318 / 0.0398 ms | held |
| SQLite | 500 | 1 | 500 | 83 ms | 0.0247 / 0.0344 ms | held |
| SQLite | 500 | 2 | 328 | 16.8 s | 0.0303 / 0.0327 ms | missed: backlog grew 169 jobs/s, 5 allowed |
| SQLite | 500 | 3 | 331 | 15.2 s | 0.0294 / 0.0317 ms | missed: backlog grew 168 jobs/s, 5 allowed |
| SQLite | 1,000 | 1 | 752 | 22.6 s | 0.0254 / 0.0327 ms | missed: backlog grew 201 jobs/s, 10 allowed |
| SQLite | 2,000 | | | | | not run by rule |
| SQLite | 250 | confirm, 120 s | 250 | 108 ms | 0.0313 / 0.0327 ms | held |

Highest rate held in every pass: PostgreSQL 15,000/s; SQL Server 5,000/s; SQLite 250/s.

### Sustained throughput, woken through Redis

With Redis carrying each enqueue's wake to the worker processes, how many jobs per second, one per enqueue call, does Acta enqueue, run and durably complete with a flat backlog and p99 under 2 s?

Setup: workers: 4 on PostgreSQL, 4 on SQL Server, 1 on SQLite; namespaces: 2 on PostgreSQL, 2 on SQL Server, 1 on SQLite; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; workers learn of new work: through Redis on PostgreSQL, through Redis on SQL Server, in process on SQLite; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Disk write, mean / p95 | Result |
|---|---:|---:|---:|---:|---:|---|
| PostgreSQL | 10,000 | 1 | 9,999 | 178 ms | 0.038 / 0.0485 ms | held |
| PostgreSQL | 12,500 | 1 | 12,507 | 97 ms | 0.0487 / 0.241 ms | held |
| PostgreSQL | 15,000 | 1 | 14,999 | 95 ms | 0.0529 / 0.0614 ms | held |
| PostgreSQL | 17,500 | 1 | 17,699 | 559 ms | 0.284 / 3.53 ms | held |
| PostgreSQL | 20,000 | 1 | 17,316 | 6.0 s | 0.0515 / 0.052 ms | missed: backlog grew 2,665 jobs/s, 200 allowed |
| PostgreSQL | 25,000 | | | | | not run by rule |
| PostgreSQL | 30,000 | | | | | not run by rule |
| SQL Server | 5,000 | 1 | 4,994 | 153 ms | 0.357 / 6.6 ms | held |
| SQL Server | 7,500 | 1 | 6,185 | 7.7 s | 0.0965 / 0.544 ms | missed: backlog grew 1,276 jobs/s, 75 allowed |
| SQL Server | 10,000 | | | | | not run by rule |
| SQL Server | 12,500 | | | | | not run by rule |
| SQL Server | 15,000 | | | | | not run by rule |
| SQLite | | | | | | SQLite runs its one worker in the controller's process, where an enqueue wakes it without a transport. |

Highest rate held in every pass: PostgreSQL 17,500/s; SQL Server 5,000/s.

### Sustained throughput, one worker

How many jobs per second does one worker of default executors, in the process that enqueues, run and durably complete, one job per enqueue call, with a flat backlog and p99 under 2 s?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; workers learn of new work: in process; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Disk write, mean / p95 | Result |
|---|---:|---:|---:|---:|---:|---|
| PostgreSQL | 5,000 | 1 | 5,001 | 52 ms | 0.0339 / 0.0353 ms | held |
| PostgreSQL | 5,000 | 2 | 5,001 | 55 ms | 0.0377 / 0.0646 ms | held |
| PostgreSQL | 5,000 | 3 | 5,000 | 86 ms | 0.0366 / 0.0376 ms | held |
| PostgreSQL | 7,500 | 1 | 7,501 | 42 ms | 0.0334 / 0.0387 ms | held |
| PostgreSQL | 7,500 | 2 | 7,501 | 47 ms | 0.0365 / 0.0416 ms | held |
| PostgreSQL | 7,500 | 3 | 7,501 | 44 ms | 0.0383 / 0.117 ms | held |
| PostgreSQL | 10,000 | 1 | 9,732 | 831 ms | 0.0419 / 0.052 ms | missed: backlog grew 258 jobs/s, 100 allowed |
| PostgreSQL | 10,000 | 2 | 9,920 | 415 ms | 0.0446 / 0.0471 ms | held |
| PostgreSQL | 10,000 | 3 | 9,997 | 43 ms | 0.0378 / 0.0768 ms | held |
| PostgreSQL | 12,500 | 2 | 7,991 | 12.7 s | 0.144 / 0.0509 ms | missed: backlog grew 4,517 jobs/s, 125 allowed |
| PostgreSQL | 12,500 | 3 | 8,606 | 9.6 s | 0.128 / 0.0506 ms | missed: backlog grew 4,302 jobs/s, 125 allowed |
| PostgreSQL | 15,000 | | | | | not run by rule |
| PostgreSQL | 7,500 | confirm, 120 s | 7,500 | 49 ms | 0.0575 / 0.111 ms | held |
| SQL Server | 2,500 | 1 | 2,499 | 87 ms | 0.0519 / 0.437 ms | held |
| SQL Server | 2,500 | 2 | 2,503 | 98 ms | 0.314 / 3.13 ms | held |
| SQL Server | 2,500 | 3 | 2,500 | 96 ms | 0.0456 / 0.886 ms | held |
| SQL Server | 5,000 | 1 | 4,973 | 186 ms | 0.0668 / 0.225 ms | held |
| SQL Server | 5,000 | 2 | 4,907 | 817 ms | 0.413 / 13.4 ms | held |
| SQL Server | 5,000 | 3 | 4,998 | 418 ms | 0.127 / 1.06 ms | held |
| SQL Server | 7,500 | 1 | 6,118 | 8.5 s | 0.145 / 0.635 ms | missed: backlog grew 1,297 jobs/s, 75 allowed |
| SQL Server | 7,500 | 2 | 6,144 | 7.8 s | 0.167 / 0.881 ms | missed: backlog grew 1,285 jobs/s, 75 allowed |
| SQL Server | 7,500 | 3 | 6,127 | 8.6 s | 0.177 / 0.943 ms | missed: backlog grew 1,313 jobs/s, 75 allowed |
| SQL Server | 10,000 | | | | | not run by rule |
| SQL Server | 5,000 | confirm, 120 s | 5,007 | 1.1 s | 0.238 / 1.39 ms | held |
| SQLite | | | | | | The scenario writes no rate for sqlite. |

Highest rate held in every pass: PostgreSQL 7,500/s; SQL Server 5,000/s.

### Enqueue, one job per call

How fast are single-job enqueue calls acknowledged, each in its own transaction, before any worker runs?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; workers learn of new work: by polling; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Disk write, mean / p95 | Result |
|---|---:|---:|---:|---:|---:|---|
| PostgreSQL | 10,000 | 1 | 0 | 25 ms | 0.0396 / 0.0468 ms | held |
| SQL Server | 5,000 | 1 | 0 | 30 ms | 0.0286 / 0.0451 ms | held |
| SQLite | 200 | 1 | 0 | 23 ms | 0.0439 / 0.176 ms | held |

Highest rate held in every pass: PostgreSQL 10,000/s, every written rate, so the ceiling is above the ladder; SQL Server 5,000/s, every written rate, so the ceiling is above the ladder; SQLite 200/s, every written rate, so the ceiling is above the ladder.

### Enqueue in batches of 100

How fast are batches of 100 jobs acknowledged, before any worker runs?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 100; workers learn of new work: by polling; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Disk write, mean / p95 | Result |
|---|---:|---:|---:|---:|---:|---|
| PostgreSQL | 10,000 | 1 | 0 | 38 ms | 0.243 / 0.0421 ms | held |
| SQL Server | 5,000 | 1 | 0 | 47 ms | 2 / 0.16 ms | held |
| SQLite | 5,000 | 1 | 0 | 28 ms | 0.0306 / 0.0352 ms | held |

Highest rate held in every pass: PostgreSQL 10,000/s, every written rate, so the ceiling is above the ladder; SQL Server 5,000/s, every written rate, so the ceiling is above the ladder; SQLite 5,000/s, every written rate, so the ceiling is above the ladder.

### Enqueue in batches of 1,000

How fast are batches of 1,000 jobs acknowledged, before any worker runs?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1,000; workers learn of new work: by polling; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Disk write, mean / p95 | Result |
|---|---:|---:|---:|---:|---:|---|
| PostgreSQL | 20,000 | 1 | 0 | 159 ms | 0.295 / 2.26 ms | held |
| SQL Server | 10,000 | 1 | 0 | 144 ms | 1.2 / 0.0487 ms | held |
| SQLite | 10,000 | 1 | 0 | 78 ms | 0.0593 / 0.143 ms | held |

Highest rate held in every pass: PostgreSQL 20,000/s, every written rate, so the ceiling is above the ladder; SQL Server 10,000/s, every written rate, so the ceiling is above the ladder; SQLite 10,000/s, every written rate, so the ceiling is above the ladder.

### Drain, one worker

How fast does one worker run and durably complete a backlog enqueued before it starts?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 8; jobs per call: 1,000.

| Database | Pass | Jobs | Steady rate, database clock | Whole cohort | Disk write, mean / p95 | Result |
|---|---:|---:|---:|---:|---:|---|
| PostgreSQL | 1 | 200,000 | 17,654 jobs/s | 11.2 s | 0.453 / 1.89 ms | held |
| SQL Server | 1 | 200,000 | 12,336 jobs/s | 16.5 s | 1.53 / 17.1 ms | held |
| SQLite | 1 | 50,000 | 6,488 jobs/s | 7.93 s | 0.0572 / 0.345 ms | held |

### Drain, four workers

How fast do four workers over two namespaces drain a backlog enqueued before they start?

Setup: workers: 4; namespaces: 2; executors per worker: the product default; enqueue calls in flight: 8; jobs per call: 1,000.

| Database | Pass | Jobs | Steady rate, database clock | Whole cohort | Disk write, mean / p95 | Result |
|---|---:|---:|---:|---:|---:|---|
| PostgreSQL | 1 | 200,000 | 58,111 jobs/s | 3.54 s | 0.0606 / 0.211 ms | held |
| SQL Server | 1 | 200,000 | 38,872 jobs/s | 5.13 s | 0.0396 / 0.0899 ms | held |
| SQLite | | | | | | The scenario writes no cohort for sqlite. |

### Drain, sixteen workers

How fast do sixteen workers over two namespaces drain a backlog enqueued before they start?

Setup: workers: 16; namespaces: 2; executors per worker: the product default; enqueue calls in flight: 8; jobs per call: 1,000.

| Database | Pass | Jobs | Steady rate, database clock | Whole cohort | Disk write, mean / p95 | Result |
|---|---:|---:|---:|---:|---:|---|
| PostgreSQL | 1 | 1,000,000 | 74,379 jobs/s | 14.3 s | 0.817 / 5.63 ms | held |
| SQL Server | 1 | 1,000,000 | 47,220 jobs/s | 22.9 s | 0.824 / 0.698 ms | held |
| SQLite | | | | | | The scenario writes no cohort for sqlite. |

### Throughput, sixteen workers

Do sixteen worker processes over two namespaces hold a high offered rate with p99 under 2 s?

Setup: workers: 16; namespaces: 2; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; workers learn of new work: by polling; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Disk write, mean / p95 | Result |
|---|---:|---:|---:|---:|---:|---|
| PostgreSQL | 15,000 | 1 | 15,577 | 1.2 s | 0.0599 / 0.0632 ms | held |
| SQL Server | 4,000 | 1 | 4,355 | 2.2 s | 0.0904 / 4.94 ms | missed: p99 2.2 s against a 2.0 s target |
| SQLite | | | | | | The scenario writes no rate for sqlite. |

Highest rate held in every pass: PostgreSQL 15,000/s, every written rate, so the ceiling is above the ladder; SQL Server none, the floor missed.

### Throughput, sixteen workers, woken through Redis

With Redis carrying each enqueue's wake, do sixteen worker processes over two namespaces hold a high offered rate with p99 under 2 s?

Setup: workers: 16; namespaces: 2; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; workers learn of new work: through Redis; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Disk write, mean / p95 | Result |
|---|---:|---:|---:|---:|---:|---|
| PostgreSQL | 12,500 | 1 | 12,598 | 182 ms | 0.0424 / 0.0634 ms | held |
| SQL Server | 4,000 | 1 | 4,019 | 809 ms | 0.0343 / 0.17 ms | held |
| SQLite | | | | | | SQLite is one process's database; sixteen worker processes are not a SQLite topology. |

Highest rate held in every pass: PostgreSQL 12,500/s, every written rate, so the ceiling is above the ladder; SQL Server 4,000/s, every written rate, so the ceiling is above the ladder.

### Throughput, sixteen workers on one namespace

Do sixteen worker processes sharing one namespace hold a high offered rate with p99 under 2 s?

Setup: workers: 16; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; workers learn of new work: by polling; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Disk write, mean / p95 | Result |
|---|---:|---:|---:|---:|---:|---|
| PostgreSQL | 15,000 | 1 | 15,130 | 292 ms | 0.0478 / 0.14 ms | held |
| SQL Server | 4,000 | 1 | 4,096 | 854 ms | 0.0911 / 2.55 ms | held |
| SQLite | | | | | | The scenario writes no rate for sqlite. |

Highest rate held in every pass: PostgreSQL 15,000/s, every written rate, so the ceiling is above the ladder; SQL Server 4,000/s, every written rate, so the ceiling is above the ladder.

### Throughput, 32 executors

Does one worker with 32 executors hold the stream its default configuration holds?

Setup: workers: 1; namespaces: 1; executors per worker: 32; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; workers learn of new work: in process; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Disk write, mean / p95 | Result |
|---|---:|---:|---:|---:|---:|---|
| PostgreSQL | 5,000 | 1 | 5,001 | 52 ms | 0.0328 / 0.037 ms | held |
| SQL Server | 2,500 | 1 | 2,500 | 113 ms | 0.0253 / 0.0375 ms | held |
| SQLite | 160 | 1 | 160 | 95 ms | 0.042 / 0.178 ms | held |

Highest rate held in every pass: PostgreSQL 5,000/s, every written rate, so the ceiling is above the ladder; SQL Server 2,500/s, every written rate, so the ceiling is above the ladder; SQLite 160/s, every written rate, so the ceiling is above the ladder.

### Three persisted steps

Do jobs that commit three checkpoints each hold their offered rate with p99 under 5 s?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; workers learn of new work: in process; p99 target: 5.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Disk write, mean / p95 | Result |
|---|---:|---:|---:|---:|---:|---|
| PostgreSQL | 1,000 | 1 | 1,000 | 107 ms | 0.0256 / 0.0303 ms | held |
| SQL Server | 1,000 | 1 | 1,001 | 120 ms | 0.0325 / 0.398 ms | held |
| SQLite | 40 | 1 | 40 | 55 ms | 0.0352 / 0.4 ms | held |

Highest rate held in every pass: PostgreSQL 1,000/s, every written rate, so the ceiling is above the ladder; SQL Server 1,000/s, every written rate, so the ceiling is above the ladder; SQLite 40/s, every written rate, so the ceiling is above the ladder.

### Transient failure and retry

With one job in four failing twice before it succeeds, does the stream hold its rate with p99 under 10 s?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; workers learn of new work: in process; p99 target: 10.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Disk write, mean / p95 | Result |
|---|---:|---:|---:|---:|---:|---|
| PostgreSQL | 5,000 | 1 | 5,003 | 2.1 s | 0.0342 / 0.0396 ms | held |
| SQL Server | 2,500 | 1 | 2,502 | 2.2 s | 0.103 / 0.523 ms | held |
| SQLite | 160 | 1 | 160 | 2.2 s | 0.0364 / 0.0578 ms | held |

Highest rate held in every pass: PostgreSQL 5,000/s, every written rate, so the ceiling is above the ladder; SQL Server 2,500/s, every written rate, so the ceiling is above the ladder; SQLite 160/s, every written rate, so the ceiling is above the ladder.

### Fixed CPU work

Do jobs that each hash for a fixed number of rounds hold their offered rate with p99 under 2 s?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; workers learn of new work: in process; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Disk write, mean / p95 | Result |
|---|---:|---:|---:|---:|---:|---|
| PostgreSQL | 4,000 | 1 | 4,003 | 226 ms | 0.0337 / 0.0391 ms | held |
| SQL Server | 2,500 | 1 | 2,498 | 101 ms | 0.105 / 1.88 ms | held |
| SQLite | 160 | 1 | 160 | 92 ms | 0.0363 / 0.06 ms | held |

Highest rate held in every pass: PostgreSQL 4,000/s, every written rate, so the ceiling is above the ladder; SQL Server 2,500/s, every written rate, so the ceiling is above the ladder; SQLite 160/s, every written rate, so the ceiling is above the ladder.

### Payloads of 256 B

Do jobs carrying a 256-byte payload hold their offered rate with p99 under 2 s?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; workers learn of new work: in process; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Disk write, mean / p95 | Result |
|---|---:|---:|---:|---:|---:|---|
| PostgreSQL | 4,000 | 1 | 3,999 | 58 ms | 0.0377 / 0.0383 ms | held |
| SQL Server | 2,500 | 1 | 2,501 | 89 ms | 0.0993 / 0.717 ms | held |
| SQLite | 120 | 1 | 120 | 91 ms | 0.0403 / 0.207 ms | held |

Highest rate held in every pass: PostgreSQL 4,000/s, every written rate, so the ceiling is above the ladder; SQL Server 2,500/s, every written rate, so the ceiling is above the ladder; SQLite 120/s, every written rate, so the ceiling is above the ladder.

### Payloads of 4 KB

Do jobs carrying a 4 KB payload hold their offered rate with p99 under 2 s?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; workers learn of new work: in process; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Disk write, mean / p95 | Result |
|---|---:|---:|---:|---:|---:|---|
| PostgreSQL | 5,000 | 1 | 5,001 | 54 ms | 0.0379 / 0.0476 ms | held |
| SQL Server | 2,500 | 1 | 2,501 | 96 ms | 0.26 / 2.13 ms | held |
| SQLite | 120 | 1 | 120 | 83 ms | 0.0389 / 0.0813 ms | held |

Highest rate held in every pass: PostgreSQL 5,000/s, every written rate, so the ceiling is above the ladder; SQL Server 2,500/s, every written rate, so the ceiling is above the ladder; SQLite 120/s, every written rate, so the ceiling is above the ladder.

### Payloads of 256 B, 4 KB and 64 KB mixed

Do jobs carrying payloads of three sizes in turn hold their offered rate with p99 under 2 s?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; workers learn of new work: in process; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Disk write, mean / p95 | Result |
|---|---:|---:|---:|---:|---:|---|
| PostgreSQL | 2,500 | 1 | 2,500 | 483 ms | 0.125 / 1.33 ms | held |
| SQL Server | 1,000 | 1 | 1,001 | 113 ms | 0.88 / 11.7 ms | held |
| SQLite | 160 | 1 | 160 | 89 ms | 0.0354 / 0.377 ms | held |

Highest rate held in every pass: PostgreSQL 2,500/s, every written rate, so the ceiling is above the ladder; SQL Server 1,000/s, every written rate, so the ceiling is above the ladder; SQLite 160/s, every written rate, so the ceiling is above the ladder.

### Payloads of 64 KB

Do jobs carrying a 64 KB payload hold their offered rate with p99 under 2 s?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; workers learn of new work: in process; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Disk write, mean / p95 | Result |
|---|---:|---:|---:|---:|---:|---|
| PostgreSQL | 1,000 | 1 | 1,000 | 83 ms | 0.749 / 8.41 ms | held |
| SQL Server | 500 | 1 | 501 | 110 ms | 3.6 / 32.7 ms | held |
| SQLite | 40 | 1 | 40 | 30 ms | 0.0448 / 0.0955 ms | held |

Highest rate held in every pass: PostgreSQL 1,000/s, every written rate, so the ceiling is above the ladder; SQL Server 500/s, every written rate, so the ceiling is above the ladder; SQLite 40/s, every written rate, so the ceiling is above the ladder.

### A thousand lanes

Spread over a thousand ordered lanes, does the stream hold its rate with p99 under 2 s?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; workers learn of new work: in process; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Disk write, mean / p95 | Result |
|---|---:|---:|---:|---:|---:|---|
| PostgreSQL | 4,000 | 1 | 4,003 | 58 ms | 0.0476 / 0.139 ms | held |
| SQL Server | 1,000 | 1 | 999 | 104 ms | 5.01 / 44.2 ms | held |
| SQLite | 120 | 1 | 120 | 91 ms | 0.0403 / 0.0425 ms | held |

Highest rate held in every pass: PostgreSQL 4,000/s, every written rate, so the ceiling is above the ladder; SQL Server 1,000/s, every written rate, so the ceiling is above the ladder; SQLite 120/s, every written rate, so the ceiling is above the ladder.

### Operator queries during processing

While the stream runs, do the operator list queries answer quickly, and does the stream keep its rate?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; workers learn of new work: in process; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Disk write, mean / p95 | Result |
|---|---:|---:|---:|---:|---:|---|
| PostgreSQL | 5,000 | 1 | 5,000 | 116 ms | 0.0377 / 0.0447 ms | held |
| SQL Server | 2,500 | 1 | 2,497 | 91 ms | 0.0854 / 5.79 ms | held |
| SQLite | 120 | 1 | 120 | 98 ms | 0.0399 / 0.0668 ms | held |

Highest rate held in every pass: PostgreSQL 5,000/s, every written rate, so the ceiling is above the ladder; SQL Server 2,500/s, every written rate, so the ceiling is above the ladder; SQLite 120/s, every written rate, so the ceiling is above the ladder.

### Parked work alongside

With a large population of jobs parked on signals, does the stream hold its rate with p99 under 2 s?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; workers learn of new work: in process; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Disk write, mean / p95 | Result |
|---|---:|---:|---:|---:|---:|---|
| PostgreSQL | 5,000 | 1 | 5,001 | 55 ms | 0.0748 / 0.639 ms | held |
| SQL Server | 500 | 1 | 500 | 108 ms | 0.0573 / 0.68 ms | held |
| SQLite | 160 | 1 | 160 | 94 ms | 0.0371 / 0.088 ms | held |

Highest rate held in every pass: PostgreSQL 5,000/s, every written rate, so the ceiling is above the ladder; SQL Server 500/s, every written rate, so the ceiling is above the ladder; SQLite 160/s, every written rate, so the ceiling is above the ladder.

### Future high-priority work alongside

With a large population of high-priority jobs due tomorrow, does the stream hold its rate with p99 under 2 s?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; workers learn of new work: in process; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Disk write, mean / p95 | Result |
|---|---:|---:|---:|---:|---:|---|
| PostgreSQL | 5,000 | 1 | 5,001 | 56 ms | 0.558 / 4.09 ms | held |
| SQL Server | 320 | 1 | 320 | 103 ms | 1.55 / 12.5 ms | held |
| SQLite | 160 | 1 | 160 | 92 ms | 0.0344 / 0.0553 ms | held |

Highest rate held in every pass: PostgreSQL 5,000/s, every written rate, so the ceiling is above the ladder; SQL Server 320/s, every written rate, so the ceiling is above the ladder; SQLite 160/s, every written rate, so the ceiling is above the ladder.

### Ten children and a join

Do parents that fan out ten children and join them hold their offered rate with p99 under 10 s?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; workers learn of new work: in process; p99 target: 10.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Disk write, mean / p95 | Result |
|---|---:|---:|---:|---:|---:|---|
| PostgreSQL | 250 | 1 | 251 | 292 ms | 0.0476 / 0.268 ms | held |
| SQL Server | 200 | 1 | 199 | 342 ms | 0.0507 / 0.762 ms | held |
| SQLite | 8 | 1 | 8 | 111 ms | 0.0299 / 0.0652 ms | held |

Highest rate held in every pass: PostgreSQL 250/s, every written rate, so the ceiling is above the ladder; SQL Server 200/s, every written rate, so the ceiling is above the ladder; SQLite 8/s, every written rate, so the ceiling is above the ladder.

### Controlled I/O through an idempotent sink

Do jobs that each call an external idempotent sink for 5 ms hold their offered rate with p99 under 5 s?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; workers learn of new work: in process; p99 target: 5.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Disk write, mean / p95 | Result |
|---|---:|---:|---:|---:|---:|---|
| PostgreSQL | 250 | 1 | 250 | 189 ms | 0.0237 / 0.0481 ms | held |
| SQL Server | 200 | 1 | 200 | 264 ms | 0.0235 / 0.025 ms | held |
| SQLite | 80 | 1 | 80.3 | 329 ms | 0.0373 / 0.114 ms | held |

Highest rate held in every pass: PostgreSQL 250/s, every written rate, so the ceiling is above the ladder; SQL Server 200/s, every written rate, so the ceiling is above the ladder; SQLite 80/s, every written rate, so the ceiling is above the ladder.

### Short and long work mixed

Do no-op, CPU, sink, timer and fan-out jobs mixed in turn hold their offered rate with p99 under 5 s?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; workers learn of new work: in process; p99 target: 5.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Disk write, mean / p95 | Result |
|---|---:|---:|---:|---:|---:|---|
| PostgreSQL | 600 | 1 | 600 | 1.1 s | 0.0274 / 0.213 ms | held |
| SQL Server | 500 | 1 | 500 | 1.1 s | 0.0524 / 0.937 ms | held |
| SQLite | 80 | 1 | 80.5 | 1.2 s | 0.0278 / 0.0695 ms | held |

Highest rate held in every pass: PostgreSQL 600/s, every written rate, so the ceiling is above the ladder; SQL Server 500/s, every written rate, so the ceiling is above the ladder; SQLite 80/s, every written rate, so the ceiling is above the ladder.

### Throughput, one executor

Does a worker with a single executor keep up with a modest stream, so the result shows its serial cost?

Setup: workers: 1; namespaces: 1; executors per worker: 1; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; workers learn of new work: in process; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Disk write, mean / p95 | Result |
|---|---:|---:|---:|---:|---:|---|
| PostgreSQL | 150 | 1 | 150 | 29 ms | 0.0219 / 0.0227 ms | held |
| SQL Server | 150 | 1 | 152 | 398 ms | 0.0307 / 0.127 ms | held |
| SQLite | 100 | 1 | 100 | 36 ms | 0.0331 / 0.128 ms | held |

Highest rate held in every pass: PostgreSQL 150/s, every written rate, so the ceiling is above the ladder; SQL Server 150/s, every written rate, so the ceiling is above the ladder; SQLite 100/s, every written rate, so the ceiling is above the ladder.

### One hot lane

How fast does one ordered lane hand its jobs over, each waiting for the previous completion to commit?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; workers learn of new work: in process; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Disk write, mean / p95 | Result |
|---|---:|---:|---:|---:|---:|---|
| PostgreSQL | 60 | 1 | 60 | 27 ms | 0.0249 / 0.0362 ms | held |
| SQL Server | 100 | 1 | 100 | 45 ms | 0.115 / 1.37 ms | held |
| SQLite | 80 | 1 | 80 | 40 ms | 0.0362 / 0.0523 ms | held |

Highest rate held in every pass: PostgreSQL 60/s, every written rate, so the ceiling is above the ladder; SQL Server 100/s, every written rate, so the ceiling is above the ladder; SQLite 80/s, every written rate, so the ceiling is above the ladder.

### One hot concurrency key

With every job sharing one concurrency key of limit one, does a stream of one a second keep a flat backlog with p99 under 30 s?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; workers learn of new work: in process; p99 target: 30.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Disk write, mean / p95 | Result |
|---|---:|---:|---:|---:|---:|---|
| PostgreSQL | 1 | 1 | 1 | 44 ms | 0.759 / 2.05 ms | held |
| SQL Server | 1 | 1 | 1 | 45 ms | 0.628 / 3.21 ms | held |
| SQLite | 4 | 1 | 4 | 32 ms | 0.131 / 0.851 ms | held |

Highest rate held in every pass: PostgreSQL 1/s, every written rate, so the ceiling is above the ladder; SQL Server 1/s, every written rate, so the ceiling is above the ladder; SQLite 4/s, every written rate, so the ceiling is above the ladder.

### Five-minute mixed soak

Does the mixed workload hold a steady rate for five minutes with p99 under 5 s and a flat backlog?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; workers learn of new work: in process; p99 target: 5.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Disk write, mean / p95 | Result |
|---|---:|---:|---:|---:|---:|---|
| PostgreSQL | 700 | 1 | 700 | 1.2 s | 0.0816 / 0.488 ms | held |
| SQL Server | 350 | 1 | 350 | 1.1 s | 0.0911 / 0.52 ms | held |
| SQLite | 56 | 1 | 56 | 1.1 s | 0.0356 / 0.0437 ms | held |

Highest rate held in every pass: PostgreSQL 700/s, every written rate, so the ceiling is above the ladder; SQL Server 350/s, every written rate, so the ceiling is above the ladder; SQLite 56/s, every written rate, so the ceiling is above the ladder.

### Overload, then recovery

Offered more than it can run, does the backlog grow, and does it fall back once the offered rate drops?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Disk write, mean / p95 | Result |
|---|---:|---:|---:|---:|---:|---|
| PostgreSQL | 13,000, then 2,500 | 1 | 7,750 | 10.5 s | 0.146 / 0.0786 ms | pass: the backlog grew, then fell back |
| SQL Server | 8,000, then 1,000 | 1 | 4,500 | 10.6 s | 0.0608 / 0.199 ms | pass: the backlog grew, then fell back |
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
| Non-idempotent effect, crash before completion | pass | pass | pass |
| Idempotent effect, crash before its checkpoint | pass | pass | pass |
| Rolling restart | pass | pass | pass |

## Chaos

| Scenario | PostgreSQL | SQL Server | SQLite |
|---|---|---|---|
| Worker kills under mixed load | pass | pass | pass |

## What did not pass

- **Throughput, sixteen workers** on SQL Server, fail: Pass 1 missed the floor of 4000/s: p99 2.2 s against a 2.0 s target.

## How this run was made

- Command: `acta-verify release --db all`.
- Commit `096ea16e50e20f18784c8097bfdd2cb75ac76ff9`, measurement version 3, written standard `253e7183162a`.
- Machine: Intel(R) Core(TM) i9-14900K, 24 cores, 32 logical processors, Microsoft Windows 10.0.26300, .NET 10.0.14.
- Docker VM: 32 CPUs, 46.9 GB; measured containers capped at PostgreSQL 32031 MB, SQL Server 32031 MB.
- PostgreSQL: peak 518 database connections; disk before measuring: quiet before 33 of 33 cells.
- SQL Server: peak 524 database connections; disk before measuring: quiet before 27 of 33 cells.
- SQLite: peak 0 database connections; disk before measuring: quiet before 25 of 25 cells.
- Wall time: performance on PostgreSQL 2 h 22 min, correctness on PostgreSQL 6 min, chaos on PostgreSQL 18 min, performance on SQL Server 2 h 6 min, correctness on SQL Server 10 min, chaos on SQL Server 29 min, performance on SQLite 1 h 19 min, correctness on SQLite 5 min, chaos on SQLite 12 min; correctness runs on every database at once, chaos one database at a time.
- Evidence: 30.1 MB of a 50 MB budget.
- A rate holds when every scheduled arrival reached an outcome and none was refused, the backlog trend stayed within 1% of the rate, p99 stayed within the target, at most 0.1% of arrivals were picked up late by the harness, and every job reconciled. These are the rc.6 criteria.
- p99 runs from an arrival's scheduled instant to its job's durable completion, or to the enqueue call's answer in an enqueue-only scenario. A scenario that streams arrivals to one worker in one namespace runs that worker in the process that enqueues, as a single Acta host does: it shares that process's heap and thread pool and the enqueue calls' connection pool, and each enqueue wakes it at once. A worker process claims again while its claims return jobs; after an empty claim it waits up to its 1 s safety poll, or until Redis wakes it where the setup says so.

| Disk, by phase and drive | Windows counters |
|---|---|
| before \| 0 c: | write 1.32 ms mean, 7.77 ms p95, 12 writes/s, 99% idle, 14 samples |
| before \| 1 d: | write 5.15 ms mean, 7.35 ms p95, 10 writes/s, 99% idle, 14 samples |
| chaos mssql \| 0 c: | write 2.29 ms mean, 5.00 ms p95, 10 writes/s, 99% idle, 893 samples |
| chaos mssql \| 1 d: | write 0.27 ms mean, 0.99 ms p95, 899 writes/s, 67% idle, 893 samples |
| chaos pg \| 0 c: | write 2.19 ms mean, 4.78 ms p95, 10 writes/s, 99% idle, 558 samples |
| chaos pg \| 1 d: | write 0.05 ms mean, 0.06 ms p95, 930 writes/s, 45% idle, 558 samples |
| chaos sqlite \| 0 c: | write 1.46 ms mean, 3.30 ms p95, 18 writes/s, 99% idle, 372 samples |
| chaos sqlite \| 1 d: | write 1.60 ms mean, 7.00 ms p95, 510 writes/s, 85% idle, 372 samples |
| correctness \| 0 c: | write 1.88 ms mean, 4.47 ms p95, 15 writes/s, 98% idle, 328 samples |
| correctness \| 1 d: | write 0.31 ms mean, 1.57 ms p95, 494 writes/s, 82% idle, 328 samples |
| mssql \| 0 c: | write 1.79 ms mean, 4.50 ms p95, 21 writes/s, 99% idle, 3791 samples |
| mssql \| 1 d: | write 1.38 ms mean, 6.94 ms p95, 1435 writes/s, 64% idle, 3791 samples |
| pg \| 0 c: | write 2.09 ms mean, 4.99 ms p95, 17 writes/s, 99% idle, 4243 samples |
| pg \| 1 d: | write 1.12 ms mean, 6.67 ms p95, 714 writes/s, 69% idle, 4243 samples |
| sqlite \| 0 c: | write 1.86 ms mean, 4.34 ms p95, 14 writes/s, 99% idle, 2377 samples |
| sqlite \| 1 d: | write 0.66 ms mean, 6.32 ms p95, 748 writes/s, 73% idle, 2377 samples |
| after \| 0 c: | write 1.05 ms mean, 1.96 ms p95, 16 writes/s, 99% idle, 15 samples |
| after \| 1 d: | write 3.79 ms mean, 7.27 ms p95, 4 writes/s, 100% idle, 15 samples |
