# Acta 1.0.0-rc.10: benchmark and release validation

The 1.0.0-rc.10 release evidence, measured by the project on its own machine: one Acta Verify release run on `d20e7c77e` on PostgreSQL, SQL Server, and SQLite. Every correctness, fault, and chaos scenario passed, every trial's ledger passed the ledger checks, and every performance floor held in the run, with no rerun.

- The run's commit is not in the tagged history under that hash: its commit messages were reworded afterwards, which changes every hash and no file. Its tree is `9e26a491cc435c700c72939852f3c75c2e718784`, the tree of the commit "Name 1.0.0-rc.10 in the prose" at the tag. The commits after it change documentation, the site, two queries of one lab, the connection timeout of the conformance suites' test hosts, and the CI workflow, not the engine, so this evidence stands for the tag.
- This is measurement version 3, as in rc.9. `acta-verify compare` sets no scenario beside rc.9's run, because the PostgreSQL container image differs. Read side by side: PostgreSQL held 15,000 jobs a second offered through its 120 s confirmation, which rc.9 missed, and completed 14,857 a second against 12,527; its drain is 83,966 against 73,864. SQL Server completed 5,001 against 4,999, its drain is 50,774 against 42,768, and the sixteen-worker cell rc.9 reran held in the run.

# Acta Verify: PASS

The release suite on PostgreSQL, SQL Server, SQLite, commit `d20e7c77e`, started 2026-11-11 19:12 UTC, 7 h 15 min, on Intel(R) Core(TM) i9-14900K.

| | PostgreSQL | SQL Server | SQLite |
|---|---|---|---|
| Correctness | pass | pass | pass |
| Crash-recovery chaos | pass, 1,000,000 jobs, 84 busy kills reclaimed | pass, 1,000,000 jobs, 81 busy kills reclaimed | pass, 10,000 jobs, 35 busy kills reclaimed |
| Ledger checks | pass, 157 trials checked | pass, 148 trials checked | pass, 125 trials checked |
| Performance floors | held | held | held |
| Drain | 83,966 jobs/s | 50,774 jobs/s | 8,290 jobs/s |
| Throughput | 14,857 jobs/s | 5,001 jobs/s | 250 jobs/s |
| Throughput, one worker | 7,500 jobs/s | 4,999 jobs/s | not written |

> **14,857 fully executed, durably completed jobs per second** on PostgreSQL: 4 workers polling for work, one job per enqueue call, held 3 × 30 s and 120 s at 15,000/s offered, p99 1.6 s, Intel(R) Core(TM) i9-14900K.

> **5,001 fully executed, durably completed jobs per second** on SQL Server: 4 workers polling for work, one job per enqueue call, held 3 × 30 s and 120 s at 5,000/s offered, p99 931 ms, Intel(R) Core(TM) i9-14900K.

> **250 fully executed, durably completed jobs per second** on SQLite: 1 worker, woken in process, one job per enqueue call, held 3 × 30 s and 120 s at 250/s offered, p99 109 ms, Intel(R) Core(TM) i9-14900K.

> **7,500 fully executed, durably completed jobs per second** on PostgreSQL: 1 worker, woken in process, one job per enqueue call, held 3 × 30 s and 120 s at 7,500/s offered, p99 61 ms, Intel(R) Core(TM) i9-14900K.

> **4,999 fully executed, durably completed jobs per second** on SQL Server: 1 worker, woken in process, one job per enqueue call, held 3 × 30 s and 120 s at 5,000/s offered, p99 132 ms, Intel(R) Core(TM) i9-14900K.

## Rates to raise

These written rates ask less than the database ran; they decide nothing in this run.

- **Enqueue in batches of 100** on PostgreSQL: the floor of 10,000/s also held at 40,000/s.
- **Enqueue in batches of 1,000** on PostgreSQL: the floor of 20,000/s also held at 80,000/s.
- **Throughput, 32 executors** on SQLite: the floor of 160/s also held at 640/s.
- **Fixed CPU work** on SQLite: the floor of 160/s also held at 640/s.
- **Payloads of 256 B, 4 KB and 64 KB mixed** on SQLite: the floor of 160/s also held at 640/s.
- **Payloads of 64 KB** on SQLite: the floor of 40/s also held at 160/s.
- **A thousand lanes** on SQL Server: the floor of 1,000/s also held at 4,000/s.
- **A thousand lanes** on SQLite: the floor of 120/s also held at 480/s.
- **Operator queries during processing** on SQLite: the floor of 120/s also held at 480/s.
- **Parked work alongside** on SQL Server: the floor of 500/s also held at 2,000/s.
- **Parked work alongside** on SQLite: the floor of 160/s also held at 640/s.
- **Future high-priority work alongside** on SQL Server: the floor of 320/s also held at 1,280/s.
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
| PostgreSQL | 1 | 1,000,000 | 83,966 jobs/s | 13.5 s | 0.499 / 2.82 ms | held |
| PostgreSQL | 2 | 1,000,000 | 70,795 jobs/s | 15 s | 15.9 / 82.3 ms | held |
| PostgreSQL | 3 | 1,000,000 | 88,711 jobs/s | 12.3 s | 7.15 / 41.8 ms | held |
| SQL Server | 1 | 1,000,000 | 36,028 jobs/s | 28.2 s | 0.157 / 0.307 ms | held |
| SQL Server | 2 | 1,000,000 | 50,774 jobs/s | 23.2 s | 1.32 / 2.42 ms | held |
| SQL Server | 3 | 1,000,000 | 52,189 jobs/s | 19.8 s | 0.152 / 0.925 ms | held |
| SQLite | 1 | 100,000 | 8,359 jobs/s | 12.3 s | 0.0389 / 0.117 ms | held |
| SQLite | 2 | 100,000 | 8,188 jobs/s | 12.2 s | 0.0327 / 0.0438 ms | held |
| SQLite | 3 | 100,000 | 8,290 jobs/s | 12.2 s | 0.0405 / 0.12 ms | held |

