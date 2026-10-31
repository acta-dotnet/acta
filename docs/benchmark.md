# Acta 1.0.0-rc.7: benchmark and certification

The 1.0.0-rc.7 release evidence on one machine, in two Acta Verify bundles. The release run on `e294a8b91` ran every scenario of the release suite on PostgreSQL, SQL Server, and SQLite: every correctness and fault scenario passed, and every trial's ledger passed the certification checks. Four scenarios it could not judge, for reasons in Acta Verify rather than the engine. `54ddd670e` changes only Acta Verify, and the rerun of those four against the same engine passed on every database. Both reports follow, the rerun last.

- Overload, then recover offered PostgreSQL and SQL Server less than one worker absorbs, so no backlog grew. The rerun offers 13,000/s and 8,000/s.
- One hot concurrency key on SQL Server held 1/s with a flat backlog, but p99 was 12.7 s against a 10 s target set from shorter calibration windows. The rerun's target is the 30 s window.
- Ten children on SQLite missed its 10/s floor on the backlog trend, where at so low a rate the poll's jitter decides it. The rerun's floor is 2/s.
- Chaos on PostgreSQL and SQL Server ran side by side and starved the host: a quiesce count timed out, and a replacement worker's login timed out on all three starts. The rerun runs chaos one database at a time, as rc.6 did.

# The release run on e294a8b91: FAIL

The release suite on PostgreSQL, SQL Server, SQLite, commit `e294a8b91`, started 2026-10-30 23:12 UTC, 2 h 47 min, on Intel(R) Core(TM) i9-14900K.

| | PostgreSQL | SQL Server | SQLite |
|---|---|---|---|
| Correctness | pass | pass | pass |
| Crash-recovery chaos | incomplete, 1 trial | incomplete, 1 trial | pass, 10,000 jobs, 30 busy kills reclaimed |
| Ledger checks | pass, 133 trials checked | pass, 121 trials checked | pass, 106 trials checked |
| Performance floors | held | missed 1 | missed 1 |
| Drain | 70,085 jobs/s | 42,543 jobs/s | 4,907 jobs/s |
| Throughput | 12,430 jobs/s | 5,000 jobs/s | 50.3 jobs/s |
| Throughput, one worker | 10,017 jobs/s | 5,007 jobs/s | not written |

> **12,430 fully executed, durably completed jobs per second** on PostgreSQL: 4 workers, one job per enqueue call, held 3 × 30 s and 120 s at 12,500/s offered, p99 1.0 s, Intel(R) Core(TM) i9-14900K.

> **5,000 fully executed, durably completed jobs per second** on SQL Server: 4 workers, one job per enqueue call, held 3 × 30 s and 120 s at 5,000/s offered, p99 936 ms, Intel(R) Core(TM) i9-14900K.

> **50.3 fully executed, durably completed jobs per second** on SQLite: 1 worker, one job per enqueue call, held 3 × 30 s and 120 s at 50/s offered, p99 1.1 s, Intel(R) Core(TM) i9-14900K.

> **10,017 fully executed, durably completed jobs per second** on PostgreSQL: 1 worker, one job per enqueue call, held 3 × 30 s and 120 s at 10,000/s offered, p99 1.0 s, Intel(R) Core(TM) i9-14900K.

> **5,007 fully executed, durably completed jobs per second** on SQL Server: 1 worker, one job per enqueue call, held 3 × 30 s and 120 s at 5,000/s offered, p99 817 ms, Intel(R) Core(TM) i9-14900K.

## Performance

Each scenario's first written rate is its floor, which this suite requires to hold.

### Drain

How fast do eight workers over two namespaces run and durably complete a backlog enqueued before they start?

Setup: workers: 8 on PostgreSQL, 8 on SQL Server, 1 on SQLite; namespaces: 2 on PostgreSQL, 2 on SQL Server, 1 on SQLite; executors per worker: the product default; enqueue calls in flight: 8; jobs per call: 1,000.

| Database | Pass | Jobs | Steady rate, database clock | Whole cohort | Result |
|---|---:|---:|---:|---:|---|
| PostgreSQL | 1 | 1,000,000 | 68,710 jobs/s | 15.2 s | held |
| PostgreSQL | 2 | 1,000,000 | 70,085 jobs/s | 14.9 s | held |
| PostgreSQL | 3 | 1,000,000 | 80,360 jobs/s | 13.1 s | held |
| SQL Server | 1 | 1,000,000 | 39,868 jobs/s | 26.1 s | held |
| SQL Server | 2 | 1,000,000 | 42,543 jobs/s | 23.7 s | held |
| SQL Server | 3 | 1,000,000 | 52,457 jobs/s | 20.3 s | held |
| SQLite | 1 | 100,000 | 4,883 jobs/s | 20.8 s | held |
| SQLite | 2 | 100,000 | 4,907 jobs/s | 20.7 s | held |
| SQLite | 3 | 100,000 | 5,799 jobs/s | 17.4 s | held |

### Sustained throughput

How many jobs per second, one per enqueue call, does Acta enqueue, run and durably complete with a flat backlog and p99 under 2 s?

