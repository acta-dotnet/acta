# Acta 1.0.0-rc.14: benchmark and release validation

The 1.0.0-rc.14 release evidence, measured by the project on its own machine: one Acta Verify release run on `d74d9393e` on PostgreSQL, SQL Server, and SQLite. Every correctness, fault, and chaos scenario passed, every trial's ledger passed the ledger checks, and every performance floor held, with no rerun.

- The run's commit, `d74d9393e`, is in the tagged history, and the commit after it changes documentation and the site, so the evidence stands for the tag.
- Both PostgreSQL sixteen-worker ladders start at 12,500/s since rc.12, with 15,000 above it, and both held every written rate. Two namespaces completed 15,160 jobs a second at 15,000/s offered with p99 470 ms, and one namespace 15,230 with p99 1.4 s against the 2.0 s target, the cell rc.12 held on its fourth run.
- PostgreSQL's four workers held 15,000/s offered in all three 30 s passes and in the 120 s confirmation, completing 14,904 jobs a second with p99 1.0 s, where rc.12's confirmation held at 12,500. SQL Server's one worker confirmed at 5,000/s, 5,003 jobs a second, where rc.12's first run confirmed at 2,500.
- The crash-recovery chaos runs finished a million jobs each on PostgreSQL and SQL Server while a worker was killed every five seconds, with 84 and 83 busy kills reclaimed, and ten thousand on SQLite with 48.
- This is measurement version 3, as in rc.12, and `acta-verify compare` sets the run beside rc.12's, both on .NET 10.0.15. All 219 scenario runs the two share passed both times; the two PostgreSQL sixteen-worker throughput cells are not compared, since their ladders changed. Of the measured cells, 20 read faster, 58 the same, and 14 slower, each from one run per release.
- The fourteen that read slower are p99s at rates both runs held, each inside its floor, and none was rerun. The largest are PostgreSQL's enqueue of one job per call, 170 ms against 26 ms at 10,000/s; sixteen workers woken through Redis, 521 ms against 120 ms on PostgreSQL at 12,500/s and 1.7 s against 478 ms on SQL Server at 4,000/s; PostgreSQL's fixed CPU work, 292 ms against 63 ms at 4,000/s; and SQL Server's 32 executors, 234 ms against 89 ms at 2,500/s.

# Acta Verify: PASS

The release suite on PostgreSQL, SQL Server, SQLite, commit `d74d9393e`, started 2026-11-17 23:11 UTC, 7 h 1 min, on Intel(R) Core(TM) i9-14900K.

| | PostgreSQL | SQL Server | SQLite |
|---|---|---|---|
| Correctness | pass | pass | pass |
| Crash-recovery chaos | pass, 1,000,000 jobs, 84 busy kills reclaimed | pass, 1,000,000 jobs, 83 busy kills reclaimed | pass, 10,000 jobs, 48 busy kills reclaimed |
| Ledger checks | pass, 160 trials checked | pass, 148 trials checked | pass, 125 trials checked |
| Performance floors | held | held | held |
| Drain | 87,113 jobs/s | 46,595 jobs/s | 6,566 jobs/s |
| Throughput | 14,904 jobs/s | 5,012 jobs/s | 250 jobs/s |
| Throughput, one worker | 7,500 jobs/s | 5,003 jobs/s | not written |

> **14,904 fully executed, durably completed jobs per second** on PostgreSQL: 4 workers polling for work, one job per enqueue call, held 3 × 30 s and 120 s at 15,000/s offered, p99 1.0 s, Intel(R) Core(TM) i9-14900K.

> **5,012 fully executed, durably completed jobs per second** on SQL Server: 4 workers polling for work, one job per enqueue call, held 3 × 30 s and 120 s at 5,000/s offered, p99 956 ms, Intel(R) Core(TM) i9-14900K.

> **250 fully executed, durably completed jobs per second** on SQLite: 1 worker, woken in process, one job per enqueue call, held 3 × 30 s and 120 s at 250/s offered, p99 104 ms, Intel(R) Core(TM) i9-14900K.

> **7,500 fully executed, durably completed jobs per second** on PostgreSQL: 1 worker, woken in process, one job per enqueue call, held 3 × 30 s and 120 s at 7,500/s offered, p99 53 ms, Intel(R) Core(TM) i9-14900K.

> **5,003 fully executed, durably completed jobs per second** on SQL Server: 1 worker, woken in process, one job per enqueue call, held 3 × 30 s and 120 s at 5,000/s offered, p99 1.0 s, Intel(R) Core(TM) i9-14900K.

## Rates to raise

These written rates ask less than the database ran; they decide nothing in this run.

