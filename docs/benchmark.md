# Acta 1.0.0-rc.8: benchmark and certification

The 1.0.0-rc.8 release evidence on one machine, in two Acta Verify bundles. The release run on `be95c5098` ran every scenario of the release suite on PostgreSQL, SQL Server, and SQLite: every correctness, fault, and chaos scenario passed, and every trial's ledger passed the certification checks. The one case it could not judge was overload on SQLite, and finding out why found a defect: SQLite's busy handler let a steady flood of enqueues keep a worker's claim off the lock. `e8cc81d90` queues a process's writes to one SQLite file in arrival order, and the SQLite release run on it passed every case it judges. Its SQLite figures replace the first report's. Both reports follow, SQLite's last.

- The PostgreSQL and SQL Server figures come from `be95c5098`. The commits after it change operation SQL, the producer's outbox table, and SQLite's write path, not Acta's schema.
- On SQLite the queue raised the drain from 4,931 to 8,418 jobs/s and the sustained throughput from 50.3 to 200 jobs/s, the top of the ladder, and cut enqueue p99 at 200/s from 658 ms to 23 ms.
- Overload, then recovery reads inconclusive on SQLite in both runs. SQLite now absorbs 1,000 jobs/s, and past that the load generator, which shares the one writer, queues the excess in the harness, not the database. `c84843c19`, which changes only Acta Verify, takes the case off SQLite.
- Against rc.7, the release run's SQL Server drains with one and four workers read 25% and 15% slower. Rerun back to back on both commits, twice, they matched: one worker 14,011 and 13,667 jobs/s on rc.7, 14,138 and 14,019 on rc.8; four workers 33,914 and 34,922 on rc.7, 33,484 and 34,665 on rc.8.

# Acta Verify: INCOMPLETE

The release suite on PostgreSQL, SQL Server, SQLite, commit `be95c5098`, started 2026-10-31 15:52 UTC, 3 h 26 min, on Intel(R) Core(TM) i9-14900K.

| | PostgreSQL | SQL Server | SQLite |
|---|---|---|---|
| Correctness | pass | pass | pass |
| Crash-recovery chaos | pass, 1,000,000 jobs, 84 busy kills reclaimed | pass, 1,000,000 jobs, 83 busy kills reclaimed | pass, 10,000 jobs, 39 busy kills reclaimed |
| Ledger checks | pass, 131 trials checked | pass, 121 trials checked | pass, 106 trials checked |
| Performance floors | held | held | held |
| Drain | 70,223 jobs/s | 45,636 jobs/s | 4,931 jobs/s |
| Throughput | 12,519 jobs/s | 5,001 jobs/s | 50.3 jobs/s |
| Throughput, one worker | 9,946 jobs/s | 5,019 jobs/s | not written |

> **12,519 fully executed, durably completed jobs per second** on PostgreSQL: 4 workers, one job per enqueue call, held 3 × 30 s and 120 s at 12,500/s offered, p99 1.0 s, Intel(R) Core(TM) i9-14900K.

> **5,001 fully executed, durably completed jobs per second** on SQL Server: 4 workers, one job per enqueue call, held 3 × 30 s and 120 s at 5,000/s offered, p99 951 ms, Intel(R) Core(TM) i9-14900K.

> **50.3 fully executed, durably completed jobs per second** on SQLite: 1 worker, one job per enqueue call, held 3 × 30 s and 120 s at 50/s offered, p99 1.1 s, Intel(R) Core(TM) i9-14900K.

> **9,946 fully executed, durably completed jobs per second** on PostgreSQL: 1 worker, one job per enqueue call, held 3 × 30 s and 120 s at 10,000/s offered, p99 1.9 s, Intel(R) Core(TM) i9-14900K.

> **5,019 fully executed, durably completed jobs per second** on SQL Server: 1 worker, one job per enqueue call, held 3 × 30 s and 120 s at 5,000/s offered, p99 1.2 s, Intel(R) Core(TM) i9-14900K.

## Performance

Each scenario's first written rate is its floor, which this suite requires to hold.

### Drain

How fast do eight workers over two namespaces run and durably complete a backlog enqueued before they start?

Setup: workers: 8 on PostgreSQL, 8 on SQL Server, 1 on SQLite; namespaces: 2 on PostgreSQL, 2 on SQL Server, 1 on SQLite; executors per worker: the product default; enqueue calls in flight: 8; jobs per call: 1,000.

| Database | Pass | Jobs | Steady rate, database clock | Whole cohort | Result |
|---|---:|---:|---:|---:|---|
| PostgreSQL | 1 | 1,000,000 | 61,761 jobs/s | 17.3 s | held |
| PostgreSQL | 2 | 1,000,000 | 70,223 jobs/s | 14.9 s | held |
| PostgreSQL | 3 | 1,000,000 | 82,192 jobs/s | 13 s | held |
| SQL Server | 1 | 1,000,000 | 43,338 jobs/s | 26.3 s | held |
| SQL Server | 2 | 1,000,000 | 47,233 jobs/s | 21.9 s | held |
| SQL Server | 3 | 1,000,000 | 45,636 jobs/s | 21.8 s | held |
| SQLite | 1 | 100,000 | 5,001 jobs/s | 20.7 s | held |
| SQLite | 2 | 100,000 | 4,809 jobs/s | 21 s | held |
| SQLite | 3 | 100,000 | 4,931 jobs/s | 20.2 s | held |

### Sustained throughput

How many jobs per second, one per enqueue call, does Acta enqueue, run and durably complete with a flat backlog and p99 under 2 s?