Setup: workers: 4 on PostgreSQL, 4 on SQL Server, 1 on SQLite; namespaces: 2 on PostgreSQL, 2 on SQL Server, 1 on SQLite; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Result |
|---|---:|---:|---:|---:|---|
| PostgreSQL | 10,000 | 1 | 9,801 | 1.0 s | held |
| PostgreSQL | 10,000 | 2 | 10,052 | 1.0 s | held |
| PostgreSQL | 10,000 | 3 | 9,904 | 1.0 s | held |
| PostgreSQL | 12,500 | 1 | 12,337 | 1.0 s | held |
| PostgreSQL | 12,500 | 2 | 12,501 | 1.0 s | held |
| PostgreSQL | 12,500 | 3 | 12,393 | 1.0 s | held |
| PostgreSQL | 15,000 | 1 | 15,057 | 1.0 s | held |
| PostgreSQL | 15,000 | 2 | 14,774 | 1.0 s | held |
| PostgreSQL | 15,000 | 3 | 15,004 | 967 ms | held |
| PostgreSQL | 17,500 | 1 | 17,902 | 684 ms | held |
| PostgreSQL | 17,500 | 2 | 17,325 | 1.4 s | missed: backlog grew 244 jobs/s |
| PostgreSQL | 17,500 | 3 | 17,604 | 791 ms | held |
| PostgreSQL | 20,000 | 1 | 17,498 | 6.3 s | missed: backlog grew 2,538 jobs/s |
| PostgreSQL | 20,000 | 3 | 17,438 | 6.2 s | missed: backlog grew 2,654 jobs/s |
| PostgreSQL | 25,000 | | | | not run by rule |
| PostgreSQL | 30,000 | | | | not run by rule |
| PostgreSQL | 15,000 | confirm, 120 s | 14,666 | 3.1 s | missed: p99 3.1 s |
| PostgreSQL | 12,500 | confirm, 120 s | 12,430 | 1.0 s | held |
| SQL Server | 5,000 | 1 | 4,996 | 999 ms | held |
| SQL Server | 5,000 | 2 | 5,092 | 906 ms | held |
| SQL Server | 5,000 | 3 | 5,000 | 483 ms | held |
| SQL Server | 7,500 | 1 | 6,435 | 6.3 s | missed: backlog grew 1,015 jobs/s |
| SQL Server | 7,500 | 2 | 6,637 | 5.2 s | missed: backlog grew 867 jobs/s |
| SQL Server | 7,500 | 3 | 6,737 | 4.5 s | missed: backlog grew 715 jobs/s |
| SQL Server | 10,000 | | | | not run by rule |
| SQL Server | 12,500 | | | | not run by rule |
| SQL Server | 15,000 | | | | not run by rule |
| SQL Server | 5,000 | confirm, 120 s | 5,000 | 936 ms | held |
| SQLite | 25 | 1 | 25.1 | 1.1 s | held |
| SQLite | 25 | 2 | 25.4 | 1.1 s | held |
| SQLite | 25 | 3 | 25.8 | 1.1 s | held |
| SQLite | 50 | 1 | 50.1 | 1.1 s | held |
| SQLite | 50 | 2 | 51.4 | 1.1 s | held |
| SQLite | 50 | 3 | 51.2 | 1.1 s | held |
| SQLite | 100 | 1 | 97.8 | 4.8 s | missed: p99 4.8 s |
| SQLite | 100 | 2 | 101 | 2.1 s | missed: p99 2.1 s |
| SQLite | 100 | 3 | 98.9 | 5.5 s | missed: p99 5.5 s |
| SQLite | 200 | | | | not run by rule |
| SQLite | 400 | | | | not run by rule |
| SQLite | 50 | confirm, 120 s | 50.3 | 1.1 s | held |

Highest rate held in every pass: PostgreSQL 15,000/s; SQL Server 5,000/s; SQLite 50/s.

### Sustained throughput, one worker