- **Enqueue in batches of 100** on PostgreSQL: the floor of 10,000/s also held at 40,000/s.
- **Enqueue in batches of 1,000** on PostgreSQL: the floor of 20,000/s also held at 80,000/s.
- **Throughput, sixteen workers** on PostgreSQL: every written rate held, up to 15,000/s, so the ladder found no ceiling.
- **Throughput, sixteen workers on one namespace** on PostgreSQL: every written rate held, up to 15,000/s, so the ladder found no ceiling.
- **Throughput, 32 executors** on SQLite: the floor of 160/s also held at 640/s.
- **Fixed CPU work** on SQLite: the floor of 160/s also held at 640/s.
- **Payloads of 4 KB** on SQLite: the floor of 120/s also held at 480/s.
- **Payloads of 256 B, 4 KB and 64 KB mixed** on SQLite: the floor of 160/s also held at 640/s.
- **Payloads of 64 KB** on SQLite: the floor of 40/s also held at 160/s.
- **A thousand lanes** on SQL Server: the floor of 1,000/s also held at 4,000/s.
- **Operator queries during processing** on SQLite: the floor of 120/s also held at 480/s.
- **Parked work alongside** on SQL Server: the floor of 500/s also held at 2,000/s.
- **Parked work alongside** on SQLite: the floor of 160/s also held at 640/s.
- **Future high-priority work alongside** on SQL Server: the floor of 320/s also held at 1,280/s.
- **Ten children and a join** on SQLite: the floor of 8/s also held at 32/s.
- **Throughput, one executor** on PostgreSQL: the floor of 150/s also held at 600/s.
- **Throughput, one executor** on SQL Server: the floor of 150/s also held at 600/s.
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
| PostgreSQL | 1 | 1,000,000 | 87,113 jobs/s | 12.2 s | 0.403 / 2.63 ms | held |
| PostgreSQL | 2 | 1,000,000 | 70,741 jobs/s | 14.8 s | 15.5 / 90.9 ms | held |
| PostgreSQL | 3 | 1,000,000 | 87,543 jobs/s | 12.4 s | 5.07 / 29.2 ms | held |
| SQL Server | 1 | 1,000,000 | 38,790 jobs/s | 29.4 s | 1.24 / 0.59 ms | held |
| SQL Server | 2 | 1,000,000 | 46,595 jobs/s | 22.6 s | 8.74 / 0.657 ms | held |
| SQL Server | 3 | 1,000,000 | 47,244 jobs/s | 21.7 s | 2.24 / 2.07 ms | held |
| SQLite | 1 | 100,000 | 6,502 jobs/s | 15.7 s | 0.0472 / 0.125 ms | held |
| SQLite | 2 | 100,000 | 6,566 jobs/s | 15.5 s | 0.0413 / 0.0664 ms | held |
| SQLite | 3 | 100,000 | 6,736 jobs/s | 15.4 s | 0.0496 / 0.121 ms | held |

### Sustained throughput

How many jobs per second, one per enqueue call, does Acta enqueue, run and durably complete with a flat backlog and p99 under 2 s?

Setup: workers: 4 on PostgreSQL, 4 on SQL Server, 1 on SQLite; namespaces: 2 on PostgreSQL, 2 on SQL Server, 1 on SQLite; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; workers learn of new work: by polling on PostgreSQL, by polling on SQL Server, in process on SQLite; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Disk write, mean / p95 | Result |
|---|---:|---:|---:|---:|---:|---|
| PostgreSQL | 10,000 | 1 | 10,001 | 1.0 s | 0.0372 / 0.0387 ms | held |
| PostgreSQL | 10,000 | 2 | 9,979 | 1.0 s | 0.217 / 2.74 ms | held |
| PostgreSQL | 10,000 | 3 | 10,159 | 1.0 s | 0.173 / 2.37 ms | held |
| PostgreSQL | 12,500 | 1 | 12,539 | 1.0 s | 0.098 / 1.1 ms | held |
| PostgreSQL | 12,500 | 2 | 12,531 | 1.0 s | 0.0416 / 0.0849 ms | held |
| PostgreSQL | 12,500 | 3 | 12,692 | 1.0 s | 0.0533 / 0.0567 ms | held |
| PostgreSQL | 15,000 | 1 | 14,858 | 1.0 s | 0.06 / 0.337 ms | held |
| PostgreSQL | 15,000 | 2 | 15,012 | 977 ms | 0.0494 / 0.0603 ms | held |
| PostgreSQL | 15,000 | 3 | 14,970 | 1.0 s | 0.0507 / 0.061 ms | held |
| PostgreSQL | 17,500 | 1 | 17,034 | 1.7 s | 0.403 / 6.48 ms | missed: backlog grew 546 jobs/s, 175 allowed |
| PostgreSQL | 17,500 | 2 | 17,482 | 1.0 s | 0.124 / 1.16 ms | held |
| PostgreSQL | 17,500 | 3 | 17,385 | 1.0 s | 0.698 / 8.65 ms | held |
| PostgreSQL | 20,000 | 2 | 17,913 | 4.5 s | 0.138 / 0.0472 ms | missed: backlog grew 2,194 jobs/s, 200 allowed |
| PostgreSQL | 20,000 | 3 | 16,977 | 7.1 s | 0.054 / 0.0665 ms | missed: backlog grew 2,903 jobs/s, 200 allowed |
| PostgreSQL | 25,000 | | | | | not run by rule |
| PostgreSQL | 30,000 | | | | | not run by rule |
| PostgreSQL | 15,000 | confirm, 120 s | 14,904 | 1.0 s | 0.205 / 0.422 ms | held |
| SQL Server | 5,000 | 1 | 4,863 | 906 ms | 0.028 / 0.0414 ms | held |
| SQL Server | 5,000 | 2 | 5,004 | 804 ms | 0.333 / 4.26 ms | held |
| SQL Server | 5,000 | 3 | 4,997 | 745 ms | 0.54 / 6.63 ms | held |
| SQL Server | 7,500 | 1 | 6,721 | 4.9 s | 0.0865 / 0.579 ms | missed: backlog grew 800 jobs/s, 75 allowed |
| SQL Server | 7,500 | 2 | 6,893 | 3.6 s | 0.168 / 1.8 ms | missed: backlog grew 599 jobs/s, 75 allowed |
| SQL Server | 7,500 | 3 | 6,833 | 4.1 s | 0.0914 / 1.31 ms | missed: backlog grew 687 jobs/s, 75 allowed |
| SQL Server | 10,000 | | | | | not run by rule |
| SQL Server | 12,500 | | | | | not run by rule |
| SQL Server | 15,000 | | | | | not run by rule |
| SQL Server | 5,000 | confirm, 120 s | 5,012 | 956 ms | 0.49 / 1.85 ms | held |
| SQLite | 250 | 1 | 250 | 82 ms | 0.0296 / 0.0672 ms | held |
| SQLite | 250 | 2 | 250 | 104 ms | 0.0302 / 0.0363 ms | held |
| SQLite | 250 | 3 | 250 | 106 ms | 0.0301 / 0.0309 ms | held |
| SQLite | 500 | 1 | 500 | 82 ms | 0.0236 / 0.025 ms | held |
| SQLite | 500 | 2 | 383 | 12.9 s | 0.0283 / 0.0304 ms | missed: backlog grew 148 jobs/s, 5 allowed |
| SQLite | 500 | 3 | 526 | 2.5 s | 0.0242 / 0.0291 ms | missed: p99 2.5 s against a 2.0 s target |
| SQLite | 1,000 | 1 | 689 | 27.9 s | 0.0254 / 0.03 ms | missed: backlog grew 298 jobs/s, 10 allowed |
| SQLite | 2,000 | | | | | not run by rule |
| SQLite | 250 | confirm, 120 s | 250 | 104 ms | 0.0298 / 0.0308 ms | held |