Setup: workers: 4 on PostgreSQL, 4 on SQL Server, 1 on SQLite; namespaces: 2 on PostgreSQL, 2 on SQL Server, 1 on SQLite; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Result |
|---|---:|---:|---:|---:|---|
| PostgreSQL | 10,000 | 1 | 10,069 | 1.0 s | held |
| PostgreSQL | 10,000 | 2 | 10,052 | 1.0 s | held |
| PostgreSQL | 10,000 | 3 | 9,987 | 1.0 s | held |
| PostgreSQL | 12,500 | 1 | 12,324 | 1.0 s | held |
| PostgreSQL | 12,500 | 2 | 12,195 | 1.0 s | held |
| PostgreSQL | 12,500 | 3 | 12,666 | 1.0 s | held |
| PostgreSQL | 15,000 | 1 | 14,646 | 1.0 s | held |
| PostgreSQL | 15,000 | 2 | 14,997 | 956 ms | held |
| PostgreSQL | 15,000 | 3 | 14,813 | 1.0 s | held |
| PostgreSQL | 17,500 | 1 | 17,260 | 1.7 s | missed: backlog grew 216 jobs/s, 175 allowed |
| PostgreSQL | 17,500 | 2 | 16,738 | 2.3 s | missed: backlog grew 776 jobs/s, 175 allowed |
| PostgreSQL | 17,500 | 3 | 16,828 | 2.5 s | missed: backlog grew 852 jobs/s, 175 allowed |
| PostgreSQL | 20,000 | | | | not run by rule |
| PostgreSQL | 25,000 | | | | not run by rule |
| PostgreSQL | 30,000 | | | | not run by rule |
| PostgreSQL | 15,000 | confirm, 120 s | 14,658 | 3.1 s | missed: p99 3.1 s against a 2.0 s target |
| PostgreSQL | 12,500 | confirm, 120 s | 12,519 | 1.0 s | held |
| SQL Server | 5,000 | 1 | 5,002 | 931 ms | held |
| SQL Server | 5,000 | 2 | 4,925 | 967 ms | held |
| SQL Server | 5,000 | 3 | 5,026 | 676 ms | held |
| SQL Server | 7,500 | 1 | 6,105 | 8.3 s | missed: backlog grew 1,404 jobs/s, 75 allowed |
| SQL Server | 7,500 | 2 | 6,207 | 7.7 s | missed: backlog grew 1,318 jobs/s, 75 allowed |
| SQL Server | 7,500 | 3 | 6,407 | 6.9 s | missed: backlog grew 1,076 jobs/s, 75 allowed |
| SQL Server | 10,000 | | | | not run by rule |
| SQL Server | 12,500 | | | | not run by rule |
| SQL Server | 15,000 | | | | not run by rule |
| SQL Server | 5,000 | confirm, 120 s | 5,001 | 951 ms | held |
| SQLite | 25 | 1 | 25.2 | 1.1 s | held |
| SQLite | 25 | 2 | 25 | 1.1 s | held |
| SQLite | 25 | 3 | 25.7 | 1.1 s | held |
| SQLite | 50 | 1 | 50 | 1.1 s | held |
| SQLite | 50 | 2 | 51 | 1.1 s | held |
| SQLite | 50 | 3 | 51.2 | 1.1 s | held |
| SQLite | 100 | 1 | 103 | 3.7 s | missed: p99 3.7 s against a 2.0 s target |
| SQLite | 100 | 2 | 106 | 4.4 s | missed: p99 4.4 s against a 2.0 s target |
| SQLite | 100 | 3 | 102 | 2.3 s | missed: p99 2.3 s against a 2.0 s target |
| SQLite | 200 | | | | not run by rule |
| SQLite | 400 | | | | not run by rule |
| SQLite | 50 | confirm, 120 s | 50.3 | 1.1 s | held |

Highest rate held in every pass: PostgreSQL 15,000/s; SQL Server 5,000/s; SQLite 50/s.

### Sustained throughput, one worker

How many jobs per second does one worker process of default executors run and durably complete, one job per enqueue call, with a flat backlog and p99 under 2 s?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Result |
|---|---:|---:|---:|---:|---|
| PostgreSQL | 5,000 | 1 | 4,877 | 1.0 s | held |
| PostgreSQL | 5,000 | 2 | 5,100 | 1.0 s | held |
| PostgreSQL | 5,000 | 3 | 5,084 | 1.0 s | held |
| PostgreSQL | 7,500 | 1 | 7,423 | 1.0 s | held |
| PostgreSQL | 7,500 | 2 | 7,533 | 1.0 s | held |
| PostgreSQL | 7,500 | 3 | 7,440 | 1.0 s | held |
| PostgreSQL | 10,000 | 1 | 10,080 | 1.0 s | held |
| PostgreSQL | 10,000 | 2 | 9,788 | 1.0 s | held |
| PostgreSQL | 10,000 | 3 | 10,207 | 1.0 s | held |
| PostgreSQL | 12,500 | 1 | 11,187 | 4.7 s | missed: backlog grew 1,308 jobs/s, 125 allowed |
| PostgreSQL | 12,500 | 2 | 10,957 | 5.4 s | missed: backlog grew 1,527 jobs/s, 125 allowed |
| PostgreSQL | 12,500 | 3 | 11,136 | 4.2 s | missed: backlog grew 1,383 jobs/s, 125 allowed |
| PostgreSQL | 15,000 | | | | not run by rule |
| PostgreSQL | 10,000 | confirm, 120 s | 9,946 | 1.9 s | held |
| SQL Server | 2,500 | 1 | 2,505 | 1.0 s | held |
| SQL Server | 2,500 | 2 | 2,427 | 1.1 s | held |
| SQL Server | 2,500 | 3 | 2,543 | 1.1 s | held |
| SQL Server | 5,000 | 1 | 5,004 | 906 ms | held |
| SQL Server | 5,000 | 2 | 5,084 | 411 ms | held |
| SQL Server | 5,000 | 3 | 5,045 | 211 ms | held |
| SQL Server | 7,500 | 1 | 5,988 | 9.9 s | missed: backlog grew 1,389 jobs/s, 75 allowed |
| SQL Server | 7,500 | 2 | 5,764 | 10.3 s | missed: backlog grew 1,621 jobs/s, 75 allowed |
| SQL Server | 7,500 | 3 | 5,724 | 10.4 s | missed: backlog grew 1,666 jobs/s, 75 allowed |
| SQL Server | 10,000 | | | | not run by rule |
| SQL Server | 5,000 | confirm, 120 s | 5,019 | 1.2 s | held |
| SQLite | | | | | The scenario writes no rate for sqlite. |

Highest rate held in every pass: PostgreSQL 10,000/s; SQL Server 5,000/s.

### Enqueue, one job per call

How fast are single-job enqueue calls acknowledged, each in its own transaction, before any worker runs?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Result |
|---|---:|---:|---:|---:|---|
| PostgreSQL | 10,000 | 1 | 0 | 25 ms | held |
| SQL Server | 5,000 | 1 | 0 | 31 ms | held |
| SQLite | 200 | 1 | 0 | 658 ms | held |

Highest rate held in every pass: PostgreSQL 10,000/s, every written rate, so the ceiling is above the ladder; SQL Server 5,000/s, every written rate, so the ceiling is above the ladder; SQLite 200/s, every written rate, so the ceiling is above the ladder.

### Enqueue in batches of 100

How fast are batches of 100 jobs acknowledged, before any worker runs?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 100; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Result |
|---|---:|---:|---:|---:|---|
| PostgreSQL | 10,000 | 1 | 0 | 36 ms | held |
| SQL Server | 5,000 | 1 | 0 | 41 ms | held |
| SQLite | 5,000 | 1 | 0 | 24 ms | held |

Highest rate held in every pass: PostgreSQL 10,000/s, every written rate, so the ceiling is above the ladder; SQL Server 5,000/s, every written rate, so the ceiling is above the ladder; SQLite 5,000/s, every written rate, so the ceiling is above the ladder.