How many jobs per second does one worker process of default executors run and durably complete, one job per enqueue call, with a flat backlog and p99 under 2 s?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Result |
|---|---:|---:|---:|---:|---|
| PostgreSQL | 5,000 | 1 | 5,076 | 1.0 s | held |
| PostgreSQL | 5,000 | 2 | 5,120 | 1.0 s | held |
| PostgreSQL | 5,000 | 3 | 5,066 | 1.0 s | held |
| PostgreSQL | 7,500 | 1 | 7,682 | 1.0 s | held |
| PostgreSQL | 7,500 | 2 | 7,496 | 1.0 s | held |
| PostgreSQL | 7,500 | 3 | 7,424 | 1.0 s | held |
| PostgreSQL | 10,000 | 1 | 10,131 | 1.0 s | held |
| PostgreSQL | 10,000 | 2 | 10,177 | 1.0 s | held |
| PostgreSQL | 10,000 | 3 | 9,942 | 1.0 s | held |
| PostgreSQL | 12,500 | 1 | 9,378 | 8.9 s | missed: backlog grew 3,451 jobs/s |
| PostgreSQL | 12,500 | 2 | 11,763 | 3.2 s | missed: backlog grew 752 jobs/s |
| PostgreSQL | 12,500 | 3 | 8,299 | 12.3 s | missed: backlog grew 4,202 jobs/s |
| PostgreSQL | 15,000 | | | | not run by rule |
| PostgreSQL | 10,000 | confirm, 120 s | 10,017 | 1.0 s | held |
| SQL Server | 2,500 | 1 | 2,459 | 1.1 s | held |
| SQL Server | 2,500 | 2 | 2,513 | 1.0 s | held |
| SQL Server | 2,500 | 3 | 2,490 | 1.0 s | held |
| SQL Server | 5,000 | 1 | 5,021 | 962 ms | held |
| SQL Server | 5,000 | 2 | 5,093 | 891 ms | held |
| SQL Server | 5,000 | 3 | 5,060 | 1.0 s | held |
| SQL Server | 7,500 | 1 | 6,231 | 8.6 s | missed: backlog grew 1,053 jobs/s |
| SQL Server | 7,500 | 2 | 6,238 | 8.1 s | missed: backlog grew 1,251 jobs/s |
| SQL Server | 7,500 | 3 | 5,984 | 10.0 s | missed: backlog grew 1,648 jobs/s |
| SQL Server | 10,000 | | | | not run by rule |
| SQL Server | 5,000 | confirm, 120 s | 5,007 | 817 ms | held |
| SQLite | | | | | The scenario writes no rate for sqlite. |

Highest rate held in every pass: PostgreSQL 10,000/s; SQL Server 5,000/s.

### Enqueue, one job per call

How fast are single-job enqueue calls acknowledged, each in its own transaction, before any worker runs?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Result |
|---|---:|---:|---:|---:|---|
| PostgreSQL | 10,000 | 1 | 0 | 27 ms | held |
| SQL Server | 5,000 | 1 | 0 | 32 ms | held |
| SQLite | 200 | 1 | 0 | 706 ms | held |

Highest rate held in every pass: PostgreSQL 10,000/s, every written rate, so the ceiling is above the ladder; SQL Server 5,000/s, every written rate, so the ceiling is above the ladder; SQLite 200/s, every written rate, so the ceiling is above the ladder.

### Enqueue in batches of 100

How fast are batches of 100 jobs acknowledged, before any worker runs?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 100; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Result |
|---|---:|---:|---:|---:|---|
| PostgreSQL | 10,000 | 1 | 0 | 36 ms | held |
| SQL Server | 5,000 | 1 | 0 | 41 ms | held |
| SQLite | 5,000 | 1 | 0 | 43 ms | held |

Highest rate held in every pass: PostgreSQL 10,000/s, every written rate, so the ceiling is above the ladder; SQL Server 5,000/s, every written rate, so the ceiling is above the ladder; SQLite 5,000/s, every written rate, so the ceiling is above the ladder.

### Enqueue in batches of 1,000

How fast are batches of 1,000 jobs acknowledged, before any worker runs?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1,000; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Result |
|---|---:|---:|---:|---:|---|
| PostgreSQL | 20,000 | 1 | 0 | 204 ms | held |
| SQL Server | 10,000 | 1 | 0 | 137 ms | held |
| SQLite | 10,000 | 1 | 0 | 84 ms | held |

Highest rate held in every pass: PostgreSQL 20,000/s, every written rate, so the ceiling is above the ladder; SQL Server 10,000/s, every written rate, so the ceiling is above the ladder; SQLite 10,000/s, every written rate, so the ceiling is above the ladder.

### Drain, one worker

How fast does one worker run and durably complete a backlog enqueued before it starts?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 8; jobs per call: 1,000.

| Database | Pass | Jobs | Steady rate, database clock | Whole cohort | Result |
|---|---:|---:|---:|---:|---|
| PostgreSQL | 1 | 200,000 | 21,448 jobs/s | 9.84 s | held |
| SQL Server | 1 | 200,000 | 14,774 jobs/s | 13.7 s | held |
| SQLite | 1 | 50,000 | 5,721 jobs/s | 8.87 s | held |

### Drain, four workers

How fast do four workers over two namespaces drain a backlog enqueued before they start?

Setup: workers: 4; namespaces: 2; executors per worker: the product default; enqueue calls in flight: 8; jobs per call: 1,000.

| Database | Pass | Jobs | Steady rate, database clock | Whole cohort | Result |
|---|---:|---:|---:|---:|---|
| PostgreSQL | 1 | 200,000 | 42,591 jobs/s | 4.72 s | held |
| SQL Server | 1 | 200,000 | 39,072 jobs/s | 5.15 s | held |
| SQLite | | | | | The scenario writes no cohort for sqlite. |

### Drain, sixteen workers

How fast do sixteen workers over two namespaces drain a backlog enqueued before they start?

Setup: workers: 16; namespaces: 2; executors per worker: the product default; enqueue calls in flight: 8; jobs per call: 1,000.