Highest rate held in every pass: PostgreSQL 15,000/s; SQL Server 5,000/s; SQLite 250/s.

### Sustained throughput, woken through Redis

With Redis carrying each enqueue's wake to the worker processes, how many jobs per second, one per enqueue call, does Acta enqueue, run and durably complete with a flat backlog and p99 under 2 s?

Setup: workers: 4 on PostgreSQL, 4 on SQL Server, 1 on SQLite; namespaces: 2 on PostgreSQL, 2 on SQL Server, 1 on SQLite; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; workers learn of new work: through Redis on PostgreSQL, through Redis on SQL Server, in process on SQLite; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Disk write, mean / p95 | Result |
|---|---:|---:|---:|---:|---:|---|
| PostgreSQL | 10,000 | 1 | 9,998 | 100 ms | 0.0415 / 0.0483 ms | held |
| PostgreSQL | 12,500 | 1 | 12,511 | 97 ms | 0.039 / 0.0692 ms | held |
| PostgreSQL | 15,000 | 1 | 15,012 | 96 ms | 0.0543 / 0.0634 ms | held |
| PostgreSQL | 17,500 | 1 | 16,184 | 3.1 s | 0.052 / 0.0653 ms | missed: backlog grew 1,204 jobs/s, 175 allowed |
| PostgreSQL | 20,000 | | | | | not run by rule |
| PostgreSQL | 25,000 | | | | | not run by rule |
| PostgreSQL | 30,000 | | | | | not run by rule |
| SQL Server | 5,000 | 1 | 5,002 | 168 ms | 0.135 / 0.787 ms | held |
| SQL Server | 7,500 | 1 | 6,858 | 4.3 s | 0.119 / 0.48 ms | missed: backlog grew 604 jobs/s, 75 allowed |
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
| PostgreSQL | 5,000 | 1 | 5,001 | 52 ms | 0.0338 / 0.0363 ms | held |
| PostgreSQL | 5,000 | 2 | 5,000 | 465 ms | 0.0357 / 0.0376 ms | held |
| PostgreSQL | 5,000 | 3 | 4,999 | 115 ms | 0.0743 / 0.788 ms | held |
| PostgreSQL | 7,500 | 1 | 7,501 | 42 ms | 0.0335 / 0.0341 ms | held |
| PostgreSQL | 7,500 | 2 | 7,501 | 50 ms | 0.0415 / 0.0429 ms | held |
| PostgreSQL | 7,500 | 3 | 7,501 | 50 ms | 0.0368 / 0.0428 ms | held |
| PostgreSQL | 10,000 | 1 | 9,468 | 1.6 s | 0.0412 / 0.0516 ms | missed: backlog grew 571 jobs/s, 100 allowed |
| PostgreSQL | 10,000 | 2 | 10,120 | 1.3 s | 0.0415 / 0.0516 ms | held |
| PostgreSQL | 10,000 | 3 | 8,772 | 4.4 s | 0.0465 / 0.0502 ms | missed: backlog grew 1,233 jobs/s, 100 allowed |
| PostgreSQL | 12,500 | 2 | 11,017 | 3.8 s | 0.0372 / 0.0379 ms | missed: backlog grew 1,486 jobs/s, 125 allowed |
| PostgreSQL | 15,000 | | | | | not run by rule |
| PostgreSQL | 7,500 | confirm, 120 s | 7,500 | 53 ms | 0.113 / 0.25 ms | held |
| SQL Server | 2,500 | 1 | 2,496 | 92 ms | 0.0315 / 0.288 ms | held |
| SQL Server | 2,500 | 2 | 2,501 | 102 ms | 0.36 / 3.71 ms | held |
| SQL Server | 2,500 | 3 | 2,503 | 96 ms | 0.0272 / 0.037 ms | held |
| SQL Server | 5,000 | 1 | 4,998 | 630 ms | 0.0948 / 1.52 ms | held |
| SQL Server | 5,000 | 2 | 5,001 | 135 ms | 0.174 / 8.46 ms | held |
| SQL Server | 5,000 | 3 | 5,002 | 148 ms | 0.0698 / 0.392 ms | held |
| SQL Server | 7,500 | 1 | 6,046 | 9.3 s | 0.241 / 0.923 ms | missed: backlog grew 1,372 jobs/s, 75 allowed |
| SQL Server | 7,500 | 2 | 6,287 | 7.8 s | 0.214 / 0.954 ms | missed: backlog grew 1,133 jobs/s, 75 allowed |
| SQL Server | 7,500 | 3 | 6,152 | 7.7 s | 0.178 / 0.746 ms | missed: backlog grew 1,147 jobs/s, 75 allowed |
| SQL Server | 10,000 | | | | | not run by rule |
| SQL Server | 5,000 | confirm, 120 s | 5,003 | 1.0 s | 0.213 / 0.85 ms | held |
| SQLite | | | | | | The scenario writes no rate for sqlite. |