### Sustained throughput

How many jobs per second, one per enqueue call, does Acta enqueue, run and durably complete with a flat backlog and p99 under 2 s?

Setup: workers: 4 on PostgreSQL, 4 on SQL Server, 1 on SQLite; namespaces: 2 on PostgreSQL, 2 on SQL Server, 1 on SQLite; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; workers learn of new work: by polling on PostgreSQL, by polling on SQL Server, in process on SQLite; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Disk write, mean / p95 | Result |
|---|---:|---:|---:|---:|---:|---|
| PostgreSQL | 10,000 | 1 | 10,161 | 1.0 s | 0.0498 / 0.0517 ms | held |
| PostgreSQL | 10,000 | 2 | 9,827 | 1.0 s | 0.0385 / 0.0456 ms | held |
| PostgreSQL | 10,000 | 3 | 10,166 | 1.0 s | 0.0513 / 0.0526 ms | held |
| PostgreSQL | 12,500 | 1 | 12,562 | 1.0 s | 0.0428 / 0.0565 ms | held |
| PostgreSQL | 12,500 | 2 | 12,573 | 999 ms | 0.0675 / 0.479 ms | held |
| PostgreSQL | 12,500 | 3 | 12,262 | 1.0 s | 0.0519 / 0.196 ms | held |
| PostgreSQL | 15,000 | 1 | 14,548 | 1.0 s | 0.0395 / 0.0426 ms | held |
| PostgreSQL | 15,000 | 2 | 15,051 | 1.0 s | 0.0404 / 0.0451 ms | held |
| PostgreSQL | 15,000 | 3 | 15,115 | 1.0 s | 0.0513 / 0.198 ms | held |
| PostgreSQL | 17,500 | 1 | 16,431 | 2.8 s | 0.0569 / 0.0663 ms | missed: backlog grew 880 jobs/s, 175 allowed |
| PostgreSQL | 17,500 | 2 | 15,852 | 3.8 s | 0.0583 / 0.0659 ms | missed: backlog grew 1,578 jobs/s, 175 allowed |
| PostgreSQL | 17,500 | 3 | 16,391 | 2.8 s | 0.0571 / 0.0661 ms | missed: backlog grew 1,148 jobs/s, 175 allowed |
| PostgreSQL | 20,000 | | | | | not run by rule |
| PostgreSQL | 25,000 | | | | | not run by rule |
| PostgreSQL | 30,000 | | | | | not run by rule |
| PostgreSQL | 15,000 | confirm, 120 s | 14,857 | 1.6 s | 0.455 / 0.407 ms | held |
| SQL Server | 5,000 | 1 | 4,995 | 967 ms | 0.0329 / 0.131 ms | held |
| SQL Server | 5,000 | 2 | 4,997 | 936 ms | 0.799 / 12 ms | held |
| SQL Server | 5,000 | 3 | 4,953 | 817 ms | 0.279 / 5.8 ms | held |
| SQL Server | 7,500 | 1 | 6,626 | 5.2 s | 0.104 / 0.467 ms | missed: backlog grew 859 jobs/s, 75 allowed |
| SQL Server | 7,500 | 2 | 6,889 | 3.7 s | 0.628 / 0.688 ms | missed: backlog grew 650 jobs/s, 75 allowed |
| SQL Server | 7,500 | 3 | 6,595 | 5.3 s | 0.0626 / 0.303 ms | missed: backlog grew 911 jobs/s, 75 allowed |
| SQL Server | 10,000 | | | | | not run by rule |
| SQL Server | 12,500 | | | | | not run by rule |
| SQL Server | 15,000 | | | | | not run by rule |
| SQL Server | 5,000 | confirm, 120 s | 5,001 | 931 ms | 0.32 / 1.57 ms | held |
| SQLite | 250 | 1 | 250 | 82 ms | 0.0293 / 0.0304 ms | held |
| SQLite | 250 | 2 | 250 | 106 ms | 0.0311 / 0.044 ms | held |
| SQLite | 250 | 3 | 250 | 103 ms | 0.0303 / 0.0409 ms | held |
| SQLite | 500 | 1 | 500 | 85 ms | 0.0243 / 0.0402 ms | held |
| SQLite | 500 | 2 | 425 | 6.1 s | 0.0256 / 0.0301 ms | missed: backlog grew 73.2 jobs/s, 5 allowed |
| SQLite | 500 | 3 | 397 | 11.9 s | 0.0284 / 0.0304 ms | missed: backlog grew 126 jobs/s, 5 allowed |
| SQLite | 1,000 | 1 | 688 | 28.2 s | 0.0259 / 0.0312 ms | missed: backlog grew 298 jobs/s, 10 allowed |
| SQLite | 2,000 | | | | | not run by rule |
| SQLite | 250 | confirm, 120 s | 250 | 109 ms | 0.03 / 0.0309 ms | held |

Highest rate held in every pass: PostgreSQL 15,000/s; SQL Server 5,000/s; SQLite 250/s.

### Sustained throughput, woken through Redis