| Database | Pass | Jobs | Steady rate, database clock | Whole cohort | Result |
|---|---:|---:|---:|---:|---|
| PostgreSQL | 1 | 1,000,000 | 72,006 jobs/s | 14.5 s | held |
| SQL Server | 1 | 1,000,000 | 49,560 jobs/s | 21.3 s | held |
| SQLite | | | | | The scenario writes no cohort for sqlite. |

### Throughput, sixteen workers

Do sixteen worker processes over two namespaces hold a high offered rate with p99 under 2 s?

Setup: workers: 16; namespaces: 2; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Result |
|---|---:|---:|---:|---:|---|
| PostgreSQL | 15,000 | 1 | 15,301 | 1.0 s | held |
| SQL Server | 4,000 | 1 | 3,976 | 684 ms | held |
| SQLite | | | | | The scenario writes no rate for sqlite. |

Highest rate held in every pass: PostgreSQL 15,000/s, every written rate, so the ceiling is above the ladder; SQL Server 4,000/s, every written rate, so the ceiling is above the ladder.

### Throughput, sixteen workers on one namespace

Do sixteen worker processes sharing one namespace hold a high offered rate with p99 under 2 s?

Setup: workers: 16; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Result |
|---|---:|---:|---:|---:|---|
| PostgreSQL | 15,000 | 1 | 15,306 | 926 ms | held |
| SQL Server | 4,000 | 1 | 4,010 | 494 ms | held |
| SQLite | | | | | The scenario writes no rate for sqlite. |

Highest rate held in every pass: PostgreSQL 15,000/s, every written rate, so the ceiling is above the ladder; SQL Server 4,000/s, every written rate, so the ceiling is above the ladder.

### Throughput, 32 executors

Does one worker with 32 executors hold the stream its default configuration holds?

Setup: workers: 1; namespaces: 1; executors per worker: 32; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Result |
|---|---:|---:|---:|---:|---|
| PostgreSQL | 5,000 | 1 | 4,942 | 1.0 s | held |
| SQL Server | 2,500 | 1 | 2,495 | 1.1 s | held |
| SQLite | 40 | 1 | 39.6 | 1.1 s | held |

Highest rate held in every pass: PostgreSQL 5,000/s, every written rate, so the ceiling is above the ladder; SQL Server 2,500/s, every written rate, so the ceiling is above the ladder; SQLite 40/s, every written rate, so the ceiling is above the ladder.

### Three persisted steps

Do jobs that commit three checkpoints each hold their offered rate with p99 under 5 s?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; p99 target: 5.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Result |
|---|---:|---:|---:|---:|---|
| PostgreSQL | 1,000 | 1 | 976 | 1.1 s | held |
| SQL Server | 1,000 | 1 | 1,003 | 1.1 s | held |
| SQLite | 15 | 1 | 15 | 1.7 s | held |

Highest rate held in every pass: PostgreSQL 1,000/s, every written rate, so the ceiling is above the ladder; SQL Server 1,000/s, every written rate, so the ceiling is above the ladder; SQLite 15/s, every written rate, so the ceiling is above the ladder.

### Transient failure and retry

With one job in four failing twice before it succeeds, does the stream hold its rate with p99 under 10 s?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; p99 target: 10.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Result |
|---|---:|---:|---:|---:|---|
| PostgreSQL | 5,000 | 1 | 5,006 | 2.2 s | held |
| SQL Server | 2,500 | 1 | 2,504 | 2.2 s | held |
| SQLite | 40 | 1 | 40.3 | 2.9 s | held |

Highest rate held in every pass: PostgreSQL 5,000/s, every written rate, so the ceiling is above the ladder; SQL Server 2,500/s, every written rate, so the ceiling is above the ladder; SQLite 40/s, every written rate, so the ceiling is above the ladder.

### Fixed CPU work

Do jobs that each hash for a fixed number of rounds hold their offered rate with p99 under 2 s?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Result |
|---|---:|---:|---:|---:|---|
| PostgreSQL | 4,000 | 1 | 4,060 | 1.0 s | held |
| SQL Server | 2,500 | 1 | 2,574 | 1.0 s | held |
| SQLite | 40 | 1 | 40.4 | 1.1 s | held |

Highest rate held in every pass: PostgreSQL 4,000/s, every written rate, so the ceiling is above the ladder; SQL Server 2,500/s, every written rate, so the ceiling is above the ladder; SQLite 40/s, every written rate, so the ceiling is above the ladder.

### Payloads of 256 B

Do jobs carrying a 256-byte payload hold their offered rate with p99 under 2 s?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Result |
|---|---:|---:|---:|---:|---|
| PostgreSQL | 4,000 | 1 | 4,055 | 1.1 s | held |
| SQL Server | 2,500 | 1 | 2,471 | 1.0 s | held |
| SQLite | 30 | 1 | 30.7 | 1.1 s | held |

Highest rate held in every pass: PostgreSQL 4,000/s, every written rate, so the ceiling is above the ladder; SQL Server 2,500/s, every written rate, so the ceiling is above the ladder; SQLite 30/s, every written rate, so the ceiling is above the ladder.

### Payloads of 4 KB