### Enqueue in batches of 1,000

How fast are batches of 1,000 jobs acknowledged, before any worker runs?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1,000; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Result |
|---|---:|---:|---:|---:|---|
| PostgreSQL | 20,000 | 1 | 0 | 247 ms | held |
| SQL Server | 10,000 | 1 | 0 | 218 ms | held |
| SQLite | 10,000 | 1 | 0 | 73 ms | held |

Highest rate held in every pass: PostgreSQL 20,000/s, every written rate, so the ceiling is above the ladder; SQL Server 10,000/s, every written rate, so the ceiling is above the ladder; SQLite 10,000/s, every written rate, so the ceiling is above the ladder.

### Drain, one worker

How fast does one worker run and durably complete a backlog enqueued before it starts?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 8; jobs per call: 1,000.

| Database | Pass | Jobs | Steady rate, database clock | Whole cohort | Result |
|---|---:|---:|---:|---:|---|
| PostgreSQL | 1 | 200,000 | 20,952 jobs/s | 10.2 s | held |
| SQL Server | 1 | 200,000 | 11,085 jobs/s | 18.6 s | held |
| SQLite | 1 | 50,000 | 5,625 jobs/s | 9.07 s | held |

### Drain, four workers

How fast do four workers over two namespaces drain a backlog enqueued before they start?

Setup: workers: 4; namespaces: 2; executors per worker: the product default; enqueue calls in flight: 8; jobs per call: 1,000.

| Database | Pass | Jobs | Steady rate, database clock | Whole cohort | Result |
|---|---:|---:|---:|---:|---|
| PostgreSQL | 1 | 200,000 | 43,224 jobs/s | 4.66 s | held |
| SQL Server | 1 | 200,000 | 33,380 jobs/s | 6.09 s | held |
| SQLite | | | | | The scenario writes no cohort for sqlite. |

### Drain, sixteen workers

How fast do sixteen workers over two namespaces drain a backlog enqueued before they start?

Setup: workers: 16; namespaces: 2; executors per worker: the product default; enqueue calls in flight: 8; jobs per call: 1,000.

| Database | Pass | Jobs | Steady rate, database clock | Whole cohort | Result |
|---|---:|---:|---:|---:|---|
| PostgreSQL | 1 | 1,000,000 | 73,732 jobs/s | 14.4 s | held |
| SQL Server | 1 | 1,000,000 | 48,719 jobs/s | 21.6 s | held |
| SQLite | | | | | The scenario writes no cohort for sqlite. |

### Throughput, sixteen workers

Do sixteen worker processes over two namespaces hold a high offered rate with p99 under 2 s?

Setup: workers: 16; namespaces: 2; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Result |
|---|---:|---:|---:|---:|---|
| PostgreSQL | 15,000 | 1 | 15,444 | 1.6 s | held |
| SQL Server | 4,000 | 1 | 4,002 | 787 ms | held |
| SQLite | | | | | The scenario writes no rate for sqlite. |

Highest rate held in every pass: PostgreSQL 15,000/s, every written rate, so the ceiling is above the ladder; SQL Server 4,000/s, every written rate, so the ceiling is above the ladder.

### Throughput, sixteen workers on one namespace

Do sixteen worker processes sharing one namespace hold a high offered rate with p99 under 2 s?

Setup: workers: 16; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Result |
|---|---:|---:|---:|---:|---|
| PostgreSQL | 15,000 | 1 | 15,050 | 1.1 s | held |
| SQL Server | 4,000 | 1 | 4,022 | 931 ms | held |
| SQLite | | | | | The scenario writes no rate for sqlite. |

Highest rate held in every pass: PostgreSQL 15,000/s, every written rate, so the ceiling is above the ladder; SQL Server 4,000/s, every written rate, so the ceiling is above the ladder.

### Throughput, 32 executors

Does one worker with 32 executors hold the stream its default configuration holds?

Setup: workers: 1; namespaces: 1; executors per worker: 32; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Result |
|---|---:|---:|---:|---:|---|
| PostgreSQL | 5,000 | 1 | 4,974 | 1.0 s | held |
| SQL Server | 2,500 | 1 | 2,495 | 1.1 s | held |
| SQLite | 40 | 1 | 40.7 | 1.1 s | held |

Highest rate held in every pass: PostgreSQL 5,000/s, every written rate, so the ceiling is above the ladder; SQL Server 2,500/s, every written rate, so the ceiling is above the ladder; SQLite 40/s, every written rate, so the ceiling is above the ladder.

### Three persisted steps

Do jobs that commit three checkpoints each hold their offered rate with p99 under 5 s?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; p99 target: 5.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Result |
|---|---:|---:|---:|---:|---|
| PostgreSQL | 1,000 | 1 | 995 | 1.1 s | held |
| SQL Server | 1,000 | 1 | 1,016 | 1.1 s | held |
| SQLite | 15 | 1 | 15.1 | 1.6 s | held |

Highest rate held in every pass: PostgreSQL 1,000/s, every written rate, so the ceiling is above the ladder; SQL Server 1,000/s, every written rate, so the ceiling is above the ladder; SQLite 15/s, every written rate, so the ceiling is above the ladder.

### Transient failure and retry

With one job in four failing twice before it succeeds, does the stream hold its rate with p99 under 10 s?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; p99 target: 10.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Result |
|---|---:|---:|---:|---:|---|
| PostgreSQL | 5,000 | 1 | 5,002 | 2.2 s | held |
| SQL Server | 2,500 | 1 | 2,499 | 2.2 s | held |
| SQLite | 40 | 1 | 40.4 | 2.7 s | held |

Highest rate held in every pass: PostgreSQL 5,000/s, every written rate, so the ceiling is above the ladder; SQL Server 2,500/s, every written rate, so the ceiling is above the ladder; SQLite 40/s, every written rate, so the ceiling is above the ladder.

### Fixed CPU work

Do jobs that each hash for a fixed number of rounds hold their offered rate with p99 under 2 s?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Result |
|---|---:|---:|---:|---:|---|
| PostgreSQL | 4,000 | 1 | 3,973 | 1.0 s | held |
| SQL Server | 2,500 | 1 | 2,569 | 1.1 s | held |
| SQLite | 40 | 1 | 40.9 | 1.1 s | held |

Highest rate held in every pass: PostgreSQL 4,000/s, every written rate, so the ceiling is above the ladder; SQL Server 2,500/s, every written rate, so the ceiling is above the ladder; SQLite 40/s, every written rate, so the ceiling is above the ladder.

### Payloads of 256 B