With Redis carrying each enqueue's wake to the worker processes, how many jobs per second, one per enqueue call, does Acta enqueue, run and durably complete with a flat backlog and p99 under 2 s?

Setup: workers: 4 on PostgreSQL, 4 on SQL Server, 1 on SQLite; namespaces: 2 on PostgreSQL, 2 on SQL Server, 1 on SQLite; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; workers learn of new work: through Redis on PostgreSQL, through Redis on SQL Server, in process on SQLite; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Disk write, mean / p95 | Result |
|---|---:|---:|---:|---:|---:|---|
| PostgreSQL | 10,000 | 1 | 10,006 | 103 ms | 0.04 / 0.049 ms | held |
| PostgreSQL | 12,500 | 1 | 12,503 | 96 ms | 0.116 / 1.44 ms | held |
| PostgreSQL | 15,000 | 1 | 14,970 | 129 ms | 0.0541 / 0.0657 ms | held |
| PostgreSQL | 17,500 | 1 | 16,879 | 1.7 s | 0.613 / 7.96 ms | missed: backlog grew 547 jobs/s, 175 allowed |
| PostgreSQL | 20,000 | | | | | not run by rule |
| PostgreSQL | 25,000 | | | | | not run by rule |
| PostgreSQL | 30,000 | | | | | not run by rule |
| SQL Server | 5,000 | 1 | 5,003 | 198 ms | 0.0804 / 0.476 ms | held |
| SQL Server | 7,500 | 1 | 6,835 | 4.1 s | 0.134 / 0.897 ms | missed: backlog grew 583 jobs/s, 75 allowed |
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
| PostgreSQL | 5,000 | 1 | 5,000 | 52 ms | 0.0391 / 0.26 ms | held |
| PostgreSQL | 5,000 | 2 | 5,001 | 52 ms | 0.0339 / 0.0354 ms | held |
| PostgreSQL | 5,000 | 3 | 5,001 | 59 ms | 0.0375 / 0.039 ms | held |
| PostgreSQL | 7,500 | 1 | 7,495 | 53 ms | 0.0339 / 0.0377 ms | held |
| PostgreSQL | 7,500 | 2 | 7,497 | 41 ms | 0.043 / 0.177 ms | held |
| PostgreSQL | 7,500 | 3 | 7,501 | 187 ms | 0.0403 / 0.0706 ms | held |
| PostgreSQL | 10,000 | 1 | 9,698 | 2.6 s | 0.0459 / 0.0586 ms | missed: backlog grew 580 jobs/s, 100 allowed |
| PostgreSQL | 10,000 | 2 | 8,768 | 3.9 s | 0.0956 / 0.986 ms | missed: backlog grew 1,211 jobs/s, 100 allowed |
| PostgreSQL | 10,000 | 3 | 9,020 | 3.5 s | 0.0461 / 0.0489 ms | missed: backlog grew 920 jobs/s, 100 allowed |
| PostgreSQL | 12,500 | | | | | not run by rule |
| PostgreSQL | 15,000 | | | | | not run by rule |
| PostgreSQL | 7,500 | confirm, 120 s | 7,500 | 61 ms | 0.0476 / 0.0647 ms | held |
| SQL Server | 2,500 | 1 | 2,502 | 89 ms | 0.296 / 10.9 ms | held |
| SQL Server | 2,500 | 2 | 2,498 | 91 ms | 0.258 / 6.59 ms | held |
| SQL Server | 2,500 | 3 | 2,504 | 93 ms | 0.0258 / 0.0403 ms | held |
| SQL Server | 5,000 | 1 | 4,998 | 817 ms | 0.199 / 3.31 ms | held |
| SQL Server | 5,000 | 2 | 5,001 | 97 ms | 0.232 / 13.9 ms | held |
| SQL Server | 5,000 | 3 | 5,001 | 161 ms | 0.269 / 2.99 ms | held |
| SQL Server | 7,500 | 1 | 6,086 | 8.1 s | 0.157 / 0.551 ms | missed: backlog grew 1,212 jobs/s, 75 allowed |
| SQL Server | 7,500 | 2 | 6,383 | 7.4 s | 0.133 / 0.65 ms | missed: backlog grew 1,005 jobs/s, 75 allowed |
| SQL Server | 7,500 | 3 | 6,315 | 7.5 s | 0.249 / 1.04 ms | missed: backlog grew 1,062 jobs/s, 75 allowed |
| SQL Server | 10,000 | | | | | not run by rule |
| SQL Server | 5,000 | confirm, 120 s | 4,999 | 132 ms | 0.199 / 0.669 ms | held |
| SQLite | | | | | | The scenario writes no rate for sqlite. |

Highest rate held in every pass: PostgreSQL 7,500/s; SQL Server 5,000/s.

### Enqueue, one job per call

How fast are single-job enqueue calls acknowledged, each in its own transaction, before any worker runs?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; workers learn of new work: by polling; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Disk write, mean / p95 | Result |
|---|---:|---:|---:|---:|---:|---|
| PostgreSQL | 10,000 | 1 | 0 | 210 ms | 0.0384 / 0.0465 ms | held |
| SQL Server | 5,000 | 1 | 0 | 29 ms | 0.0816 / 0.804 ms | held |
| SQLite | 200 | 1 | 0 | 22 ms | 0.0414 / 0.0438 ms | held |

Highest rate held in every pass: PostgreSQL 10,000/s, every written rate, so the ceiling is above the ladder; SQL Server 5,000/s, every written rate, so the ceiling is above the ladder; SQLite 200/s, every written rate, so the ceiling is above the ladder.