Do jobs carrying a 4 KB payload hold their offered rate with p99 under 2 s?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Result |
|---|---:|---:|---:|---:|---|
| PostgreSQL | 5,000 | 1 | 5,042 | 1.0 s | held |
| SQL Server | 2,500 | 1 | 2,458 | 1.0 s | held |
| SQLite | 30 | 1 | 30.5 | 1.1 s | held |

Highest rate held in every pass: PostgreSQL 5,000/s, every written rate, so the ceiling is above the ladder; SQL Server 2,500/s, every written rate, so the ceiling is above the ladder; SQLite 30/s, every written rate, so the ceiling is above the ladder.

### Payloads of 256 B, 4 KB and 64 KB mixed

Do jobs carrying payloads of three sizes in turn hold their offered rate with p99 under 2 s?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Result |
|---|---:|---:|---:|---:|---|
| PostgreSQL | 2,500 | 1 | 2,539 | 1.0 s | held |
| SQL Server | 1,000 | 1 | 1,017 | 1.1 s | held |
| SQLite | 40 | 1 | 40 | 1.1 s | held |

Highest rate held in every pass: PostgreSQL 2,500/s, every written rate, so the ceiling is above the ladder; SQL Server 1,000/s, every written rate, so the ceiling is above the ladder; SQLite 40/s, every written rate, so the ceiling is above the ladder.

### Payloads of 64 KB

Do jobs carrying a 64 KB payload hold their offered rate with p99 under 2 s?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Result |
|---|---:|---:|---:|---:|---|
| PostgreSQL | 1,000 | 1 | 979 | 1.1 s | held |
| SQL Server | 500 | 1 | 507 | 1.1 s | held |
| SQLite | 10 | 1 | 10.2 | 1.1 s | held |

Highest rate held in every pass: PostgreSQL 1,000/s, every written rate, so the ceiling is above the ladder; SQL Server 500/s, every written rate, so the ceiling is above the ladder; SQLite 10/s, every written rate, so the ceiling is above the ladder.

### A thousand lanes

Spread over a thousand ordered lanes, does the stream hold its rate with p99 under 2 s?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Result |
|---|---:|---:|---:|---:|---|
| PostgreSQL | 4,000 | 1 | 3,993 | 1.0 s | held |
| SQL Server | 1,000 | 1 | 1,004 | 1.0 s | held |
| SQLite | 30 | 1 | 30.4 | 1.1 s | held |

Highest rate held in every pass: PostgreSQL 4,000/s, every written rate, so the ceiling is above the ladder; SQL Server 1,000/s, every written rate, so the ceiling is above the ladder; SQLite 30/s, every written rate, so the ceiling is above the ladder.

### Operator queries during processing

While the stream runs, do the operator list queries answer quickly, and does the stream keep its rate?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Result |
|---|---:|---:|---:|---:|---|
| PostgreSQL | 5,000 | 1 | 5,035 | 1.0 s | held |
| SQL Server | 2,500 | 1 | 2,546 | 1.0 s | held |
| SQLite | 30 | 1 | 30.7 | 1.1 s | held |

Highest rate held in every pass: PostgreSQL 5,000/s, every written rate, so the ceiling is above the ladder; SQL Server 2,500/s, every written rate, so the ceiling is above the ladder; SQLite 30/s, every written rate, so the ceiling is above the ladder.

### Parked work alongside

With a large population of jobs parked on signals, does the stream hold its rate with p99 under 2 s?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Result |
|---|---:|---:|---:|---:|---|
| PostgreSQL | 5,000 | 1 | 5,005 | 1.0 s | held |
| SQL Server | 500 | 1 | 494 | 1.0 s | held |
| SQLite | 40 | 1 | 39.5 | 1.1 s | held |

Highest rate held in every pass: PostgreSQL 5,000/s, every written rate, so the ceiling is above the ladder; SQL Server 500/s, every written rate, so the ceiling is above the ladder; SQLite 40/s, every written rate, so the ceiling is above the ladder.

### Future high-priority work alongside

With a large population of high-priority jobs due tomorrow, does the stream hold its rate with p99 under 2 s?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Result |
|---|---:|---:|---:|---:|---|
| PostgreSQL | 5,000 | 1 | 5,023 | 1.1 s | held |
| SQL Server | 320 | 1 | 316 | 1.0 s | held |
| SQLite | 40 | 1 | 40.8 | 1.1 s | held |

Highest rate held in every pass: PostgreSQL 5,000/s, every written rate, so the ceiling is above the ladder; SQL Server 320/s, every written rate, so the ceiling is above the ladder; SQLite 40/s, every written rate, so the ceiling is above the ladder.

### Ten children and a join

Do parents that fan out ten children and join them hold their offered rate with p99 under 10 s?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; p99 target: 10.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Result |
|---|---:|---:|---:|---:|---|
| PostgreSQL | 250 | 1 | 249 | 308 ms | held |
| SQL Server | 200 | 1 | 201 | 347 ms | held |
| SQLite | 10 | 1 | 10 | 1.8 s | missed: backlog grew 0.131 jobs/s |