Do jobs carrying a 256-byte payload hold their offered rate with p99 under 2 s?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Result |
|---|---:|---:|---:|---:|---|
| PostgreSQL | 4,000 | 1 | 4,042 | 1.0 s | held |
| SQL Server | 2,500 | 1 | 2,472 | 1.0 s | held |
| SQLite | 30 | 1 | 30.7 | 1.1 s | held |

Highest rate held in every pass: PostgreSQL 4,000/s, every written rate, so the ceiling is above the ladder; SQL Server 2,500/s, every written rate, so the ceiling is above the ladder; SQLite 30/s, every written rate, so the ceiling is above the ladder.

### Payloads of 4 KB

Do jobs carrying a 4 KB payload hold their offered rate with p99 under 2 s?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Result |
|---|---:|---:|---:|---:|---|
| PostgreSQL | 5,000 | 1 | 4,913 | 1.0 s | held |
| SQL Server | 2,500 | 1 | 2,455 | 1.0 s | held |
| SQLite | 30 | 1 | 30.4 | 1.1 s | held |

Highest rate held in every pass: PostgreSQL 5,000/s, every written rate, so the ceiling is above the ladder; SQL Server 2,500/s, every written rate, so the ceiling is above the ladder; SQLite 30/s, every written rate, so the ceiling is above the ladder.

### Payloads of 256 B, 4 KB and 64 KB mixed

Do jobs carrying payloads of three sizes in turn hold their offered rate with p99 under 2 s?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Result |
|---|---:|---:|---:|---:|---|
| PostgreSQL | 2,500 | 1 | 2,527 | 1.0 s | held |
| SQL Server | 1,000 | 1 | 983 | 1.1 s | held |
| SQLite | 40 | 1 | 40.8 | 1.1 s | held |

Highest rate held in every pass: PostgreSQL 2,500/s, every written rate, so the ceiling is above the ladder; SQL Server 1,000/s, every written rate, so the ceiling is above the ladder; SQLite 40/s, every written rate, so the ceiling is above the ladder.

### Payloads of 64 KB

Do jobs carrying a 64 KB payload hold their offered rate with p99 under 2 s?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Result |
|---|---:|---:|---:|---:|---|
| PostgreSQL | 1,000 | 1 | 980 | 1.1 s | held |
| SQL Server | 500 | 1 | 513 | 1.1 s | held |
| SQLite | 10 | 1 | 10.2 | 1.1 s | held |

Highest rate held in every pass: PostgreSQL 1,000/s, every written rate, so the ceiling is above the ladder; SQL Server 500/s, every written rate, so the ceiling is above the ladder; SQLite 10/s, every written rate, so the ceiling is above the ladder.

### A thousand lanes

Spread over a thousand ordered lanes, does the stream hold its rate with p99 under 2 s?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Result |
|---|---:|---:|---:|---:|---|
| PostgreSQL | 4,000 | 1 | 4,052 | 1.0 s | held |
| SQL Server | 1,000 | 1 | 976 | 1.0 s | held |
| SQLite | 30 | 1 | 30.2 | 1.1 s | held |

Highest rate held in every pass: PostgreSQL 4,000/s, every written rate, so the ceiling is above the ladder; SQL Server 1,000/s, every written rate, so the ceiling is above the ladder; SQLite 30/s, every written rate, so the ceiling is above the ladder.

### Operator queries during processing

While the stream runs, do the operator list queries answer quickly, and does the stream keep its rate?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Result |
|---|---:|---:|---:|---:|---|
| PostgreSQL | 5,000 | 1 | 4,930 | 1.0 s | held |
| SQL Server | 2,500 | 1 | 2,540 | 1.1 s | held |
| SQLite | 30 | 1 | 30.1 | 1.1 s | held |

Highest rate held in every pass: PostgreSQL 5,000/s, every written rate, so the ceiling is above the ladder; SQL Server 2,500/s, every written rate, so the ceiling is above the ladder; SQLite 30/s, every written rate, so the ceiling is above the ladder.

### Parked work alongside

With a large population of jobs parked on signals, does the stream hold its rate with p99 under 2 s?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Result |
|---|---:|---:|---:|---:|---|
| PostgreSQL | 5,000 | 1 | 5,102 | 1.0 s | held |
| SQL Server | 500 | 1 | 486 | 1.0 s | held |
| SQLite | 40 | 1 | 40.6 | 1.1 s | held |

Highest rate held in every pass: PostgreSQL 5,000/s, every written rate, so the ceiling is above the ladder; SQL Server 500/s, every written rate, so the ceiling is above the ladder; SQLite 40/s, every written rate, so the ceiling is above the ladder.

### Future high-priority work alongside

With a large population of high-priority jobs due tomorrow, does the stream hold its rate with p99 under 2 s?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Result |
|---|---:|---:|---:|---:|---|
| PostgreSQL | 5,000 | 1 | 5,039 | 1.0 s | held |
| SQL Server | 320 | 1 | 317 | 1.0 s | held |
| SQLite | 40 | 1 | 40.6 | 1.1 s | held |

Highest rate held in every pass: PostgreSQL 5,000/s, every written rate, so the ceiling is above the ladder; SQL Server 320/s, every written rate, so the ceiling is above the ladder; SQLite 40/s, every written rate, so the ceiling is above the ladder.

### Ten children and a join

Do parents that fan out ten children and join them hold their offered rate with p99 under 10 s?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; p99 target: 10.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Result |
|---|---:|---:|---:|---:|---|
| PostgreSQL | 250 | 1 | 249 | 307 ms | held |
| SQL Server | 200 | 1 | 202 | 400 ms | held |
| SQLite | 2 | 1 | 1.97 | 1.2 s | held |

Highest rate held in every pass: PostgreSQL 250/s, every written rate, so the ceiling is above the ladder; SQL Server 200/s, every written rate, so the ceiling is above the ladder; SQLite 2/s, every written rate, so the ceiling is above the ladder.

### Controlled I/O through an idempotent sink

Do jobs that each call an external idempotent sink for 5 ms hold their offered rate with p99 under 5 s?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; p99 target: 5.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Result |
|---|---:|---:|---:|---:|---|
| PostgreSQL | 250 | 1 | 248 | 1.1 s | held |
| SQL Server | 200 | 1 | 198 | 1.1 s | held |
| SQLite | 20 | 1 | 20.2 | 2.3 s | held |

Highest rate held in every pass: PostgreSQL 250/s, every written rate, so the ceiling is above the ladder; SQL Server 200/s, every written rate, so the ceiling is above the ladder; SQLite 20/s, every written rate, so the ceiling is above the ladder.

### Short and long work mixed