### Enqueue in batches of 100

How fast are batches of 100 jobs acknowledged, before any worker runs?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 100; workers learn of new work: by polling; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Disk write, mean / p95 | Result |
|---|---:|---:|---:|---:|---:|---|
| PostgreSQL | 10,000 | 1 | 0 | 44 ms | 0.132 / 0.0377 ms | held |
| SQL Server | 5,000 | 1 | 0 | 48 ms | 3.48 / 0.0431 ms | held |
| SQLite | 5,000 | 1 | 0 | 28 ms | 0.0354 / 0.0381 ms | held |

Highest rate held in every pass: PostgreSQL 10,000/s, every written rate, so the ceiling is above the ladder; SQL Server 5,000/s, every written rate, so the ceiling is above the ladder; SQLite 5,000/s, every written rate, so the ceiling is above the ladder.

### Enqueue in batches of 1,000

How fast are batches of 1,000 jobs acknowledged, before any worker runs?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1,000; workers learn of new work: by polling; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Disk write, mean / p95 | Result |
|---|---:|---:|---:|---:|---:|---|
| PostgreSQL | 20,000 | 1 | 0 | 202 ms | 0.233 / 0.407 ms | held |
| SQL Server | 10,000 | 1 | 0 | 186 ms | 1.48 / 0.0436 ms | held |
| SQLite | 10,000 | 1 | 0 | 79 ms | 0.0599 / 0.139 ms | held |

Highest rate held in every pass: PostgreSQL 20,000/s, every written rate, so the ceiling is above the ladder; SQL Server 10,000/s, every written rate, so the ceiling is above the ladder; SQLite 10,000/s, every written rate, so the ceiling is above the ladder.

### Drain, one worker

How fast does one worker run and durably complete a backlog enqueued before it starts?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 8; jobs per call: 1,000.

| Database | Pass | Jobs | Steady rate, database clock | Whole cohort | Disk write, mean / p95 | Result |
|---|---:|---:|---:|---:|---:|---|
| PostgreSQL | 1 | 200,000 | 17,260 jobs/s | 11.5 s | 0.26 / 0.932 ms | held |
| SQL Server | 1 | 200,000 | 12,314 jobs/s | 15.9 s | 1.89 / 19.2 ms | held |
| SQLite | 1 | 50,000 | 6,378 jobs/s | 7.94 s | 0.0479 / 0.0876 ms | held |

### Drain, four workers

How fast do four workers over two namespaces drain a backlog enqueued before they start?

Setup: workers: 4; namespaces: 2; executors per worker: the product default; enqueue calls in flight: 8; jobs per call: 1,000.

| Database | Pass | Jobs | Steady rate, database clock | Whole cohort | Disk write, mean / p95 | Result |
|---|---:|---:|---:|---:|---:|---|
| PostgreSQL | 1 | 200,000 | 42,844 jobs/s | 4.72 s | 0.581 / 1.62 ms | held |
| SQL Server | 1 | 200,000 | 38,961 jobs/s | 5.05 s | 0.185 / 0.942 ms | held |
| SQLite | | | | | | The scenario writes no cohort for sqlite. |

### Drain, sixteen workers

How fast do sixteen workers over two namespaces drain a backlog enqueued before they start?

Setup: workers: 16; namespaces: 2; executors per worker: the product default; enqueue calls in flight: 8; jobs per call: 1,000.

| Database | Pass | Jobs | Steady rate, database clock | Whole cohort | Disk write, mean / p95 | Result |
|---|---:|---:|---:|---:|---:|---|
| PostgreSQL | 1 | 1,000,000 | 74,503 jobs/s | 14.3 s | 10.7 / 44.8 ms | held |
| SQL Server | 1 | 1,000,000 | 45,701 jobs/s | 22.9 s | 1.49 / 0.849 ms | held |
| SQLite | | | | | | The scenario writes no cohort for sqlite. |

### Throughput, sixteen workers

Do sixteen worker processes over two namespaces hold a high offered rate with p99 under 2 s?

Setup: workers: 16; namespaces: 2; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; workers learn of new work: by polling; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Disk write, mean / p95 | Result |
|---|---:|---:|---:|---:|---:|---|
| PostgreSQL | 15,000 | 1 | 15,470 | 1.1 s | 0.059 / 0.0648 ms | held |
| SQL Server | 4,000 | 1 | 4,244 | 1.3 s | 0.0674 / 0.573 ms | held |
| SQLite | | | | | | The scenario writes no rate for sqlite. |

Highest rate held in every pass: PostgreSQL 15,000/s, every written rate, so the ceiling is above the ladder; SQL Server 4,000/s, every written rate, so the ceiling is above the ladder.

### Throughput, sixteen workers, woken through Redis

With Redis carrying each enqueue's wake, do sixteen worker processes over two namespaces hold a high offered rate with p99 under 2 s?

Setup: workers: 16; namespaces: 2; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; workers learn of new work: through Redis; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Disk write, mean / p95 | Result |
|---|---:|---:|---:|---:|---:|---|
| PostgreSQL | 12,500 | 1 | 12,656 | 387 ms | 0.0763 / 0.433 ms | held |
| SQL Server | 4,000 | 1 | 4,008 | 353 ms | 0.0312 / 0.0527 ms | held |
| SQLite | | | | | | SQLite is one process's database; sixteen worker processes are not a SQLite topology. |