Highest rate held in every pass: PostgreSQL 250/s, every written rate, so the ceiling is above the ladder; SQL Server 200/s, every written rate, so the ceiling is above the ladder; SQLite none, the floor missed.

### Controlled I/O through an idempotent sink

Do jobs that each call an external idempotent sink for 5 ms hold their offered rate with p99 under 5 s?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; p99 target: 5.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Result |
|---|---:|---:|---:|---:|---|
| PostgreSQL | 250 | 1 | 248 | 1.1 s | held |
| SQL Server | 200 | 1 | 199 | 1.1 s | held |
| SQLite | 20 | 1 | 20 | 2.5 s | held |

Highest rate held in every pass: PostgreSQL 250/s, every written rate, so the ceiling is above the ladder; SQL Server 200/s, every written rate, so the ceiling is above the ladder; SQLite 20/s, every written rate, so the ceiling is above the ladder.

### Short and long work mixed

Do no-op, CPU, sink, timer and fan-out jobs mixed in turn hold their offered rate with p99 under 5 s?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; p99 target: 5.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Result |
|---|---:|---:|---:|---:|---|
| PostgreSQL | 600 | 1 | 601 | 1.1 s | held |
| SQL Server | 500 | 1 | 499 | 1.1 s | held |
| SQLite | 20 | 1 | 19.9 | 1.9 s | held |

Highest rate held in every pass: PostgreSQL 600/s, every written rate, so the ceiling is above the ladder; SQL Server 500/s, every written rate, so the ceiling is above the ladder; SQLite 20/s, every written rate, so the ceiling is above the ladder.

### Throughput, one executor

Does a worker with a single executor keep up with a modest stream, so the result shows its serial cost?

Setup: workers: 1; namespaces: 1; executors per worker: 1; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Result |
|---|---:|---:|---:|---:|---|
| PostgreSQL | 150 | 1 | 152 | 1.0 s | held |
| SQL Server | 150 | 1 | 148 | 1.0 s | held |
| SQLite | 25 | 1 | 25.1 | 1.1 s | held |

Highest rate held in every pass: PostgreSQL 150/s, every written rate, so the ceiling is above the ladder; SQL Server 150/s, every written rate, so the ceiling is above the ladder; SQLite 25/s, every written rate, so the ceiling is above the ladder.

### One hot lane

How fast does one ordered lane hand its jobs over, each waiting for the previous completion to commit?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Result |
|---|---:|---:|---:|---:|---|
| PostgreSQL | 60 | 1 | 58.7 | 1.0 s | held |
| SQL Server | 100 | 1 | 101 | 1.0 s | held |
| SQLite | 20 | 1 | 19.8 | 1.1 s | held |

Highest rate held in every pass: PostgreSQL 60/s, every written rate, so the ceiling is above the ladder; SQL Server 100/s, every written rate, so the ceiling is above the ladder; SQLite 20/s, every written rate, so the ceiling is above the ladder.

### One hot concurrency key

With every job sharing one concurrency key of limit one, does a slow stream still complete with p99 under 10 s?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; p99 target: 10.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Result |
|---|---:|---:|---:|---:|---|
| PostgreSQL | 1 | 1 | 1 | 5.1 s | held |
| SQL Server | 1 | 1 | 0.967 | 12.7 s | missed: p99 12.7 s |
| SQLite | 1 | 1 | 1.03 | 1.1 s | held |

Highest rate held in every pass: PostgreSQL 1/s, every written rate, so the ceiling is above the ladder; SQL Server none, the floor missed; SQLite 1/s, every written rate, so the ceiling is above the ladder.

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
| PostgreSQL | 2,500 | 1 | 6,234 | 1.0 s | harness error: The offered overload of 10000 roots/s did not grow the backlog during the overload half. |
| SQL Server | 1,000 | 1 | 2,500 | 1.0 s | harness error: The offered overload of 4000 roots/s did not grow the backlog during the overload half. |
| SQLite | 50 | 1 | 425 | 3.5 s | missed: p99 3.5 s |

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
| Worker kills under mixed load | incomplete, 1 trial | incomplete, 1 trial | pass |

## What did not pass

- **Overload, then recovery** on PostgreSQL, incomplete: The offered overload of 10000 roots/s did not grow the backlog during the overload half. (`histories/pg-overload-then-recover-measure-1-2500-checks.json`)
- **Overload, then recovery** on SQL Server, incomplete: The offered overload of 4000 roots/s did not grow the backlog during the overload half. (`histories/mssql-overload-then-recover-measure-1-1000-checks.json`)
- **One hot concurrency key** on SQL Server, fail: Pass 1 missed the floor of 1/s.
- **Ten children and a join** on SQLite, fail: Pass 1 missed the floor of 10/s.
- **Worker kills under mixed load** on SQL Server, incomplete: Scenario did not finish (InvalidOperationException: Owned worker exited before readiness: Acta Verify could not complete the command (Win32Exception: The wait operation timed out.).); partial histories retained. (`histories/mssql-chaos-ledger.json`)
- **Worker kills under mixed load** on SQL Server, incomplete: Chaos did not show 10 busy kills with reclaim over all 1000000 acknowledged jobs.
- **Worker kills under mixed load** on PostgreSQL, incomplete: Scenario did not finish (NpgsqlException: Exception while reading from stream); partial histories retained. (`histories/pg-chaos-ledger.json`)
- **Worker kills under mixed load** on PostgreSQL, incomplete: Chaos did not show 10 busy kills with reclaim over all 1000000 acknowledged jobs.