Do no-op, CPU, sink, timer and fan-out jobs mixed in turn hold their offered rate with p99 under 5 s?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; p99 target: 5.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Result |
|---|---:|---:|---:|---:|---|
| PostgreSQL | 600 | 1 | 600 | 1.1 s | held |
| SQL Server | 500 | 1 | 500 | 1.1 s | held |
| SQLite | 20 | 1 | 19.8 | 1.6 s | held |

Highest rate held in every pass: PostgreSQL 600/s, every written rate, so the ceiling is above the ladder; SQL Server 500/s, every written rate, so the ceiling is above the ladder; SQLite 20/s, every written rate, so the ceiling is above the ladder.

### Throughput, one executor

Does a worker with a single executor keep up with a modest stream, so the result shows its serial cost?

Setup: workers: 1; namespaces: 1; executors per worker: 1; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Result |
|---|---:|---:|---:|---:|---|
| PostgreSQL | 150 | 1 | 152 | 1.0 s | held |
| SQL Server | 150 | 1 | 148 | 1.1 s | held |
| SQLite | 25 | 1 | 25.1 | 1.1 s | held |

Highest rate held in every pass: PostgreSQL 150/s, every written rate, so the ceiling is above the ladder; SQL Server 150/s, every written rate, so the ceiling is above the ladder; SQLite 25/s, every written rate, so the ceiling is above the ladder.

### One hot lane

How fast does one ordered lane hand its jobs over, each waiting for the previous completion to commit?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Result |
|---|---:|---:|---:|---:|---|
| PostgreSQL | 60 | 1 | 58.2 | 1.0 s | held |
| SQL Server | 100 | 1 | 100 | 1.1 s | held |
| SQLite | 20 | 1 | 20.1 | 1.1 s | held |

Highest rate held in every pass: PostgreSQL 60/s, every written rate, so the ceiling is above the ladder; SQL Server 100/s, every written rate, so the ceiling is above the ladder; SQLite 20/s, every written rate, so the ceiling is above the ladder.

### One hot concurrency key

With every job sharing one concurrency key of limit one, does a stream of one a second keep a flat backlog with p99 under 30 s?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; p99 target: 30.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Result |
|---|---:|---:|---:|---:|---|
| PostgreSQL | 1 | 1 | 0.967 | 11.3 s | held |
| SQL Server | 1 | 1 | 0.967 | 6.5 s | held |
| SQLite | 1 | 1 | 1 | 1.0 s | held |

Highest rate held in every pass: PostgreSQL 1/s, every written rate, so the ceiling is above the ladder; SQL Server 1/s, every written rate, so the ceiling is above the ladder; SQLite 1/s, every written rate, so the ceiling is above the ladder.

### Five-minute mixed soak

Does the mixed workload hold a steady rate for five minutes with p99 under 5 s and a flat backlog?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; p99 target: 5.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Result |
|---|---:|---:|---:|---:|---|
| PostgreSQL | 700 | 1 | 700 | 1.2 s | held |
| SQL Server | 350 | 1 | 350 | 1.1 s | held |
| SQLite | 14 | 1 | 14 | 1.9 s | held |

Highest rate held in every pass: PostgreSQL 700/s, every written rate, so the ceiling is above the ladder; SQL Server 350/s, every written rate, so the ceiling is above the ladder; SQLite 14/s, every written rate, so the ceiling is above the ladder.

### Overload, then recovery

Offered more than it can run, does the backlog grow, and does it fall back once the offered rate drops?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Result |
|---|---:|---:|---:|---:|---|
| PostgreSQL | 13,000, then 2,500 | 1 | 7,749 | 11.5 s | pass: the backlog grew, then fell back |
| SQL Server | 8,000, then 1,000 | 1 | 4,499 | 14.3 s | pass: the backlog grew, then fell back |
| SQLite | 800, then 50 | 1 | 425 | 4.5 s | incomplete: The offered overload of 800 jobs/s did not grow the backlog during the overload half. |

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

- **Overload, then recovery** on SQLite, incomplete: The offered overload of 800 jobs/s did not grow the backlog during the overload half. (`histories/sqlite-overload-then-recover-measure-1-50-checks.json`)

## How this run was made

- Command: `acta-verify release --db all`.
- Commit `be95c5098926600cffba6340e5d827d98c75dd00`, measurement version 2, written standard `56961845f991`.
- Machine: Intel(R) Core(TM) i9-14900K, 24 cores, 32 logical processors, Microsoft Windows 10.0.26300, .NET 10.0.12.
- Docker VM: 32 CPUs, 46.9 GB; measured containers capped at PostgreSQL 32031 MB, SQL Server 32031 MB.
- PostgreSQL: peak 325 database connections; disk before measuring: no, after 62 s: too few samples; writing most: chatgpt 0.0 MB/s, chatgpt 0.0 MB/s, opera 0.0 MB/s.
- SQL Server: peak 335 database connections; disk before measuring: yes, after 12 s.
- SQLite: peak 0 database connections; disk before measuring: no, after 62 s: too few samples; writing most: opera 4.1 MB/s, opera 0.4 MB/s, opera 0.4 MB/s.
- Wall time: performance on PostgreSQL 57 min, correctness on PostgreSQL 5 min, chaos on PostgreSQL 18 min, performance on SQL Server 47 min, correctness on SQL Server 10 min, chaos on SQL Server 27 min, performance on SQLite 30 min, correctness on SQLite 5 min, chaos on SQLite 13 min; correctness runs on every database at once, chaos one database at a time.
- Evidence: 19.3 MB of a 50 MB budget.
- A rate holds when every scheduled arrival reached an outcome and none was refused, the backlog trend stayed within 1% of the rate, p99 stayed within the target, at most 0.1% of arrivals were picked up late by the harness, and every job reconciled. These are the rc.6 criteria.