Highest rate held in every pass: PostgreSQL 7,500/s; SQL Server 5,000/s.

### Enqueue, one job per call

How fast are single-job enqueue calls acknowledged, each in its own transaction, before any worker runs?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; workers learn of new work: by polling; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Disk write, mean / p95 | Result |
|---|---:|---:|---:|---:|---:|---|
| PostgreSQL | 10,000 | 1 | 0 | 170 ms | 0.0379 / 0.0468 ms | held |
| SQL Server | 5,000 | 1 | 0 | 27 ms | 0.181 / 0.0627 ms | held |
| SQLite | 200 | 1 | 0 | 20 ms | 0.0398 / 0.0603 ms | held |

Highest rate held in every pass: PostgreSQL 10,000/s, every written rate, so the ceiling is above the ladder; SQL Server 5,000/s, every written rate, so the ceiling is above the ladder; SQLite 200/s, every written rate, so the ceiling is above the ladder.

### Enqueue in batches of 100

How fast are batches of 100 jobs acknowledged, before any worker runs?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 100; workers learn of new work: by polling; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Disk write, mean / p95 | Result |
|---|---:|---:|---:|---:|---:|---|
| PostgreSQL | 10,000 | 1 | 0 | 38 ms | 0.133 / 0.593 ms | held |
| SQL Server | 5,000 | 1 | 0 | 57 ms | 2.75 / 0.0484 ms | held |
| SQLite | 5,000 | 1 | 0 | 31 ms | 0.0347 / 0.0433 ms | held |

Highest rate held in every pass: PostgreSQL 10,000/s, every written rate, so the ceiling is above the ladder; SQL Server 5,000/s, every written rate, so the ceiling is above the ladder; SQLite 5,000/s, every written rate, so the ceiling is above the ladder.

### Enqueue in batches of 1,000

How fast are batches of 1,000 jobs acknowledged, before any worker runs?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1,000; workers learn of new work: by polling; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Disk write, mean / p95 | Result |
|---|---:|---:|---:|---:|---:|---|
| PostgreSQL | 20,000 | 1 | 0 | 127 ms | 0.446 / 0.162 ms | held |
| SQL Server | 10,000 | 1 | 0 | 182 ms | 0.848 / 0.0709 ms | held |
| SQLite | 10,000 | 1 | 0 | 83 ms | 0.0588 / 0.132 ms | held |

Highest rate held in every pass: PostgreSQL 20,000/s, every written rate, so the ceiling is above the ladder; SQL Server 10,000/s, every written rate, so the ceiling is above the ladder; SQLite 10,000/s, every written rate, so the ceiling is above the ladder.

### Drain, one worker

How fast does one worker run and durably complete a backlog enqueued before it starts?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 8; jobs per call: 1,000.

| Database | Pass | Jobs | Steady rate, database clock | Whole cohort | Disk write, mean / p95 | Result |
|---|---:|---:|---:|---:|---:|---|
| PostgreSQL | 1 | 200,000 | 20,842 jobs/s | 9.77 s | 0.542 / 2.67 ms | held |
| SQL Server | 1 | 200,000 | 13,817 jobs/s | 15 s | 2.56 / 24 ms | held |
| SQLite | 1 | 50,000 | 6,502 jobs/s | 7.81 s | 0.0564 / 0.797 ms | held |

### Drain, four workers

How fast do four workers over two namespaces drain a backlog enqueued before they start?

Setup: workers: 4; namespaces: 2; executors per worker: the product default; enqueue calls in flight: 8; jobs per call: 1,000.

| Database | Pass | Jobs | Steady rate, database clock | Whole cohort | Disk write, mean / p95 | Result |
|---|---:|---:|---:|---:|---:|---|
| PostgreSQL | 1 | 200,000 | 57,389 jobs/s | 3.6 s | 0.119 / 0.184 ms | held |
| SQL Server | 1 | 200,000 | 39,646 jobs/s | 5.03 s | 0.0438 / 0.0927 ms | held |
| SQLite | | | | | | The scenario writes no cohort for sqlite. |

### Drain, sixteen workers

How fast do sixteen workers over two namespaces drain a backlog enqueued before they start?

Setup: workers: 16; namespaces: 2; executors per worker: the product default; enqueue calls in flight: 8; jobs per call: 1,000.

| Database | Pass | Jobs | Steady rate, database clock | Whole cohort | Disk write, mean / p95 | Result |
|---|---:|---:|---:|---:|---:|---|
| PostgreSQL | 1 | 1,000,000 | 71,426 jobs/s | 14.7 s | 0.294 / 1.51 ms | held |
| SQL Server | 1 | 1,000,000 | 45,301 jobs/s | 22.6 s | 0.508 / 0.732 ms | held |
| SQLite | | | | | | The scenario writes no cohort for sqlite. |

### Throughput, sixteen workers

Do sixteen worker processes over two namespaces hold a high offered rate with p99 under 2 s?

Setup: workers: 16; namespaces: 2; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; workers learn of new work: by polling; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Disk write, mean / p95 | Result |
|---|---:|---:|---:|---:|---:|---|
| PostgreSQL | 12,500 | 1 | 12,441 | 822 ms | 0.0498 / 0.0629 ms | held |
| PostgreSQL | 15,000 | 1 | 15,160 | 470 ms | 0.133 / 1.48 ms | held |
| SQL Server | 4,000 | 1 | 4,009 | 840 ms | 0.462 / 26.8 ms | held |
| SQLite | | | | | | The scenario writes no rate for sqlite. |