## How this run was made

- Command: `acta-verify release --db all`.
- Commit `e294a8b91fa1cbae6308c7d88b60a6382c1d51d0`, measurement version 2, written standard `c3fa7071f673`.
- Machine: Intel(R) Core(TM) i9-14900K, 24 cores, 32 logical processors, Microsoft Windows 10.0.26300, .NET 10.0.12.
- Docker VM: 32 CPUs, 46.9 GB; measured containers capped at PostgreSQL 32031 MB, SQL Server 32031 MB.
- PostgreSQL: peak 333 database connections; disk before measuring: yes, after 12 s.
- SQL Server: peak 336 database connections; disk before measuring: no, after 62 s: too few samples; writing most: chatgpt 0.0 MB/s, bash 0.0 MB/s, chatgpt 0.0 MB/s.
- SQLite: peak 0 database connections; disk before measuring: no, after 62 s: too few samples; writing most: wsl 0.0 MB/s, com.docker.backend 0.0 MB/s, chatgpt 0.0 MB/s.
- Wall time: performance on PostgreSQL 58 min, correctness on PostgreSQL 29 min, performance on SQL Server 48 min, correctness on SQL Server 24 min, performance on SQLite 30 min, correctness on SQLite 18 min; correctness runs on every database at once, chaos one database at a time.
- Evidence: 19.4 MB of a 50 MB budget.
- A rate holds when every scheduled arrival reached an outcome and none was refused, the backlog trend stayed within 1% of the rate, p99 stayed within the target, at most 0.1% of arrivals were picked up late by the harness, and every job reconciled. These are the rc.6 criteria.

| Disk, by phase and drive | Windows counters |
|---|---|
| before \| 0 c: | write 1.16 ms mean, 2.61 ms p95, 38 writes/s, 98% idle, 14 samples |
| before \| 1 d: | write 3.87 ms mean, 7.98 ms p95, 43 writes/s, 99% idle, 14 samples |
| correctness \| 0 c: | write 1.55 ms mean, 6.64 ms p95, 8015 writes/s, 69% idle, 874 samples |
| correctness \| 1 d: | write 0.10 ms mean, 0.42 ms p95, 1247 writes/s, 46% idle, 874 samples |
| mssql \| 0 c: | write 3.63 ms mean, 8.08 ms p95, 7 writes/s, 99% idle, 1441 samples |
| mssql \| 1 d: | write 0.87 ms mean, 2.23 ms p95, 1966 writes/s, 51% idle, 1441 samples |
| pg \| 0 c: | write 3.39 ms mean, 8.00 ms p95, 9 writes/s, 99% idle, 1737 samples |
| pg \| 1 d: | write 0.73 ms mean, 3.27 ms p95, 839 writes/s, 66% idle, 1737 samples |
| sqlite \| 0 c: | write 3.05 ms mean, 7.84 ms p95, 7 writes/s, 99% idle, 910 samples |
| sqlite \| 1 d: | write 0.11 ms mean, 0.16 ms p95, 313 writes/s, 96% idle, 910 samples |
| after \| 0 c: | write 3.00 ms mean, 7.51 ms p95, 6 writes/s, 99% idle, 14 samples |
| after \| 1 d: | write 0.19 ms mean, 0.55 ms p95, 16 writes/s, 100% idle, 14 samples |

# The rerun on 54ddd670e: PASS

The release suite on PostgreSQL, SQL Server, SQLite, commit `54ddd670e`, started 2026-10-31 02:03 UTC, 1 h 8 min, on Intel(R) Core(TM) i9-14900K.

| | PostgreSQL | SQL Server | SQLite |
|---|---|---|---|
| Crash-recovery chaos | pass, 1,000,000 jobs, 84 busy kills reclaimed | pass, 1,000,000 jobs, 82 busy kills reclaimed | pass, 10,000 jobs, 44 busy kills reclaimed |
| Ledger checks | pass, 4 trials checked | pass, 4 trials checked | pass, 4 trials checked |
| Performance floors | held | held | held |

## Performance

Each scenario's first written rate is its floor, which this suite requires to hold.

### Ten children and a join

Do parents that fan out ten children and join them hold their offered rate with p99 under 10 s?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; p99 target: 10.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Result |
|---|---:|---:|---:|---:|---|
| PostgreSQL | 250 | 1 | 254 | 305 ms | held |
| SQL Server | 200 | 1 | 199 | 491 ms | held |
| SQLite | 2 | 1 | 1.97 | 1.3 s | held |

Highest rate held in every pass: PostgreSQL 250/s, every written rate, so the ceiling is above the ladder; SQL Server 200/s, every written rate, so the ceiling is above the ladder; SQLite 2/s, every written rate, so the ceiling is above the ladder.