| Disk, by phase and drive | Windows counters |
|---|---|
| before \| 0 c: | write 2.24 ms mean, 4.97 ms p95, 10 writes/s, 99% idle, 14 samples |
| before \| 1 d: | write 4.87 ms mean, 7.44 ms p95, 44 writes/s, 99% idle, 14 samples |
| chaos mssql \| 0 c: | write 2.77 ms mean, 7.66 ms p95, 9 writes/s, 99% idle, 829 samples |
| chaos mssql \| 1 d: | write 0.22 ms mean, 0.34 ms p95, 807 writes/s, 68% idle, 829 samples |
| chaos pg \| 0 c: | write 2.26 ms mean, 7.37 ms p95, 12 writes/s, 99% idle, 538 samples |
| chaos pg \| 1 d: | write 0.05 ms mean, 0.05 ms p95, 969 writes/s, 44% idle, 538 samples |
| chaos sqlite \| 0 c: | write 2.66 ms mean, 7.55 ms p95, 18 writes/s, 99% idle, 397 samples |
| chaos sqlite \| 1 d: | write 1.89 ms mean, 7.51 ms p95, 524 writes/s, 82% idle, 397 samples |
| correctness \| 0 c: | write 2.56 ms mean, 6.22 ms p95, 13 writes/s, 99% idle, 307 samples |
| correctness \| 1 d: | write 0.40 ms mean, 2.66 ms p95, 588 writes/s, 84% idle, 307 samples |
| mssql \| 0 c: | write 2.51 ms mean, 7.34 ms p95, 15 writes/s, 98% idle, 1428 samples |
| mssql \| 1 d: | write 0.77 ms mean, 2.46 ms p95, 1907 writes/s, 52% idle, 1428 samples |
| pg \| 0 c: | write 2.85 ms mean, 7.60 ms p95, 10 writes/s, 99% idle, 1710 samples |
| pg \| 1 d: | write 0.73 ms mean, 3.45 ms p95, 827 writes/s, 66% idle, 1710 samples |
| sqlite \| 0 c: | write 2.35 ms mean, 6.72 ms p95, 13 writes/s, 99% idle, 916 samples |
| sqlite \| 1 d: | write 0.08 ms mean, 0.18 ms p95, 401 writes/s, 96% idle, 916 samples |
| after \| 0 c: | write 2.56 ms mean, 7.48 ms p95, 12 writes/s, 99% idle, 15 samples |
| after \| 1 d: | write 4.00 ms mean, 7.59 ms p95, 7 writes/s, 100% idle, 15 samples |

# Acta Verify: INCOMPLETE

The release suite on SQLite, commit `e8cc81d90`, started 2026-10-31 20:57 UTC, 53 min, on Intel(R) Core(TM) i9-14900K.

| | SQLite |
|---|---|
| Correctness | pass |
| Crash-recovery chaos | pass, 10,000 jobs, 33 busy kills reclaimed |
| Ledger checks | pass, 113 trials checked |
| Performance floors | held |
| Drain | 8,418 jobs/s |
| Throughput | 200 jobs/s |
| Throughput, one worker | not written |

> **200 fully executed, durably completed jobs per second** on SQLite: 1 worker, one job per enqueue call, held 3 × 30 s and 120 s at 200/s offered, p99 1.0 s, Intel(R) Core(TM) i9-14900K.

## Performance

Each scenario's first written rate is its floor, which this suite requires to hold.

### Drain

How fast do eight workers over two namespaces run and durably complete a backlog enqueued before they start?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 8; jobs per call: 1,000.

| Database | Pass | Jobs | Steady rate, database clock | Whole cohort | Result |
|---|---:|---:|---:|---:|---|
| SQLite | 1 | 100,000 | 8,418 jobs/s | 12 s | held |
| SQLite | 2 | 100,000 | 8,430 jobs/s | 12.2 s | held |
| SQLite | 3 | 100,000 | 8,325 jobs/s | 12.1 s | held |

### Sustained throughput

How many jobs per second, one per enqueue call, does Acta enqueue, run and durably complete with a flat backlog and p99 under 2 s?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 8; jobs per call: 1; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Result |
|---|---:|---:|---:|---:|---|
| SQLite | 25 | 1 | 25.2 | 1.1 s | held |
| SQLite | 25 | 2 | 25.7 | 1.1 s | held |
| SQLite | 25 | 3 | 25.4 | 1.1 s | held |
| SQLite | 50 | 1 | 50.3 | 1.1 s | held |
| SQLite | 50 | 2 | 50.1 | 1.1 s | held |
| SQLite | 50 | 3 | 50 | 1.1 s | held |
| SQLite | 100 | 1 | 102 | 1.1 s | held |
| SQLite | 100 | 2 | 102 | 1.1 s | held |
| SQLite | 100 | 3 | 103 | 1.1 s | held |
| SQLite | 200 | 1 | 202 | 1.0 s | held |
| SQLite | 200 | 2 | 205 | 1.0 s | held |
| SQLite | 200 | 3 | 202 | 1.0 s | held |
| SQLite | 400 | 1 | 409 | 1.1 s | held |
| SQLite | 400 | 2 | 408 | 1.1 s | held |
| SQLite | 400 | 3 | 397 | 1.1 s | held |
| SQLite | 400 | confirm, 120 s | 401 | 2.1 s | missed: p99 2.1 s against a 2.0 s target |
| SQLite | 200 | confirm, 120 s | 200 | 1.0 s | held |

Highest rate held in every pass: SQLite 400/s, every written rate, so the ceiling is above the ladder.

### Sustained throughput, one worker

How many jobs per second does one worker process of default executors run and durably complete, one job per enqueue call, with a flat backlog and p99 under 2 s?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 8; jobs per call: 1; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Result |
|---|---:|---:|---:|---:|---|
| SQLite | | | | | The scenario writes no rate for sqlite. |

Highest rate held in every pass: .

### Enqueue, one job per call

How fast are single-job enqueue calls acknowledged, each in its own transaction, before any worker runs?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 8; jobs per call: 1; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Result |
|---|---:|---:|---:|---:|---|
| SQLite | 200 | 1 | 0 | 23 ms | held |

Highest rate held in every pass: SQLite 200/s, every written rate, so the ceiling is above the ladder.

### Enqueue in batches of 100

How fast are batches of 100 jobs acknowledged, before any worker runs?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 8; jobs per call: 100; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Result |
|---|---:|---:|---:|---:|---|
| SQLite | 5,000 | 1 | 0 | 30 ms | held |

Highest rate held in every pass: SQLite 5,000/s, every written rate, so the ceiling is above the ladder.

### Enqueue in batches of 1,000

How fast are batches of 1,000 jobs acknowledged, before any worker runs?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 8; jobs per call: 1,000; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Result |
|---|---:|---:|---:|---:|---|
| SQLite | 10,000 | 1 | 0 | 77 ms | held |

Highest rate held in every pass: SQLite 10,000/s, every written rate, so the ceiling is above the ladder.

### Drain, one worker

How fast does one worker run and durably complete a backlog enqueued before it starts?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 8; jobs per call: 1,000.

| Database | Pass | Jobs | Steady rate, database clock | Whole cohort | Result |
|---|---:|---:|---:|---:|---|
| SQLite | 1 | 50,000 | 8,056 jobs/s | 6.29 s | held |

### Drain, four workers

How fast do four workers over two namespaces drain a backlog enqueued before they start?

Setup: workers: 4; namespaces: 2; executors per worker: the product default; enqueue calls in flight: 8; jobs per call: 1,000.

| Database | Pass | Jobs | Steady rate, database clock | Whole cohort | Result |
|---|---:|---:|---:|---:|---|
| SQLite | | | | | The scenario writes no cohort for sqlite. |

### Drain, sixteen workers

How fast do sixteen workers over two namespaces drain a backlog enqueued before they start?