Highest rate held in every pass: PostgreSQL 15,000/s, every written rate, so the ceiling is above the ladder; SQL Server 4,000/s, every written rate, so the ceiling is above the ladder.

### Throughput, sixteen workers, woken through Redis

With Redis carrying each enqueue's wake, do sixteen worker processes over two namespaces hold a high offered rate with p99 under 2 s?

Setup: workers: 16; namespaces: 2; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; workers learn of new work: through Redis; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Disk write, mean / p95 | Result |
|---|---:|---:|---:|---:|---:|---|
| PostgreSQL | 12,500 | 1 | 12,690 | 521 ms | 0.392 / 6.06 ms | held |
| SQL Server | 4,000 | 1 | 4,287 | 1.7 s | 0.234 / 3.52 ms | held |
| SQLite | | | | | | SQLite is one process's database; sixteen worker processes are not a SQLite topology. |

Highest rate held in every pass: PostgreSQL 12,500/s, every written rate, so the ceiling is above the ladder; SQL Server 4,000/s, every written rate, so the ceiling is above the ladder.

### Throughput, sixteen workers on one namespace

Do sixteen worker processes sharing one namespace hold a high offered rate with p99 under 2 s?

Setup: workers: 16; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; workers learn of new work: by polling; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Disk write, mean / p95 | Result |
|---|---:|---:|---:|---:|---:|---|
| PostgreSQL | 12,500 | 1 | 12,515 | 427 ms | 0.0418 / 0.0868 ms | held |
| PostgreSQL | 15,000 | 1 | 15,230 | 1.4 s | 0.0432 / 0.0466 ms | held |
| SQL Server | 4,000 | 1 | 3,996 | 1.0 s | 0.679 / 9.92 ms | held |
| SQLite | | | | | | The scenario writes no rate for sqlite. |

Highest rate held in every pass: PostgreSQL 15,000/s, every written rate, so the ceiling is above the ladder; SQL Server 4,000/s, every written rate, so the ceiling is above the ladder.

### Throughput, 32 executors

Does one worker with 32 executors hold the stream its default configuration holds?

Setup: workers: 1; namespaces: 1; executors per worker: 32; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; workers learn of new work: in process; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Disk write, mean / p95 | Result |
|---|---:|---:|---:|---:|---:|---|
| PostgreSQL | 5,000 | 1 | 4,997 | 51 ms | 0.0315 / 0.0319 ms | held |
| SQL Server | 2,500 | 1 | 2,514 | 234 ms | 0.0298 / 0.0497 ms | held |
| SQLite | 160 | 1 | 160 | 95 ms | 0.0351 / 0.0657 ms | held |

Highest rate held in every pass: PostgreSQL 5,000/s, every written rate, so the ceiling is above the ladder; SQL Server 2,500/s, every written rate, so the ceiling is above the ladder; SQLite 160/s, every written rate, so the ceiling is above the ladder.

### Three persisted steps

Do jobs that commit three checkpoints each hold their offered rate with p99 under 5 s?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; workers learn of new work: in process; p99 target: 5.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Disk write, mean / p95 | Result |
|---|---:|---:|---:|---:|---:|---|
| PostgreSQL | 1,000 | 1 | 1,000 | 106 ms | 0.0258 / 0.0449 ms | held |
| SQL Server | 1,000 | 1 | 1,000 | 112 ms | 0.0494 / 0.872 ms | held |
| SQLite | 40 | 1 | 40 | 47 ms | 0.0332 / 0.0556 ms | held |

Highest rate held in every pass: PostgreSQL 1,000/s, every written rate, so the ceiling is above the ladder; SQL Server 1,000/s, every written rate, so the ceiling is above the ladder; SQLite 40/s, every written rate, so the ceiling is above the ladder.

### Transient failure and retry

With one job in four failing twice before it succeeds, does the stream hold its rate with p99 under 10 s?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; workers learn of new work: in process; p99 target: 10.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Disk write, mean / p95 | Result |
|---|---:|---:|---:|---:|---:|---|
| PostgreSQL | 5,000 | 1 | 5,004 | 2.1 s | 0.039 / 0.121 ms | held |
| SQL Server | 2,500 | 1 | 2,502 | 2.2 s | 0.031 / 0.0509 ms | held |
| SQLite | 160 | 1 | 160 | 2.2 s | 0.0346 / 0.0897 ms | held |

Highest rate held in every pass: PostgreSQL 5,000/s, every written rate, so the ceiling is above the ladder; SQL Server 2,500/s, every written rate, so the ceiling is above the ladder; SQLite 160/s, every written rate, so the ceiling is above the ladder.

### Fixed CPU work

Do jobs that each hash for a fixed number of rounds hold their offered rate with p99 under 2 s?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; workers learn of new work: in process; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Disk write, mean / p95 | Result |
|---|---:|---:|---:|---:|---:|---|
| PostgreSQL | 4,000 | 1 | 4,003 | 292 ms | 0.0361 / 0.0506 ms | held |
| SQL Server | 2,500 | 1 | 2,500 | 93 ms | 0.0543 / 0.784 ms | held |
| SQLite | 160 | 1 | 160 | 84 ms | 0.0326 / 0.0479 ms | held |

Highest rate held in every pass: PostgreSQL 4,000/s, every written rate, so the ceiling is above the ladder; SQL Server 2,500/s, every written rate, so the ceiling is above the ladder; SQLite 160/s, every written rate, so the ceiling is above the ladder.

### Payloads of 256 B

Do jobs carrying a 256-byte payload hold their offered rate with p99 under 2 s?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; workers learn of new work: in process; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Disk write, mean / p95 | Result |
|---|---:|---:|---:|---:|---:|---|
| PostgreSQL | 4,000 | 1 | 3,999 | 59 ms | 0.0389 / 0.0616 ms | held |
| SQL Server | 2,500 | 1 | 2,500 | 87 ms | 0.0489 / 2.13 ms | held |
| SQLite | 120 | 1 | 120 | 80 ms | 0.0374 / 0.203 ms | held |