Highest rate held in every pass: PostgreSQL 12,500/s, every written rate, so the ceiling is above the ladder; SQL Server 4,000/s, every written rate, so the ceiling is above the ladder.

### Throughput, sixteen workers on one namespace

Do sixteen worker processes sharing one namespace hold a high offered rate with p99 under 2 s?

Setup: workers: 16; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; workers learn of new work: by polling; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Disk write, mean / p95 | Result |
|---|---:|---:|---:|---:|---:|---|
| PostgreSQL | 15,000 | 1 | 15,097 | 1.5 s | 0.0704 / 0.216 ms | held |
| SQL Server | 4,000 | 1 | 4,011 | 1.9 s | 0.446 / 7.23 ms | held |
| SQLite | | | | | | The scenario writes no rate for sqlite. |

Highest rate held in every pass: PostgreSQL 15,000/s, every written rate, so the ceiling is above the ladder; SQL Server 4,000/s, every written rate, so the ceiling is above the ladder.

### Throughput, 32 executors

Does one worker with 32 executors hold the stream its default configuration holds?

Setup: workers: 1; namespaces: 1; executors per worker: 32; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; workers learn of new work: in process; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Disk write, mean / p95 | Result |
|---|---:|---:|---:|---:|---:|---|
| PostgreSQL | 5,000 | 1 | 5,001 | 51 ms | 0.0315 / 0.0325 ms | held |
| SQL Server | 2,500 | 1 | 2,507 | 116 ms | 0.0747 / 3.43 ms | held |
| SQLite | 160 | 1 | 160 | 95 ms | 0.0353 / 0.139 ms | held |

Highest rate held in every pass: PostgreSQL 5,000/s, every written rate, so the ceiling is above the ladder; SQL Server 2,500/s, every written rate, so the ceiling is above the ladder; SQLite 160/s, every written rate, so the ceiling is above the ladder.

### Three persisted steps

Do jobs that commit three checkpoints each hold their offered rate with p99 under 5 s?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; workers learn of new work: in process; p99 target: 5.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Disk write, mean / p95 | Result |
|---|---:|---:|---:|---:|---:|---|
| PostgreSQL | 1,000 | 1 | 1,001 | 95 ms | 0.0246 / 0.0439 ms | held |
| SQL Server | 1,000 | 1 | 1,000 | 117 ms | 0.0452 / 0.839 ms | held |
| SQLite | 40 | 1 | 40 | 53 ms | 0.0335 / 0.041 ms | held |

Highest rate held in every pass: PostgreSQL 1,000/s, every written rate, so the ceiling is above the ladder; SQL Server 1,000/s, every written rate, so the ceiling is above the ladder; SQLite 40/s, every written rate, so the ceiling is above the ladder.

### Transient failure and retry

With one job in four failing twice before it succeeds, does the stream hold its rate with p99 under 10 s?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; workers learn of new work: in process; p99 target: 10.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Disk write, mean / p95 | Result |
|---|---:|---:|---:|---:|---:|---|
| PostgreSQL | 5,000 | 1 | 4,997 | 2.1 s | 0.0375 / 0.0444 ms | held |
| SQL Server | 2,500 | 1 | 2,502 | 2.2 s | 0.145 / 1.67 ms | held |
| SQLite | 160 | 1 | 160 | 2.3 s | 0.0341 / 0.052 ms | held |

Highest rate held in every pass: PostgreSQL 5,000/s, every written rate, so the ceiling is above the ladder; SQL Server 2,500/s, every written rate, so the ceiling is above the ladder; SQLite 160/s, every written rate, so the ceiling is above the ladder.

### Fixed CPU work

Do jobs that each hash for a fixed number of rounds hold their offered rate with p99 under 2 s?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; workers learn of new work: in process; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Disk write, mean / p95 | Result |
|---|---:|---:|---:|---:|---:|---|
| PostgreSQL | 4,000 | 1 | 4,001 | 60 ms | 0.034 / 0.0563 ms | held |
| SQL Server | 2,500 | 1 | 2,499 | 95 ms | 0.0631 / 2.53 ms | held |
| SQLite | 160 | 1 | 160 | 80 ms | 0.0327 / 0.0565 ms | held |

Highest rate held in every pass: PostgreSQL 4,000/s, every written rate, so the ceiling is above the ladder; SQL Server 2,500/s, every written rate, so the ceiling is above the ladder; SQLite 160/s, every written rate, so the ceiling is above the ladder.

### Payloads of 256 B

Do jobs carrying a 256-byte payload hold their offered rate with p99 under 2 s?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; workers learn of new work: in process; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Disk write, mean / p95 | Result |
|---|---:|---:|---:|---:|---:|---|
| PostgreSQL | 4,000 | 1 | 4,003 | 57 ms | 0.0371 / 0.0387 ms | held |
| SQL Server | 2,500 | 1 | 2,498 | 90 ms | 0.117 / 0.97 ms | held |
| SQLite | 120 | 1 | 120 | 84 ms | 0.0367 / 0.0556 ms | held |

Highest rate held in every pass: PostgreSQL 4,000/s, every written rate, so the ceiling is above the ladder; SQL Server 2,500/s, every written rate, so the ceiling is above the ladder; SQLite 120/s, every written rate, so the ceiling is above the ladder.

### Payloads of 4 KB

Do jobs carrying a 4 KB payload hold their offered rate with p99 under 2 s?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; workers learn of new work: in process; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Disk write, mean / p95 | Result |
|---|---:|---:|---:|---:|---:|---|
| PostgreSQL | 5,000 | 1 | 5,000 | 87 ms | 0.738 / 9.07 ms | held |
| SQL Server | 2,500 | 1 | 2,498 | 107 ms | 0.505 / 6.4 ms | held |
| SQLite | 120 | 1 | 120 | 82 ms | 0.0369 / 0.0543 ms | held |