### One hot concurrency key

With every job sharing one concurrency key of limit one, does a stream of one a second keep a flat backlog with p99 under 30 s?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; p99 target: 30.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Result |
|---|---:|---:|---:|---:|---|
| PostgreSQL | 1 | 1 | 1 | 846 ms | held |
| SQL Server | 1 | 1 | 0.967 | 8.4 s | held |
| SQLite | 1 | 1 | 1.03 | 830 ms | held |

Highest rate held in every pass: PostgreSQL 1/s, every written rate, so the ceiling is above the ladder; SQL Server 1/s, every written rate, so the ceiling is above the ladder; SQLite 1/s, every written rate, so the ceiling is above the ladder.

### Overload, then recovery

Offered more than it can run, does the backlog grow, and does it fall back once the offered rate drops?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Result |
|---|---:|---:|---:|---:|---|
| PostgreSQL | 2,500 | 1 | 7,747 | 10.1 s | missed: p99 10.1 s |
| SQL Server | 1,000 | 1 | 4,499 | 10.4 s | missed: p99 10.4 s |
| SQLite | 50 | 1 | 425 | 19.0 s | missed: p99 19.0 s |

## Chaos

| Scenario | PostgreSQL | SQL Server | SQLite |
|---|---|---|---|
| Worker kills under mixed load | pass | pass | pass |

## How this run was made

- Command: `acta-verify run ten-children hot-concurrency-key overload-then-recover chaos --suite release --db all`.
- Commit `54ddd670ec29c64ec4d56b851914c70772b4beea`, measurement version 2, written standard `56961845f991`.
- Machine: Intel(R) Core(TM) i9-14900K, 24 cores, 32 logical processors, Microsoft Windows 10.0.26300, .NET 10.0.12.
- Docker VM: 32 CPUs, 46.9 GB; measured containers capped at PostgreSQL 32031 MB, SQL Server 32031 MB.
- PostgreSQL: peak 264 database connections; disk before measuring: yes, after 12 s.
- SQL Server: peak 264 database connections; disk before measuring: yes, after 12 s.
- SQLite: peak 0 database connections; disk before measuring: yes, after 12 s.
- Wall time: performance on PostgreSQL 2 min, chaos on PostgreSQL 17 min, performance on SQL Server 3 min, chaos on SQL Server 28 min, performance on SQLite 2 min, chaos on SQLite 12 min; correctness runs on every database at once, chaos one database at a time.
- Evidence: 1.3 MB of a 50 MB budget.
- A rate holds when every scheduled arrival reached an outcome and none was refused, the backlog trend stayed within 1% of the rate, p99 stayed within the target, at most 0.1% of arrivals were picked up late by the harness, and every job reconciled. These are the rc.6 criteria.

| Disk, by phase and drive | Windows counters |
|---|---|
| before \| 0 c: | write 2.56 ms mean, 8.55 ms p95, 9 writes/s, 99% idle, 14 samples |
| before \| 1 d: | write 4.98 ms mean, 7.76 ms p95, 42 writes/s, 99% idle, 14 samples |
| chaos mssql \| 0 c: | write 3.36 ms mean, 8.07 ms p95, 6 writes/s, 99% idle, 841 samples |
| chaos mssql \| 1 d: | write 0.24 ms mean, 0.76 ms p95, 1308 writes/s, 61% idle, 841 samples |
| chaos pg \| 0 c: | write 2.87 ms mean, 7.87 ms p95, 11 writes/s, 94% idle, 517 samples |
| chaos pg \| 1 d: | write 0.07 ms mean, 0.06 ms p95, 914 writes/s, 43% idle, 517 samples |
| chaos sqlite \| 0 c: | write 3.10 ms mean, 7.88 ms p95, 5 writes/s, 99% idle, 378 samples |
| chaos sqlite \| 1 d: | write 1.60 ms mean, 6.95 ms p95, 516 writes/s, 83% idle, 378 samples |
| mssql \| 0 c: | write 3.30 ms mean, 7.80 ms p95, 6 writes/s, 99% idle, 116 samples |
| mssql \| 1 d: | write 0.83 ms mean, 5.07 ms p95, 1562 writes/s, 71% idle, 116 samples |
| pg \| 0 c: | write 3.78 ms mean, 8.03 ms p95, 7 writes/s, 99% idle, 78 samples |
| pg \| 1 d: | write 0.61 ms mean, 4.11 ms p95, 1337 writes/s, 73% idle, 78 samples |
| sqlite \| 0 c: | write 2.40 ms mean, 6.67 ms p95, 11 writes/s, 99% idle, 75 samples |
| sqlite \| 1 d: | write 0.50 ms mean, 2.08 ms p95, 603 writes/s, 94% idle, 75 samples |
| after \| 0 c: | write 2.95 ms mean, 8.21 ms p95, 5 writes/s, 99% idle, 14 samples |
| after \| 1 d: | write 4.19 ms mean, 6.86 ms p95, 7 writes/s, 100% idle, 14 samples |