Setup: workers: 16; namespaces: 2; executors per worker: the product default; enqueue calls in flight: 8; jobs per call: 1,000.

| Database | Pass | Jobs | Steady rate, database clock | Whole cohort | Result |
|---|---:|---:|---:|---:|---|
| SQLite | | | | | The scenario writes no cohort for sqlite. |

### Throughput, sixteen workers

Do sixteen worker processes over two namespaces hold a high offered rate with p99 under 2 s?

Setup: workers: 16; namespaces: 2; executors per worker: the product default; enqueue calls in flight: 8; jobs per call: 1; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Result |
|---|---:|---:|---:|---:|---|
| SQLite | | | | | The scenario writes no rate for sqlite. |

Highest rate held in every pass: .

### Throughput, sixteen workers on one namespace

Do sixteen worker processes sharing one namespace hold a high offered rate with p99 under 2 s?

Setup: workers: 16; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 8; jobs per call: 1; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Result |
|---|---:|---:|---:|---:|---|
| SQLite | | | | | The scenario writes no rate for sqlite. |

Highest rate held in every pass: .

### Throughput, 32 executors

Does one worker with 32 executors hold the stream its default configuration holds?

Setup: workers: 1; namespaces: 1; executors per worker: 32; enqueue calls in flight: 8; jobs per call: 1; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Result |
|---|---:|---:|---:|---:|---|
| SQLite | 40 | 1 | 40.8 | 1.1 s | held |

Highest rate held in every pass: SQLite 40/s, every written rate, so the ceiling is above the ladder.

### Three persisted steps

Do jobs that commit three checkpoints each hold their offered rate with p99 under 5 s?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 8; jobs per call: 1; p99 target: 5.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Result |
|---|---:|---:|---:|---:|---|
| SQLite | 15 | 1 | 15.3 | 1.1 s | held |

Highest rate held in every pass: SQLite 15/s, every written rate, so the ceiling is above the ladder.

### Transient failure and retry

With one job in four failing twice before it succeeds, does the stream hold its rate with p99 under 10 s?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 8; jobs per call: 1; p99 target: 10.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Result |
|---|---:|---:|---:|---:|---|
| SQLite | 40 | 1 | 40.3 | 2.7 s | held |

Highest rate held in every pass: SQLite 40/s, every written rate, so the ceiling is above the ladder.

### Fixed CPU work

Do jobs that each hash for a fixed number of rounds hold their offered rate with p99 under 2 s?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 8; jobs per call: 1; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Result |
|---|---:|---:|---:|---:|---|
| SQLite | 40 | 1 | 40.8 | 1.1 s | held |

Highest rate held in every pass: SQLite 40/s, every written rate, so the ceiling is above the ladder.

### Payloads of 256 B

Do jobs carrying a 256-byte payload hold their offered rate with p99 under 2 s?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 8; jobs per call: 1; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Result |
|---|---:|---:|---:|---:|---|
| SQLite | 30 | 1 | 30.2 | 1.1 s | held |

Highest rate held in every pass: SQLite 30/s, every written rate, so the ceiling is above the ladder.

### Payloads of 4 KB

Do jobs carrying a 4 KB payload hold their offered rate with p99 under 2 s?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 8; jobs per call: 1; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Result |
|---|---:|---:|---:|---:|---|
| SQLite | 30 | 1 | 30.4 | 1.1 s | held |

Highest rate held in every pass: SQLite 30/s, every written rate, so the ceiling is above the ladder.

### Payloads of 256 B, 4 KB and 64 KB mixed

Do jobs carrying payloads of three sizes in turn hold their offered rate with p99 under 2 s?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 8; jobs per call: 1; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Result |
|---|---:|---:|---:|---:|---|
| SQLite | 40 | 1 | 40.6 | 1.1 s | held |

Highest rate held in every pass: SQLite 40/s, every written rate, so the ceiling is above the ladder.

### Payloads of 64 KB

Do jobs carrying a 64 KB payload hold their offered rate with p99 under 2 s?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 8; jobs per call: 1; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Result |
|---|---:|---:|---:|---:|---|
| SQLite | 10 | 1 | 10.3 | 1.1 s | held |

Highest rate held in every pass: SQLite 10/s, every written rate, so the ceiling is above the ladder.

### A thousand lanes

Spread over a thousand ordered lanes, does the stream hold its rate with p99 under 2 s?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 8; jobs per call: 1; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Result |
|---|---:|---:|---:|---:|---|
| SQLite | 30 | 1 | 30.4 | 1.1 s | held |

Highest rate held in every pass: SQLite 30/s, every written rate, so the ceiling is above the ladder.

### Operator queries during processing

While the stream runs, do the operator list queries answer quickly, and does the stream keep its rate?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 8; jobs per call: 1; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Result |
|---|---:|---:|---:|---:|---|
| SQLite | 30 | 1 | 30.7 | 1.1 s | held |

Highest rate held in every pass: SQLite 30/s, every written rate, so the ceiling is above the ladder.

### Parked work alongside

With a large population of jobs parked on signals, does the stream hold its rate with p99 under 2 s?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 8; jobs per call: 1; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Result |
|---|---:|---:|---:|---:|---|
| SQLite | 40 | 1 | 40.6 | 1.1 s | held |

Highest rate held in every pass: SQLite 40/s, every written rate, so the ceiling is above the ladder.

### Future high-priority work alongside

With a large population of high-priority jobs due tomorrow, does the stream hold its rate with p99 under 2 s?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 8; jobs per call: 1; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Result |
|---|---:|---:|---:|---:|---|
| SQLite | 40 | 1 | 40.6 | 1.1 s | held |

Highest rate held in every pass: SQLite 40/s, every written rate, so the ceiling is above the ladder.

### Ten children and a join

Do parents that fan out ten children and join them hold their offered rate with p99 under 10 s?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 8; jobs per call: 1; p99 target: 10.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Result |
|---|---:|---:|---:|---:|---|
| SQLite | 2 | 1 | 2.03 | 1.2 s | held |

Highest rate held in every pass: SQLite 2/s, every written rate, so the ceiling is above the ladder.

### Controlled I/O through an idempotent sink

Do jobs that each call an external idempotent sink for 5 ms hold their offered rate with p99 under 5 s?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 8; jobs per call: 1; p99 target: 5.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Result |
|---|---:|---:|---:|---:|---|
| SQLite | 20 | 1 | 20.5 | 1.1 s | held |

Highest rate held in every pass: SQLite 20/s, every written rate, so the ceiling is above the ladder.

### Short and long work mixed

Do no-op, CPU, sink, timer and fan-out jobs mixed in turn hold their offered rate with p99 under 5 s?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 8; jobs per call: 1; p99 target: 5.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Result |
|---|---:|---:|---:|---:|---|
| SQLite | 20 | 1 | 20.5 | 1.8 s | held |