Highest rate held in every pass: PostgreSQL 5,000/s, every written rate, so the ceiling is above the ladder; SQL Server 2,500/s, every written rate, so the ceiling is above the ladder; SQLite 120/s, every written rate, so the ceiling is above the ladder.

### Payloads of 256 B, 4 KB and 64 KB mixed

Do jobs carrying payloads of three sizes in turn hold their offered rate with p99 under 2 s?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; workers learn of new work: in process; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Disk write, mean / p95 | Result |
|---|---:|---:|---:|---:|---:|---|
| PostgreSQL | 2,500 | 1 | 2,500 | 519 ms | 0.0458 / 0.0699 ms | held |
| SQL Server | 1,000 | 1 | 999 | 106 ms | 2.69 / 23.7 ms | held |
| SQLite | 160 | 1 | 160 | 86 ms | 0.0346 / 0.0516 ms | held |

Highest rate held in every pass: PostgreSQL 2,500/s, every written rate, so the ceiling is above the ladder; SQL Server 1,000/s, every written rate, so the ceiling is above the ladder; SQLite 160/s, every written rate, so the ceiling is above the ladder.

### Payloads of 64 KB

Do jobs carrying a 64 KB payload hold their offered rate with p99 under 2 s?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; workers learn of new work: in process; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Disk write, mean / p95 | Result |
|---|---:|---:|---:|---:|---:|---|
| PostgreSQL | 1,000 | 1 | 999 | 305 ms | 5.99 / 83.2 ms | held |
| SQL Server | 500 | 1 | 500 | 108 ms | 1.98 / 24.3 ms | held |
| SQLite | 40 | 1 | 40 | 23 ms | 0.0422 / 0.124 ms | held |

Highest rate held in every pass: PostgreSQL 1,000/s, every written rate, so the ceiling is above the ladder; SQL Server 500/s, every written rate, so the ceiling is above the ladder; SQLite 40/s, every written rate, so the ceiling is above the ladder.

### A thousand lanes

Spread over a thousand ordered lanes, does the stream hold its rate with p99 under 2 s?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; workers learn of new work: in process; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Disk write, mean / p95 | Result |
|---|---:|---:|---:|---:|---:|---|
| PostgreSQL | 4,000 | 1 | 3,999 | 60 ms | 0.271 / 3.49 ms | held |
| SQL Server | 1,000 | 1 | 998 | 102 ms | 2.47 / 11.7 ms | held |
| SQLite | 120 | 1 | 120 | 89 ms | 0.0374 / 0.0588 ms | held |

Highest rate held in every pass: PostgreSQL 4,000/s, every written rate, so the ceiling is above the ladder; SQL Server 1,000/s, every written rate, so the ceiling is above the ladder; SQLite 120/s, every written rate, so the ceiling is above the ladder.

### Operator queries during processing

While the stream runs, do the operator list queries answer quickly, and does the stream keep its rate?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; workers learn of new work: in process; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Disk write, mean / p95 | Result |
|---|---:|---:|---:|---:|---:|---|
| PostgreSQL | 5,000 | 1 | 5,001 | 58 ms | 0.259 / 3.46 ms | held |
| SQL Server | 2,500 | 1 | 2,500 | 85 ms | 0.0253 / 0.046 ms | held |
| SQLite | 120 | 1 | 120 | 82 ms | 0.0372 / 0.0631 ms | held |

Highest rate held in every pass: PostgreSQL 5,000/s, every written rate, so the ceiling is above the ladder; SQL Server 2,500/s, every written rate, so the ceiling is above the ladder; SQLite 120/s, every written rate, so the ceiling is above the ladder.

### Parked work alongside

With a large population of jobs parked on signals, does the stream hold its rate with p99 under 2 s?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; workers learn of new work: in process; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Disk write, mean / p95 | Result |
|---|---:|---:|---:|---:|---:|---|
| PostgreSQL | 5,000 | 1 | 5,001 | 53 ms | 0.107 / 0.821 ms | held |
| SQL Server | 500 | 1 | 500 | 95 ms | 0.0252 / 0.0297 ms | held |
| SQLite | 160 | 1 | 160 | 80 ms | 0.0339 / 0.0518 ms | held |

Highest rate held in every pass: PostgreSQL 5,000/s, every written rate, so the ceiling is above the ladder; SQL Server 500/s, every written rate, so the ceiling is above the ladder; SQLite 160/s, every written rate, so the ceiling is above the ladder.

### Future high-priority work alongside

With a large population of high-priority jobs due tomorrow, does the stream hold its rate with p99 under 2 s?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; workers learn of new work: in process; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Disk write, mean / p95 | Result |
|---|---:|---:|---:|---:|---:|---|
| PostgreSQL | 5,000 | 1 | 5,001 | 56 ms | 0.0606 / 0.303 ms | held |
| SQL Server | 320 | 1 | 319 | 106 ms | 1.76 / 15.6 ms | held |
| SQLite | 160 | 1 | 160 | 81 ms | 0.0336 / 0.225 ms | held |

Highest rate held in every pass: PostgreSQL 5,000/s, every written rate, so the ceiling is above the ladder; SQL Server 320/s, every written rate, so the ceiling is above the ladder; SQLite 160/s, every written rate, so the ceiling is above the ladder.