Highest rate held in every pass: PostgreSQL 4,000/s, every written rate, so the ceiling is above the ladder; SQL Server 2,500/s, every written rate, so the ceiling is above the ladder; SQLite 120/s, every written rate, so the ceiling is above the ladder.

### Payloads of 4 KB

Do jobs carrying a 4 KB payload hold their offered rate with p99 under 2 s?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; workers learn of new work: in process; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Disk write, mean / p95 | Result |
|---|---:|---:|---:|---:|---:|---|
| PostgreSQL | 5,000 | 1 | 4,999 | 56 ms | 0.0418 / 0.0483 ms | held |
| SQL Server | 2,500 | 1 | 2,499 | 101 ms | 0.0608 / 0.782 ms | held |
| SQLite | 120 | 1 | 120 | 85 ms | 0.0373 / 0.0577 ms | held |

Highest rate held in every pass: PostgreSQL 5,000/s, every written rate, so the ceiling is above the ladder; SQL Server 2,500/s, every written rate, so the ceiling is above the ladder; SQLite 120/s, every written rate, so the ceiling is above the ladder.

### Payloads of 256 B, 4 KB and 64 KB mixed

Do jobs carrying payloads of three sizes in turn hold their offered rate with p99 under 2 s?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; workers learn of new work: in process; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Disk write, mean / p95 | Result |
|---|---:|---:|---:|---:|---:|---|
| PostgreSQL | 2,500 | 1 | 2,547 | 116 ms | 0.0928 / 0.776 ms | held |
| SQL Server | 1,000 | 1 | 999 | 102 ms | 14.4 / 97.8 ms | held |
| SQLite | 160 | 1 | 160 | 82 ms | 0.0336 / 0.0552 ms | held |

Highest rate held in every pass: PostgreSQL 2,500/s, every written rate, so the ceiling is above the ladder; SQL Server 1,000/s, every written rate, so the ceiling is above the ladder; SQLite 160/s, every written rate, so the ceiling is above the ladder.

### Payloads of 64 KB

Do jobs carrying a 64 KB payload hold their offered rate with p99 under 2 s?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; workers learn of new work: in process; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Disk write, mean / p95 | Result |
|---|---:|---:|---:|---:|---:|---|
| PostgreSQL | 1,000 | 1 | 1,001 | 103 ms | 0.0761 / 0.345 ms | held |
| SQL Server | 500 | 1 | 501 | 93 ms | 0.16 / 2.1 ms | held |
| SQLite | 40 | 1 | 40 | 38 ms | 0.0418 / 0.0469 ms | held |

Highest rate held in every pass: PostgreSQL 1,000/s, every written rate, so the ceiling is above the ladder; SQL Server 500/s, every written rate, so the ceiling is above the ladder; SQLite 40/s, every written rate, so the ceiling is above the ladder.

### A thousand lanes

Spread over a thousand ordered lanes, does the stream hold its rate with p99 under 2 s?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; workers learn of new work: in process; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Disk write, mean / p95 | Result |
|---|---:|---:|---:|---:|---:|---|
| PostgreSQL | 4,000 | 1 | 4,003 | 58 ms | 0.0373 / 0.0402 ms | held |
| SQL Server | 1,000 | 1 | 999 | 100 ms | 1.32 / 12.2 ms | held |
| SQLite | 120 | 1 | 120 | 86 ms | 0.0376 / 0.0614 ms | held |

Highest rate held in every pass: PostgreSQL 4,000/s, every written rate, so the ceiling is above the ladder; SQL Server 1,000/s, every written rate, so the ceiling is above the ladder; SQLite 120/s, every written rate, so the ceiling is above the ladder.

### Operator queries during processing

While the stream runs, do the operator list queries answer quickly, and does the stream keep its rate?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; workers learn of new work: in process; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Disk write, mean / p95 | Result |
|---|---:|---:|---:|---:|---:|---|
| PostgreSQL | 5,000 | 1 | 4,997 | 56 ms | 0.0372 / 0.0376 ms | held |
| SQL Server | 2,500 | 1 | 2,503 | 90 ms | 0.0332 / 0.208 ms | held |
| SQLite | 120 | 1 | 120 | 85 ms | 0.0374 / 0.0624 ms | held |

Highest rate held in every pass: PostgreSQL 5,000/s, every written rate, so the ceiling is above the ladder; SQL Server 2,500/s, every written rate, so the ceiling is above the ladder; SQLite 120/s, every written rate, so the ceiling is above the ladder.

### Parked work alongside

With a large population of jobs parked on signals, does the stream hold its rate with p99 under 2 s?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; workers learn of new work: in process; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Disk write, mean / p95 | Result |
|---|---:|---:|---:|---:|---:|---|
| PostgreSQL | 5,000 | 1 | 5,001 | 57 ms | 0.132 / 1.48 ms | held |
| SQL Server | 500 | 1 | 500 | 94 ms | 0.035 / 0.0663 ms | held |
| SQLite | 160 | 1 | 160 | 81 ms | 0.034 / 0.0652 ms | held |

Highest rate held in every pass: PostgreSQL 5,000/s, every written rate, so the ceiling is above the ladder; SQL Server 500/s, every written rate, so the ceiling is above the ladder; SQLite 160/s, every written rate, so the ceiling is above the ladder.

### Future high-priority work alongside