Highest rate held in every pass: SQLite 20/s, every written rate, so the ceiling is above the ladder.

### Throughput, one executor

Does a worker with a single executor keep up with a modest stream, so the result shows its serial cost?

Setup: workers: 1; namespaces: 1; executors per worker: 1; enqueue calls in flight: 8; jobs per call: 1; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Result |
|---|---:|---:|---:|---:|---|
| SQLite | 25 | 1 | 25.5 | 1.1 s | held |

Highest rate held in every pass: SQLite 25/s, every written rate, so the ceiling is above the ladder.

### One hot lane

How fast does one ordered lane hand its jobs over, each waiting for the previous completion to commit?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 8; jobs per call: 1; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Result |
|---|---:|---:|---:|---:|---|
| SQLite | 20 | 1 | 20.3 | 1.0 s | held |

Highest rate held in every pass: SQLite 20/s, every written rate, so the ceiling is above the ladder.

### One hot concurrency key

With every job sharing one concurrency key of limit one, does a stream of one a second keep a flat backlog with p99 under 30 s?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 8; jobs per call: 1; p99 target: 30.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Result |
|---|---:|---:|---:|---:|---|
| SQLite | 1 | 1 | 0.967 | 4.5 s | held |

Highest rate held in every pass: SQLite 1/s, every written rate, so the ceiling is above the ladder.

### Five-minute mixed soak

Does the mixed workload hold a steady rate for five minutes with p99 under 5 s and a flat backlog?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 8; jobs per call: 1; p99 target: 5.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Result |
|---|---:|---:|---:|---:|---|
| SQLite | 14 | 1 | 14 | 2.1 s | held |

Highest rate held in every pass: SQLite 14/s, every written rate, so the ceiling is above the ladder.

### Overload, then recovery

Offered more than it can run, does the backlog grow, and does it fall back once the offered rate drops?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 8; jobs per call: 1; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Result |
|---|---:|---:|---:|---:|---|
| SQLite | 1,000, then 50 | 1 | 526 | 1.6 s | incomplete: The offered overload of 1,000 jobs/s did not grow the backlog during the overload half. |

## Correctness

| Scenario | SQLite |
|---|---|
| Checkpoint depths of 1, 10 and 100 steps | pass |
| Wide fan-out of 1000 children | pass |
| Nested tree five levels deep | pass |
| One failing child among ten | pass |
| Children in another namespace | pass |
| Signal sent before the wait | pass |
| Signal sent after the wait | pass |
| Same signal sent twice | pass |
| Poison jobs beside healthy ones | pass |
| Order across 1000 lanes | pass |
| Order in one hot lane | pass |
| One-slot concurrency key | pass |
| Priority across tenants | pass |
| Rate limit shared by two workers | pass |
| New work beside retained history | pass |
| Retention purges during traffic | pass |
| Retention of a waiting parent's children | pass |
| Enqueue in a committed transaction | pass |
| Enqueue in a rolled-back transaction | pass |
| Compatible step result change | pass |
| Incompatible input type change | pass |
| Schema upgrade from a supported release | skipped |

## Faults

| Scenario | SQLite |
|---|---|
| Crash after a checkpoint commits | pass |
| Crash after an effect, before its checkpoint | pass |
| Crash inside an at-most-once step | pass |
| Parent dies during fan-out | pass |
| Last child against the join | pass |
| Cancelling a parent during fan-out | pass |
| Durable timer across a worker restart | pass |
| Signal sent during wait registration | pass |
| Signal against a wait timeout | pass |
| Timed-out attempt that keeps running | pass |
| Kill a busy worker | pass |
| Kill every worker | pass |
| Worker paused past its lease | pass |
| Database connections cut | skipped |
| Database process crash | skipped |
| Lost reply to a completion | pass |
| Lost reply to an enqueue | pass |
| Wake signals dropped | pass |
| Outbox relay crash | pass |
| Non-idempotent effect, crash before completion | pass |
| Idempotent effect, crash before its checkpoint | pass |
| Rolling restart | pass |

## Chaos

| Scenario | SQLite |
|---|---|
| Worker kills under mixed load | pass |

## What did not pass

- **Overload, then recovery** on SQLite, incomplete: The offered overload of 1,000 jobs/s did not grow the backlog during the overload half. (`histories/sqlite-overload-then-recover-measure-1-50-checks.json`)

## How this run was made

- Command: `acta-verify release --db sqlite`.
- Commit `e8cc81d90da60a8fdc293b7d499f3d903175c15f`, measurement version 2, written standard `6eb449008971`.
- Machine: Intel(R) Core(TM) i9-14900K, 24 cores, 32 logical processors, Microsoft Windows 10.0.26300, .NET 10.0.12.
- SQLite: peak 0 database connections; disk before measuring: yes, after 6 s.
- Wall time: performance on SQLite 34 min, correctness on SQLite 5 min, chaos on SQLite 12 min; correctness runs on every database at once, chaos one database at a time.
- Evidence: 5.5 MB of a 50 MB budget.
- A rate holds when every scheduled arrival reached an outcome and none was refused, the backlog trend stayed within 1% of the rate, p99 stayed within the target, at most 0.1% of arrivals were picked up late by the harness, and every job reconciled. These are the rc.6 criteria.

| Disk, by phase and drive | Windows counters |
|---|---|
| before \| 0 c: | write 2.71 ms mean, 8.07 ms p95, 8 writes/s, 99% idle, 14 samples |
| before \| 1 d: | write 2.10 ms mean, 7.28 ms p95, 182 writes/s, 99% idle, 14 samples |
| chaos sqlite \| 0 c: | write 1.88 ms mean, 4.77 ms p95, 20 writes/s, 99% idle, 372 samples |
| chaos sqlite \| 1 d: | write 1.96 ms mean, 7.22 ms p95, 498 writes/s, 78% idle, 372 samples |
| correctness \| 0 c: | write 1.42 ms mean, 4.25 ms p95, 30 writes/s, 99% idle, 158 samples |
| correctness \| 1 d: | write 0.85 ms mean, 2.76 ms p95, 221 writes/s, 90% idle, 158 samples |
| sqlite \| 0 c: | write 2.15 ms mean, 6.68 ms p95, 16 writes/s, 99% idle, 1033 samples |
| sqlite \| 1 d: | write 0.08 ms mean, 0.14 ms p95, 448 writes/s, 93% idle, 1033 samples |
| after \| 0 c: | write 2.00 ms mean, 4.42 ms p95, 26 writes/s, 99% idle, 15 samples |
| after \| 1 d: | write 4.26 ms mean, 6.78 ms p95, 8 writes/s, 100% idle, 15 samples |