### Ten children and a join

Do parents that fan out ten children and join them hold their offered rate with p99 under 10 s?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; workers learn of new work: in process; p99 target: 10.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Disk write, mean / p95 | Result |
|---|---:|---:|---:|---:|---:|---|
| PostgreSQL | 250 | 1 | 251 | 303 ms | 0.0276 / 0.0436 ms | held |
| SQL Server | 200 | 1 | 199 | 342 ms | 0.0513 / 0.325 ms | held |
| SQLite | 8 | 1 | 8 | 269 ms | 0.0317 / 0.0697 ms | held |

Highest rate held in every pass: PostgreSQL 250/s, every written rate, so the ceiling is above the ladder; SQL Server 200/s, every written rate, so the ceiling is above the ladder; SQLite 8/s, every written rate, so the ceiling is above the ladder.

### Controlled I/O through an idempotent sink

Do jobs that each call an external idempotent sink for 5 ms hold their offered rate with p99 under 5 s?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; workers learn of new work: in process; p99 target: 5.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Disk write, mean / p95 | Result |
|---|---:|---:|---:|---:|---:|---|
| PostgreSQL | 250 | 1 | 251 | 135 ms | 0.022 / 0.0442 ms | held |
| SQL Server | 200 | 1 | 200 | 219 ms | 0.0249 / 0.0742 ms | held |
| SQLite | 80 | 1 | 80.3 | 338 ms | 0.0344 / 0.0508 ms | held |

Highest rate held in every pass: PostgreSQL 250/s, every written rate, so the ceiling is above the ladder; SQL Server 200/s, every written rate, so the ceiling is above the ladder; SQLite 80/s, every written rate, so the ceiling is above the ladder.

### Short and long work mixed

Do no-op, CPU, sink, timer and fan-out jobs mixed in turn hold their offered rate with p99 under 5 s?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; workers learn of new work: in process; p99 target: 5.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Disk write, mean / p95 | Result |
|---|---:|---:|---:|---:|---:|---|
| PostgreSQL | 600 | 1 | 601 | 1.1 s | 0.0588 / 0.607 ms | held |
| SQL Server | 500 | 1 | 501 | 1.1 s | 0.0505 / 1.76 ms | held |
| SQLite | 80 | 1 | 80.2 | 1.1 s | 0.0295 / 0.0436 ms | held |

Highest rate held in every pass: PostgreSQL 600/s, every written rate, so the ceiling is above the ladder; SQL Server 500/s, every written rate, so the ceiling is above the ladder; SQLite 80/s, every written rate, so the ceiling is above the ladder.

### Throughput, one executor

Does a worker with a single executor keep up with a modest stream, so the result shows its serial cost?

Setup: workers: 1; namespaces: 1; executors per worker: 1; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; workers learn of new work: in process; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Disk write, mean / p95 | Result |
|---|---:|---:|---:|---:|---:|---|
| PostgreSQL | 150 | 1 | 150 | 27 ms | 0.022 / 0.0439 ms | held |
| SQL Server | 150 | 1 | 150 | 95 ms | 1.11 / 9.12 ms | held |
| SQLite | 100 | 1 | 100 | 26 ms | 0.0332 / 0.0925 ms | held |

Highest rate held in every pass: PostgreSQL 150/s, every written rate, so the ceiling is above the ladder; SQL Server 150/s, every written rate, so the ceiling is above the ladder; SQLite 100/s, every written rate, so the ceiling is above the ladder.

### One hot lane

How fast does one ordered lane hand its jobs over, each waiting for the previous completion to commit?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; workers learn of new work: in process; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Disk write, mean / p95 | Result |
|---|---:|---:|---:|---:|---:|---|
| PostgreSQL | 60 | 1 | 60 | 25 ms | 0.0252 / 0.0494 ms | held |
| SQL Server | 100 | 1 | 100 | 61 ms | 0.124 / 1.64 ms | held |
| SQLite | 80 | 1 | 80 | 42 ms | 0.0342 / 0.147 ms | held |

Highest rate held in every pass: PostgreSQL 60/s, every written rate, so the ceiling is above the ladder; SQL Server 100/s, every written rate, so the ceiling is above the ladder; SQLite 80/s, every written rate, so the ceiling is above the ladder.

### One hot concurrency key

With every job sharing one concurrency key of limit one, does a stream of one a second keep a flat backlog with p99 under 30 s?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; workers learn of new work: in process; p99 target: 30.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Disk write, mean / p95 | Result |
|---|---:|---:|---:|---:|---:|---|
| PostgreSQL | 1 | 1 | 1 | 41 ms | 0.701 / 2.54 ms | held |
| SQL Server | 1 | 1 | 1 | 43 ms | 0.409 / 3.42 ms | held |
| SQLite | 4 | 1 | 4 | 28 ms | 0.122 / 0.866 ms | held |

Highest rate held in every pass: PostgreSQL 1/s, every written rate, so the ceiling is above the ladder; SQL Server 1/s, every written rate, so the ceiling is above the ladder; SQLite 4/s, every written rate, so the ceiling is above the ladder.

### Five-minute mixed soak

Does the mixed workload hold a steady rate for five minutes with p99 under 5 s and a flat backlog?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; workers learn of new work: in process; p99 target: 5.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Disk write, mean / p95 | Result |
|---|---:|---:|---:|---:|---:|---|
| PostgreSQL | 700 | 1 | 700 | 1.2 s | 0.0894 / 0.589 ms | held |
| SQL Server | 350 | 1 | 350 | 1.1 s | 0.0925 / 0.604 ms | held |
| SQLite | 56 | 1 | 56 | 1.1 s | 0.0341 / 0.0425 ms | held |