With a large population of high-priority jobs due tomorrow, does the stream hold its rate with p99 under 2 s?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; workers learn of new work: in process; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Disk write, mean / p95 | Result |
|---|---:|---:|---:|---:|---:|---|
| PostgreSQL | 5,000 | 1 | 5,001 | 53 ms | 0.253 / 3.05 ms | held |
| SQL Server | 320 | 1 | 320 | 105 ms | 1.59 / 12.7 ms | held |
| SQLite | 160 | 1 | 160 | 81 ms | 0.0342 / 0.0477 ms | held |

Highest rate held in every pass: PostgreSQL 5,000/s, every written rate, so the ceiling is above the ladder; SQL Server 320/s, every written rate, so the ceiling is above the ladder; SQLite 160/s, every written rate, so the ceiling is above the ladder.

### Ten children and a join

Do parents that fan out ten children and join them hold their offered rate with p99 under 10 s?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; workers learn of new work: in process; p99 target: 10.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Disk write, mean / p95 | Result |
|---|---:|---:|---:|---:|---:|---|
| PostgreSQL | 250 | 1 | 250 | 357 ms | 0.046 / 0.282 ms | held |
| SQL Server | 200 | 1 | 200 | 346 ms | 0.05 / 1.22 ms | held |
| SQLite | 8 | 1 | 8 | 215 ms | 0.0315 / 0.052 ms | held |

Highest rate held in every pass: PostgreSQL 250/s, every written rate, so the ceiling is above the ladder; SQL Server 200/s, every written rate, so the ceiling is above the ladder; SQLite 8/s, every written rate, so the ceiling is above the ladder.

### Controlled I/O through an idempotent sink

Do jobs that each call an external idempotent sink for 5 ms hold their offered rate with p99 under 5 s?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; workers learn of new work: in process; p99 target: 5.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Disk write, mean / p95 | Result |
|---|---:|---:|---:|---:|---:|---|
| PostgreSQL | 250 | 1 | 250 | 161 ms | 0.0212 / 0.0261 ms | held |
| SQL Server | 200 | 1 | 201 | 254 ms | 0.0251 / 0.096 ms | held |
| SQLite | 80 | 1 | 80.1 | 334 ms | 0.0335 / 0.0494 ms | held |

Highest rate held in every pass: PostgreSQL 250/s, every written rate, so the ceiling is above the ladder; SQL Server 200/s, every written rate, so the ceiling is above the ladder; SQLite 80/s, every written rate, so the ceiling is above the ladder.

### Short and long work mixed

Do no-op, CPU, sink, timer and fan-out jobs mixed in turn hold their offered rate with p99 under 5 s?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; workers learn of new work: in process; p99 target: 5.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Disk write, mean / p95 | Result |
|---|---:|---:|---:|---:|---:|---|
| PostgreSQL | 600 | 1 | 598 | 1.1 s | 0.0569 / 0.486 ms | held |
| SQL Server | 500 | 1 | 501 | 1.1 s | 0.0613 / 6.94 ms | held |
| SQLite | 80 | 1 | 80.4 | 1.2 s | 0.0317 / 0.0575 ms | held |

Highest rate held in every pass: PostgreSQL 600/s, every written rate, so the ceiling is above the ladder; SQL Server 500/s, every written rate, so the ceiling is above the ladder; SQLite 80/s, every written rate, so the ceiling is above the ladder.

### Throughput, one executor

Does a worker with a single executor keep up with a modest stream, so the result shows its serial cost?

Setup: workers: 1; namespaces: 1; executors per worker: 1; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; workers learn of new work: in process; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Disk write, mean / p95 | Result |
|---|---:|---:|---:|---:|---:|---|
| PostgreSQL | 150 | 1 | 150 | 27 ms | 0.0231 / 0.0635 ms | held |
| SQL Server | 150 | 1 | 150 | 76 ms | 0.0231 / 0.0306 ms | held |
| SQLite | 100 | 1 | 100 | 28 ms | 0.0324 / 0.047 ms | held |

Highest rate held in every pass: PostgreSQL 150/s, every written rate, so the ceiling is above the ladder; SQL Server 150/s, every written rate, so the ceiling is above the ladder; SQLite 100/s, every written rate, so the ceiling is above the ladder.

### One hot lane

How fast does one ordered lane hand its jobs over, each waiting for the previous completion to commit?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; workers learn of new work: in process; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Disk write, mean / p95 | Result |
|---|---:|---:|---:|---:|---:|---|
| PostgreSQL | 60 | 1 | 60 | 24 ms | 0.0252 / 0.0477 ms | held |
| SQL Server | 100 | 1 | 100 | 28 ms | 0.0226 / 0.0259 ms | held |
| SQLite | 80 | 1 | 80 | 41 ms | 0.0349 / 0.054 ms | held |

Highest rate held in every pass: PostgreSQL 60/s, every written rate, so the ceiling is above the ladder; SQL Server 100/s, every written rate, so the ceiling is above the ladder; SQLite 80/s, every written rate, so the ceiling is above the ladder.

### One hot concurrency key

With every job sharing one concurrency key of limit one, does a stream of one a second keep a flat backlog with p99 under 30 s?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; workers learn of new work: in process; p99 target: 30.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Disk write, mean / p95 | Result |
|---|---:|---:|---:|---:|---:|---|
| PostgreSQL | 1 | 1 | 1 | 41 ms | 1.13 / 1.84 ms | held |
| SQL Server | 1 | 1 | 1 | 45 ms | 1.29 / 2.17 ms | held |
| SQLite | 4 | 1 | 4 | 33 ms | 0.18 / 1.06 ms | held |

Highest rate held in every pass: PostgreSQL 1/s, every written rate, so the ceiling is above the ladder; SQL Server 1/s, every written rate, so the ceiling is above the ladder; SQLite 4/s, every written rate, so the ceiling is above the ladder.

### Five-minute mixed soak

Does the mixed workload hold a steady rate for five minutes with p99 under 5 s and a flat backlog?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; workers learn of new work: in process; p99 target: 5.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Disk write, mean / p95 | Result |
|---|---:|---:|---:|---:|---:|---|
| PostgreSQL | 700 | 1 | 700 | 1.2 s | 0.0699 / 0.457 ms | held |
| SQL Server | 350 | 1 | 350 | 1.1 s | 0.0897 / 0.425 ms | held |
| SQLite | 56 | 1 | 56 | 1.1 s | 0.0324 / 0.041 ms | held |

Highest rate held in every pass: PostgreSQL 700/s, every written rate, so the ceiling is above the ladder; SQL Server 350/s, every written rate, so the ceiling is above the ladder; SQLite 56/s, every written rate, so the ceiling is above the ladder.

### Overload, then recovery

Offered more than it can run, does the backlog grow, and does it fall back once the offered rate drops?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Disk write, mean / p95 | Result |
|---|---:|---:|---:|---:|---:|---|
| PostgreSQL | 13,000, then 2,500 | 1 | 7,749 | 8.9 s | 0.0896 / 0.0503 ms | pass: the backlog grew, then fell back |
| SQL Server | 8,000, then 1,000 | 1 | 4,500 | 10.8 s | 0.192 / 2.09 ms | pass: the backlog grew, then fell back |
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
- Commit `d74d9393e52ce667b202cc0cad49140e210ada35`, measurement version 3, written standard `a7681504a7f6`.
- Machine: Intel(R) Core(TM) i9-14900K, 24 cores, 32 logical processors, Microsoft Windows 10.0.26300, .NET 10.0.15.
- Docker VM: 32 CPUs, 46.9 GB; measured containers capped at PostgreSQL 32031 MB, SQL Server 32031 MB.
- PostgreSQL: peak 519 database connections; disk before measuring: quiet before 33 of 33 cells.
- SQL Server: peak 534 database connections; disk before measuring: quiet before 32 of 33 cells.
- SQLite: peak 0 database connections; disk before measuring: quiet before 25 of 25 cells.
- Wall time: performance on PostgreSQL 2 h 10 min, correctness on PostgreSQL 6 min, chaos on PostgreSQL 19 min, performance on SQL Server 2 h 2 min, correctness on SQL Server 10 min, chaos on SQL Server 47 min, performance on SQLite 1 h 20 min, correctness on SQLite 4 min, chaos on SQLite 7 min; correctness runs on every database at once, chaos one database at a time.
- Evidence: 29.5 MB of a 50 MB budget.
- A rate holds when every scheduled arrival reached an outcome and none was refused, the backlog trend stayed within 1% of the rate, p99 stayed within the target, at most 0.1% of arrivals were picked up late by the harness, and every job reconciled. These are the rc.6 criteria.
- p99 runs from an arrival's scheduled instant to its job's durable completion, or to the enqueue call's answer in an enqueue-only scenario. A scenario that streams arrivals to one worker in one namespace runs that worker in the process that enqueues, as a single Acta host does: it shares that process's heap and thread pool and the enqueue calls' connection pool, and each enqueue wakes it at once. A worker process claims again while its claims return jobs; after an empty claim it waits up to its 1 s safety poll, or until Redis wakes it where the setup says so.

| Disk, by phase and drive | Windows counters |
|---|---|
| before \| 0 c: | write 1.81 ms mean, 7.60 ms p95, 7 writes/s, 99% idle, 14 samples |
| before \| 1 d: | write 5.92 ms mean, 8.64 ms p95, 12 writes/s, 99% idle, 14 samples |
| chaos mssql \| 0 c: | write 3.01 ms mean, 7.94 ms p95, 6 writes/s, 99% idle, 1425 samples |
| chaos mssql \| 1 d: | write 0.32 ms mean, 1.26 ms p95, 502 writes/s, 74% idle, 1425 samples |
| chaos pg \| 0 c: | write 2.92 ms mean, 8.00 ms p95, 9 writes/s, 99% idle, 596 samples |
| chaos pg \| 1 d: | write 0.06 ms mean, 0.08 ms p95, 797 writes/s, 52% idle, 596 samples |
| chaos sqlite \| 0 c: | write 2.82 ms mean, 8.11 ms p95, 8 writes/s, 99% idle, 213 samples |
| chaos sqlite \| 1 d: | write 0.62 ms mean, 4.65 ms p95, 1205 writes/s, 55% idle, 213 samples |
| correctness \| 0 c: | write 3.18 ms mean, 7.98 ms p95, 8 writes/s, 99% idle, 321 samples |
| correctness \| 1 d: | write 0.32 ms mean, 1.60 ms p95, 495 writes/s, 84% idle, 321 samples |
| mssql \| 0 c: | write 3.04 ms mean, 7.92 ms p95, 7 writes/s, 99% idle, 3673 samples |
| mssql \| 1 d: | write 1.22 ms mean, 6.56 ms p95, 1462 writes/s, 64% idle, 3673 samples |
| pg \| 0 c: | write 3.08 ms mean, 7.89 ms p95, 7 writes/s, 99% idle, 3905 samples |
| pg \| 1 d: | write 1.00 ms mean, 6.15 ms p95, 722 writes/s, 70% idle, 3905 samples |
| sqlite \| 0 c: | write 1.92 ms mean, 7.71 ms p95, 13 writes/s, 99% idle, 2412 samples |
| sqlite \| 1 d: | write 0.63 ms mean, 5.80 ms p95, 737 writes/s, 72% idle, 2412 samples |
| after \| 0 c: | write 3.32 ms mean, 8.49 ms p95, 9 writes/s, 99% idle, 15 samples |
| after \| 1 d: | write 4.94 ms mean, 7.11 ms p95, 8 writes/s, 100% idle, 15 samples |