Highest rate held in every pass: PostgreSQL 700/s, every written rate, so the ceiling is above the ladder; SQL Server 350/s, every written rate, so the ceiling is above the ladder; SQLite 56/s, every written rate, so the ceiling is above the ladder.

### Overload, then recovery

Offered more than it can run, does the backlog grow, and does it fall back once the offered rate drops?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Disk write, mean / p95 | Result |
|---|---:|---:|---:|---:|---:|---|
| PostgreSQL | 13,000, then 2,500 | 1 | 7,750 | 12.3 s | 0.14 / 0.0506 ms | pass: the backlog grew, then fell back |
| SQL Server | 8,000, then 1,000 | 1 | 4,500 | 10.2 s | 0.063 / 0.322 ms | pass: the backlog grew, then fell back |
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

## How this run was made

- Command: `acta-verify release --db all`.
- Commit `d20e7c77e6d9239f2ba77fe959ec6286f93a2d0b`, measurement version 3, written standard `d839704c720d`.
- Machine: Intel(R) Core(TM) i9-14900K, 24 cores, 32 logical processors, Microsoft Windows 10.0.26300, .NET 10.0.14.
- Docker VM: 32 CPUs, 46.9 GB; measured containers capped at PostgreSQL 32031 MB, SQL Server 32031 MB.
- PostgreSQL: peak 518 database connections; disk before measuring: quiet before 32 of 33 cells.
- SQL Server: peak 522 database connections; disk before measuring: quiet before 29 of 33 cells.
- SQLite: peak 0 database connections; disk before measuring: quiet before 25 of 25 cells.
- Wall time: performance on PostgreSQL 2 h 14 min, correctness on PostgreSQL 6 min, chaos on PostgreSQL 20 min, performance on SQL Server 2 h 7 min, correctness on SQL Server 10 min, chaos on SQL Server 48 min, performance on SQLite 1 h 20 min, correctness on SQLite 5 min, chaos on SQLite 12 min; correctness runs on every database at once, chaos one database at a time.
- Evidence: 29.5 MB of a 50 MB budget.
- A rate holds when every scheduled arrival reached an outcome and none was refused, the backlog trend stayed within 1% of the rate, p99 stayed within the target, at most 0.1% of arrivals were picked up late by the harness, and every job reconciled. These are the rc.6 criteria.
- p99 runs from an arrival's scheduled instant to its job's durable completion, or to the enqueue call's answer in an enqueue-only scenario. A scenario that streams arrivals to one worker in one namespace runs that worker in the process that enqueues, as a single Acta host does: it shares that process's heap and thread pool and the enqueue calls' connection pool, and each enqueue wakes it at once. A worker process claims again while its claims return jobs; after an empty claim it waits up to its 1 s safety poll, or until Redis wakes it where the setup says so.

| Disk, by phase and drive | Windows counters |
|---|---|
| before \| 0 c: | write 2.59 ms mean, 6.53 ms p95, 9 writes/s, 99% idle, 14 samples |
| before \| 1 d: | write 4.53 ms mean, 7.57 ms p95, 42 writes/s, 99% idle, 14 samples |
| chaos mssql \| 0 c: | write 3.21 ms mean, 8.13 ms p95, 7 writes/s, 99% idle, 1454 samples |
| chaos mssql \| 1 d: | write 0.29 ms mean, 1.17 ms p95, 574 writes/s, 73% idle, 1454 samples |
| chaos pg \| 0 c: | write 3.11 ms mean, 8.16 ms p95, 5 writes/s, 99% idle, 612 samples |
| chaos pg \| 1 d: | write 0.07 ms mean, 0.08 ms p95, 789 writes/s, 54% idle, 612 samples |
| chaos sqlite \| 0 c: | write 2.48 ms mean, 7.61 ms p95, 12 writes/s, 94% idle, 371 samples |
| chaos sqlite \| 1 d: | write 1.85 ms mean, 7.54 ms p95, 691 writes/s, 80% idle, 371 samples |
| correctness \| 0 c: | write 2.41 ms mean, 8.11 ms p95, 11 writes/s, 99% idle, 322 samples |
| correctness \| 1 d: | write 0.32 ms mean, 1.54 ms p95, 513 writes/s, 81% idle, 322 samples |
| mssql \| 0 c: | write 2.70 ms mean, 7.71 ms p95, 8 writes/s, 99% idle, 3797 samples |
| mssql \| 1 d: | write 1.13 ms mean, 6.42 ms p95, 1522 writes/s, 64% idle, 3797 samples |
| pg \| 0 c: | write 2.52 ms mean, 7.60 ms p95, 10 writes/s, 99% idle, 4025 samples |
| pg \| 1 d: | write 1.35 ms mean, 6.70 ms p95, 699 writes/s, 69% idle, 4025 samples |
| sqlite \| 0 c: | write 2.65 ms mean, 7.93 ms p95, 8 writes/s, 99% idle, 2391 samples |
| sqlite \| 1 d: | write 0.63 ms mean, 5.94 ms p95, 748 writes/s, 72% idle, 2391 samples |
| after \| 0 c: | write 4.03 ms mean, 8.58 ms p95, 5 writes/s, 99% idle, 15 samples |
| after \| 1 d: | write 4.69 ms mean, 7.37 ms p95, 7 writes/s, 100% idle, 15 samples |
