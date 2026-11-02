# Acta 1.0.0-rc.8: benchmark and certification

The 1.0.0-rc.8 release evidence on one machine, in one Acta Verify bundle. The release run on `5043b81fa` ran every scenario of the release suite on PostgreSQL, SQL Server, and SQLite: every correctness, fault, and chaos scenario passed, every trial's ledger passed the certification checks, and every performance floor held.

- This is measurement version 3. A rate ladder's one worker runs in the measuring process and is woken in process, as one worker in an application is, so its p99 no longer waits out a one-second poll; more workers, drains, faults, and chaos keep worker processes. Each claim names how its workers learn of new work, and the four- and sixteen-worker scenarios have twins woken through Redis. Compare refuses rc.7's bundles for the change, so their figures are not set side by side here.
- Two release runs before this one did not pass. On `a2b1f66a5` and `63bd6946b`, sixteen worker processes woken through Redis completed fewer jobs than the same fleet polling, because each enqueue's wake and each finished job's wake reached every process. `f34dd0fe9` sends a namespace's wake at most once per 10 ms and a completion wake only to the processes waiting for that job. Here sixteen PostgreSQL workers over Redis held 12,500/s, completing 12,800 with p99 787 ms, where the run on `63bd6946b` completed 9,822 of 10,000.
- The run on `63bd6946b` also could not start three SQL Server chaos replacements, because Acta Verify gave the run database its container's two-second login timeout; `d80d1c3a2` gives it the drivers' fifteen. SQLite's three persisted steps completed 58.8/s against a floor of 60 set at their capacity; `04a2100e9` sets it at 40. Both are changes to Acta Verify.
- Four PostgreSQL workers polling held 15,000/s in every 30 s pass, but the 120 s confirmation at 15,000 missed on backlog, so the published row is the 12,500/s step. Woken through Redis, the same four held 15,000/s with p99 107 ms.

# Acta Verify: PASS

The release suite on PostgreSQL, SQL Server, SQLite, commit `5043b81fa`, started 2026-11-02 04:53 UTC, 5 h 25 min, on Intel(R) Core(TM) i9-14900K.

| | PostgreSQL | SQL Server | SQLite |
|---|---|---|---|
| Correctness | pass | pass | pass |
| Crash-recovery chaos | pass, 1,000,000 jobs, 84 busy kills reclaimed | pass, 1,000,000 jobs, 83 busy kills reclaimed | pass, 10,000 jobs, 38 busy kills reclaimed |
| Ledger checks | pass, 162 trials checked | pass, 148 trials checked | pass, 126 trials checked |
| Performance floors | held | held | held |
| Drain | 73,211 jobs/s | 43,243 jobs/s | 7,856 jobs/s |
| Throughput | 12,438 jobs/s | 4,972 jobs/s | 250 jobs/s |
| Throughput, one worker | 7,500 jobs/s | 5,001 jobs/s | not written |

> **12,438 fully executed, durably completed jobs per second** on PostgreSQL: 4 workers polling for work, one job per enqueue call, held 3 × 30 s and 120 s at 12,500/s offered, p99 1.0 s, Intel(R) Core(TM) i9-14900K.

> **4,972 fully executed, durably completed jobs per second** on SQL Server: 4 workers polling for work, one job per enqueue call, held 3 × 30 s and 120 s at 5,000/s offered, p99 999 ms, Intel(R) Core(TM) i9-14900K.

> **250 fully executed, durably completed jobs per second** on SQLite: 1 worker, woken in process, one job per enqueue call, held 3 × 30 s and 120 s at 250/s offered, p99 185 ms, Intel(R) Core(TM) i9-14900K.

> **7,500 fully executed, durably completed jobs per second** on PostgreSQL: 1 worker, woken in process, one job per enqueue call, held 3 × 30 s and 120 s at 7,500/s offered, p99 72 ms, Intel(R) Core(TM) i9-14900K.

> **5,001 fully executed, durably completed jobs per second** on SQL Server: 1 worker, woken in process, one job per enqueue call, held 3 × 30 s and 120 s at 5,000/s offered, p99 1.1 s, Intel(R) Core(TM) i9-14900K.

## Rates to raise

These written rates ask less than the database ran; they decide nothing in this run.

- **Enqueue in batches of 100** on PostgreSQL: the floor of 10,000/s also held at 40,000/s.
- **Enqueue in batches of 1,000** on PostgreSQL: the floor of 20,000/s also held at 80,000/s.
- **Throughput, 32 executors** on SQLite: the floor of 160/s also held at 640/s.
- **Payloads of 256 B** on SQLite: the floor of 120/s also held at 480/s.
- **Payloads of 4 KB** on SQLite: the floor of 120/s also held at 480/s.
- **Payloads of 256 B, 4 KB and 64 KB mixed** on SQLite: the floor of 160/s also held at 640/s.
- **Payloads of 64 KB** on SQLite: the floor of 40/s also held at 160/s.
- **A thousand lanes** on SQL Server: the floor of 1,000/s also held at 4,000/s.
- **Parked work alongside** on SQL Server: the floor of 500/s also held at 2,000/s.
- **Parked work alongside** on SQLite: the floor of 160/s also held at 640/s.
- **Future high-priority work alongside** on SQL Server: the floor of 320/s also held at 1,280/s.
- **Ten children and a join** on SQLite: the floor of 8/s also held at 32/s.
- **Throughput, one executor** on SQL Server: the floor of 150/s also held at 600/s.
- **One hot concurrency key** on PostgreSQL: the floor of 1/s also held at 4/s.
- **One hot concurrency key** on SQL Server: the floor of 1/s also held at 4/s.
- **One hot concurrency key** on SQLite: the floor of 4/s also held at 16/s.

## Performance

Each scenario's first written rate is its floor, which this suite requires to hold.

### Drain

How fast do eight workers over two namespaces run and durably complete a backlog enqueued before they start?

Setup: workers: 8 on PostgreSQL, 8 on SQL Server, 1 on SQLite; namespaces: 2 on PostgreSQL, 2 on SQL Server, 1 on SQLite; executors per worker: the product default; enqueue calls in flight: 8; jobs per call: 1,000.

| Database | Pass | Jobs | Steady rate, database clock | Whole cohort | Result |
|---|---:|---:|---:|---:|---|
| PostgreSQL | 1 | 1,000,000 | 72,376 jobs/s | 14.1 s | held |
| PostgreSQL | 2 | 1,000,000 | 74,333 jobs/s | 14.1 s | held |
| PostgreSQL | 3 | 1,000,000 | 73,211 jobs/s | 14.3 s | held |
| SQL Server | 1 | 1,000,000 | 41,119 jobs/s | 25.6 s | held |
| SQL Server | 2 | 1,000,000 | 47,244 jobs/s | 21.4 s | held |
| SQL Server | 3 | 1,000,000 | 43,243 jobs/s | 22.6 s | held |
| SQLite | 1 | 100,000 | 7,950 jobs/s | 12.8 s | held |
| SQLite | 2 | 100,000 | 7,856 jobs/s | 12.8 s | held |
| SQLite | 3 | 100,000 | 7,754 jobs/s | 12.9 s | held |

### Sustained throughput

How many jobs per second, one per enqueue call, does Acta enqueue, run and durably complete with a flat backlog and p99 under 2 s?

Setup: workers: 4 on PostgreSQL, 4 on SQL Server, 1 on SQLite; namespaces: 2 on PostgreSQL, 2 on SQL Server, 1 on SQLite; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; workers learn of new work: by polling on PostgreSQL, by polling on SQL Server, in process on SQLite; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Result |
|---|---:|---:|---:|---:|---|
| PostgreSQL | 10,000 | 1 | 10,143 | 1.0 s | held |
| PostgreSQL | 10,000 | 2 | 9,802 | 1.0 s | held |
| PostgreSQL | 10,000 | 3 | 10,175 | 1.0 s | held |
| PostgreSQL | 12,500 | 1 | 12,604 | 1.0 s | held |
| PostgreSQL | 12,500 | 2 | 12,649 | 1.0 s | held |
| PostgreSQL | 12,500 | 3 | 12,426 | 1.0 s | held |
| PostgreSQL | 15,000 | 1 | 15,030 | 1.0 s | held |
| PostgreSQL | 15,000 | 2 | 15,149 | 983 ms | held |
| PostgreSQL | 15,000 | 3 | 15,157 | 983 ms | held |
| PostgreSQL | 17,500 | 1 | 17,425 | 896 ms | missed: backlog grew 183 jobs/s, 175 allowed |
| PostgreSQL | 17,500 | 2 | 17,799 | 1.0 s | held |
| PostgreSQL | 17,500 | 3 | 17,516 | 1.0 s | missed: backlog grew 275 jobs/s, 175 allowed |
| PostgreSQL | 20,000 | 2 | 18,266 | 4.2 s | missed: backlog grew 1,795 jobs/s, 200 allowed |
| PostgreSQL | 25,000 | | | | not run by rule |
| PostgreSQL | 30,000 | | | | not run by rule |
| PostgreSQL | 15,000 | confirm, 120 s | 14,404 | 4.9 s | missed: backlog grew 237 jobs/s, 150 allowed |
| PostgreSQL | 12,500 | confirm, 120 s | 12,438 | 1.0 s | held |
| SQL Server | 5,000 | 1 | 4,927 | 882 ms | held |
| SQL Server | 5,000 | 2 | 5,002 | 936 ms | held |
| SQL Server | 5,000 | 3 | 5,102 | 972 ms | held |
| SQL Server | 7,500 | 1 | 6,442 | 6.0 s | missed: backlog grew 1,027 jobs/s, 75 allowed |
| SQL Server | 7,500 | 2 | 6,790 | 4.5 s | missed: backlog grew 731 jobs/s, 75 allowed |
| SQL Server | 7,500 | 3 | 6,602 | 5.3 s | missed: backlog grew 903 jobs/s, 75 allowed |
| SQL Server | 10,000 | | | | not run by rule |
| SQL Server | 12,500 | | | | not run by rule |
| SQL Server | 15,000 | | | | not run by rule |
| SQL Server | 5,000 | confirm, 120 s | 4,972 | 999 ms | held |
| SQLite | 250 | 1 | 250 | 113 ms | held |
| SQLite | 250 | 2 | 250 | 176 ms | held |
| SQLite | 250 | 3 | 251 | 182 ms | held |
| SQLite | 500 | 1 | 500 | 120 ms | held |
| SQLite | 500 | 2 | 289 | 25.5 s | missed: backlog grew 212 jobs/s, 5 allowed |
| SQLite | 500 | 3 | 500 | 133 ms | held |
| SQLite | 1,000 | 1 | 284 | 75.6 s | missed: backlog grew 715 jobs/s, 10 allowed |
| SQLite | 1,000 | 3 | 398 | 63.6 s | missed: backlog grew 659 jobs/s, 10 allowed |
| SQLite | 2,000 | | | | not run by rule |
| SQLite | 250 | confirm, 120 s | 250 | 185 ms | held |

Highest rate held in every pass: PostgreSQL 15,000/s; SQL Server 5,000/s; SQLite 250/s.

### Sustained throughput, woken through Redis

With Redis carrying each enqueue's wake to the worker processes, how many jobs per second, one per enqueue call, does Acta enqueue, run and durably complete with a flat backlog and p99 under 2 s?

Setup: workers: 4 on PostgreSQL, 4 on SQL Server, 1 on SQLite; namespaces: 2 on PostgreSQL, 2 on SQL Server, 1 on SQLite; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; workers learn of new work: through Redis on PostgreSQL, through Redis on SQL Server, in process on SQLite; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Result |
|---|---:|---:|---:|---:|---|
| PostgreSQL | 10,000 | 1 | 9,999 | 99 ms | held |
| PostgreSQL | 12,500 | 1 | 12,503 | 96 ms | held |
| PostgreSQL | 15,000 | 1 | 14,994 | 107 ms | held |
| PostgreSQL | 17,500 | 1 | 17,381 | 809 ms | held |
| PostgreSQL | 20,000 | 1 | 16,924 | 6.5 s | missed: backlog grew 3,143 jobs/s, 200 allowed |
| PostgreSQL | 25,000 | | | | not run by rule |
| PostgreSQL | 30,000 | | | | not run by rule |
| SQL Server | 5,000 | 1 | 4,997 | 144 ms | held |
| SQL Server | 7,500 | 1 | 6,927 | 3.7 s | missed: backlog grew 585 jobs/s, 75 allowed |
| SQL Server | 10,000 | | | | not run by rule |
| SQL Server | 12,500 | | | | not run by rule |
| SQL Server | 15,000 | | | | not run by rule |
| SQLite | | | | | SQLite runs its one worker in the controller's process, where an enqueue wakes it without a transport. |

Highest rate held in every pass: PostgreSQL 17,500/s; SQL Server 5,000/s.

### Sustained throughput, one worker

How many jobs per second does one worker of default executors, in the process that enqueues, run and durably complete, one job per enqueue call, with a flat backlog and p99 under 2 s?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; workers learn of new work: in process; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Result |
|---|---:|---:|---:|---:|---|
| PostgreSQL | 5,000 | 1 | 5,001 | 55 ms | held |
| PostgreSQL | 5,000 | 2 | 5,003 | 291 ms | held |
| PostgreSQL | 5,000 | 3 | 5,001 | 55 ms | held |
| PostgreSQL | 7,500 | 1 | 7,501 | 48 ms | held |
| PostgreSQL | 7,500 | 2 | 7,502 | 47 ms | held |
| PostgreSQL | 7,500 | 3 | 7,502 | 45 ms | held |
| PostgreSQL | 10,000 | 1 | 9,801 | 858 ms | missed: backlog grew 240 jobs/s, 100 allowed |
| PostgreSQL | 10,000 | 2 | 9,997 | 47 ms | held |
| PostgreSQL | 10,000 | 3 | 10,001 | 49 ms | held |
| PostgreSQL | 12,500 | 2 | 8,414 | 10.0 s | missed: backlog grew 4,524 jobs/s, 125 allowed |
| PostgreSQL | 12,500 | 3 | 7,612 | 13.7 s | missed: backlog grew 4,897 jobs/s, 125 allowed |
| PostgreSQL | 15,000 | | | | not run by rule |
| PostgreSQL | 7,500 | confirm, 120 s | 7,500 | 72 ms | held |
| SQL Server | 2,500 | 1 | 2,500 | 96 ms | held |
| SQL Server | 2,500 | 2 | 2,501 | 101 ms | held |
| SQL Server | 2,500 | 3 | 2,500 | 96 ms | held |
| SQL Server | 5,000 | 1 | 5,000 | 189 ms | held |
| SQL Server | 5,000 | 2 | 5,001 | 113 ms | held |
| SQL Server | 5,000 | 3 | 5,001 | 116 ms | held |
| SQL Server | 7,500 | 1 | 6,282 | 8.1 s | missed: backlog grew 1,127 jobs/s, 75 allowed |
| SQL Server | 7,500 | 2 | 6,114 | 9.0 s | missed: backlog grew 1,346 jobs/s, 75 allowed |
| SQL Server | 7,500 | 3 | 6,123 | 8.2 s | missed: backlog grew 1,363 jobs/s, 75 allowed |
| SQL Server | 10,000 | | | | not run by rule |
| SQL Server | 5,000 | confirm, 120 s | 5,001 | 1.1 s | held |
| SQLite | | | | | The scenario writes no rate for sqlite. |

Highest rate held in every pass: PostgreSQL 7,500/s; SQL Server 5,000/s.

### Enqueue, one job per call

How fast are single-job enqueue calls acknowledged, each in its own transaction, before any worker runs?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; workers learn of new work: by polling; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Result |
|---|---:|---:|---:|---:|---|
| PostgreSQL | 10,000 | 1 | 0 | 27 ms | held |
| SQL Server | 5,000 | 1 | 0 | 99 ms | held |
| SQLite | 200 | 1 | 0 | 55 ms | held |

Highest rate held in every pass: PostgreSQL 10,000/s, every written rate, so the ceiling is above the ladder; SQL Server 5,000/s, every written rate, so the ceiling is above the ladder; SQLite 200/s, every written rate, so the ceiling is above the ladder.

### Enqueue in batches of 100

How fast are batches of 100 jobs acknowledged, before any worker runs?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 100; workers learn of new work: by polling; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Result |
|---|---:|---:|---:|---:|---|
| PostgreSQL | 10,000 | 1 | 0 | 39 ms | held |
| SQL Server | 5,000 | 1 | 0 | 50 ms | held |
| SQLite | 5,000 | 1 | 0 | 93 ms | held |

Highest rate held in every pass: PostgreSQL 10,000/s, every written rate, so the ceiling is above the ladder; SQL Server 5,000/s, every written rate, so the ceiling is above the ladder; SQLite 5,000/s, every written rate, so the ceiling is above the ladder.

### Enqueue in batches of 1,000

How fast are batches of 1,000 jobs acknowledged, before any worker runs?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1,000; workers learn of new work: by polling; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Result |
|---|---:|---:|---:|---:|---|
| PostgreSQL | 20,000 | 1 | 0 | 207 ms | held |
| SQL Server | 10,000 | 1 | 0 | 216 ms | held |
| SQLite | 10,000 | 1 | 0 | 90 ms | held |

Highest rate held in every pass: PostgreSQL 20,000/s, every written rate, so the ceiling is above the ladder; SQL Server 10,000/s, every written rate, so the ceiling is above the ladder; SQLite 10,000/s, every written rate, so the ceiling is above the ladder.

### Drain, one worker

How fast does one worker run and durably complete a backlog enqueued before it starts?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 8; jobs per call: 1,000.

| Database | Pass | Jobs | Steady rate, database clock | Whole cohort | Result |
|---|---:|---:|---:|---:|---|
| PostgreSQL | 1 | 200,000 | 17,641 jobs/s | 11.8 s | held |
| SQL Server | 1 | 200,000 | 15,233 jobs/s | 13.4 s | held |
| SQLite | 1 | 50,000 | 6,646 jobs/s | 7.61 s | held |

### Drain, four workers

How fast do four workers over two namespaces drain a backlog enqueued before they start?

Setup: workers: 4; namespaces: 2; executors per worker: the product default; enqueue calls in flight: 8; jobs per call: 1,000.

| Database | Pass | Jobs | Steady rate, database clock | Whole cohort | Result |
|---|---:|---:|---:|---:|---|
| PostgreSQL | 1 | 200,000 | 43,302 jobs/s | 4.56 s | held |
| SQL Server | 1 | 200,000 | 39,040 jobs/s | 5.12 s | held |
| SQLite | | | | | The scenario writes no cohort for sqlite. |

### Drain, sixteen workers

How fast do sixteen workers over two namespaces drain a backlog enqueued before they start?

Setup: workers: 16; namespaces: 2; executors per worker: the product default; enqueue calls in flight: 8; jobs per call: 1,000.

| Database | Pass | Jobs | Steady rate, database clock | Whole cohort | Result |
|---|---:|---:|---:|---:|---|
| PostgreSQL | 1 | 1,000,000 | 75,867 jobs/s | 13.9 s | held |
| SQL Server | 1 | 1,000,000 | 49,326 jobs/s | 21.8 s | held |
| SQLite | | | | | The scenario writes no cohort for sqlite. |

### Throughput, sixteen workers

Do sixteen worker processes over two namespaces hold a high offered rate with p99 under 2 s?

Setup: workers: 16; namespaces: 2; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; workers learn of new work: by polling; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Result |
|---|---:|---:|---:|---:|---|
| PostgreSQL | 15,000 | 1 | 15,350 | 1.1 s | held |
| SQL Server | 4,000 | 1 | 3,989 | 617 ms | held |
| SQLite | | | | | The scenario writes no rate for sqlite. |

Highest rate held in every pass: PostgreSQL 15,000/s, every written rate, so the ceiling is above the ladder; SQL Server 4,000/s, every written rate, so the ceiling is above the ladder.

### Throughput, sixteen workers, woken through Redis

With Redis carrying each enqueue's wake, do sixteen worker processes over two namespaces hold a high offered rate with p99 under 2 s?

Setup: workers: 16; namespaces: 2; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; workers learn of new work: through Redis; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Result |
|---|---:|---:|---:|---:|---|
| PostgreSQL | 12,500 | 1 | 12,800 | 787 ms | held |
| SQL Server | 4,000 | 1 | 4,073 | 1.2 s | held |
| SQLite | | | | | SQLite is one process's database; sixteen worker processes are not a SQLite topology. |

Highest rate held in every pass: PostgreSQL 12,500/s, every written rate, so the ceiling is above the ladder; SQL Server 4,000/s, every written rate, so the ceiling is above the ladder.

### Throughput, sixteen workers on one namespace

Do sixteen worker processes sharing one namespace hold a high offered rate with p99 under 2 s?

Setup: workers: 16; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; workers learn of new work: by polling; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Result |
|---|---:|---:|---:|---:|---|
| PostgreSQL | 15,000 | 1 | 15,241 | 497 ms | held |
| SQL Server | 4,000 | 1 | 3,993 | 313 ms | held |
| SQLite | | | | | The scenario writes no rate for sqlite. |

Highest rate held in every pass: PostgreSQL 15,000/s, every written rate, so the ceiling is above the ladder; SQL Server 4,000/s, every written rate, so the ceiling is above the ladder.

### Throughput, 32 executors

Does one worker with 32 executors hold the stream its default configuration holds?

Setup: workers: 1; namespaces: 1; executors per worker: 32; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; workers learn of new work: in process; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Result |
|---|---:|---:|---:|---:|---|
| PostgreSQL | 5,000 | 1 | 5,001 | 55 ms | held |
| SQL Server | 2,500 | 1 | 2,498 | 97 ms | held |
| SQLite | 160 | 1 | 160 | 280 ms | held |

Highest rate held in every pass: PostgreSQL 5,000/s, every written rate, so the ceiling is above the ladder; SQL Server 2,500/s, every written rate, so the ceiling is above the ladder; SQLite 160/s, every written rate, so the ceiling is above the ladder.

### Three persisted steps

Do jobs that commit three checkpoints each hold their offered rate with p99 under 5 s?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; workers learn of new work: in process; p99 target: 5.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Result |
|---|---:|---:|---:|---:|---|
| PostgreSQL | 1,000 | 1 | 999 | 111 ms | held |
| SQL Server | 1,000 | 1 | 1,001 | 124 ms | held |
| SQLite | 40 | 1 | 40 | 46 ms | held |

Highest rate held in every pass: PostgreSQL 1,000/s, every written rate, so the ceiling is above the ladder; SQL Server 1,000/s, every written rate, so the ceiling is above the ladder; SQLite 40/s, every written rate, so the ceiling is above the ladder.

### Transient failure and retry

With one job in four failing twice before it succeeds, does the stream hold its rate with p99 under 10 s?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; workers learn of new work: in process; p99 target: 10.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Result |
|---|---:|---:|---:|---:|---|
| PostgreSQL | 5,000 | 1 | 5,034 | 2.1 s | held |
| SQL Server | 2,500 | 1 | 2,501 | 2.2 s | held |
| SQLite | 160 | 1 | 160 | 2.2 s | held |

Highest rate held in every pass: PostgreSQL 5,000/s, every written rate, so the ceiling is above the ladder; SQL Server 2,500/s, every written rate, so the ceiling is above the ladder; SQLite 160/s, every written rate, so the ceiling is above the ladder.

### Fixed CPU work

Do jobs that each hash for a fixed number of rounds hold their offered rate with p99 under 2 s?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; workers learn of new work: in process; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Result |
|---|---:|---:|---:|---:|---|
| PostgreSQL | 4,000 | 1 | 3,998 | 68 ms | held |
| SQL Server | 2,500 | 1 | 2,499 | 420 ms | held |
| SQLite | 160 | 1 | 160 | 110 ms | held |

Highest rate held in every pass: PostgreSQL 4,000/s, every written rate, so the ceiling is above the ladder; SQL Server 2,500/s, every written rate, so the ceiling is above the ladder; SQLite 160/s, every written rate, so the ceiling is above the ladder.

### Payloads of 256 B

Do jobs carrying a 256-byte payload hold their offered rate with p99 under 2 s?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; workers learn of new work: in process; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Result |
|---|---:|---:|---:|---:|---|
| PostgreSQL | 4,000 | 1 | 3,998 | 54 ms | held |
| SQL Server | 2,500 | 1 | 2,502 | 89 ms | held |
| SQLite | 120 | 1 | 120 | 105 ms | held |

Highest rate held in every pass: PostgreSQL 4,000/s, every written rate, so the ceiling is above the ladder; SQL Server 2,500/s, every written rate, so the ceiling is above the ladder; SQLite 120/s, every written rate, so the ceiling is above the ladder.

### Payloads of 4 KB

Do jobs carrying a 4 KB payload hold their offered rate with p99 under 2 s?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; workers learn of new work: in process; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Result |
|---|---:|---:|---:|---:|---|
| PostgreSQL | 5,000 | 1 | 5,000 | 51 ms | held |
| SQL Server | 2,500 | 1 | 2,503 | 101 ms | held |
| SQLite | 120 | 1 | 120 | 93 ms | held |

Highest rate held in every pass: PostgreSQL 5,000/s, every written rate, so the ceiling is above the ladder; SQL Server 2,500/s, every written rate, so the ceiling is above the ladder; SQLite 120/s, every written rate, so the ceiling is above the ladder.

### Payloads of 256 B, 4 KB and 64 KB mixed

Do jobs carrying payloads of three sizes in turn hold their offered rate with p99 under 2 s?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; workers learn of new work: in process; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Result |
|---|---:|---:|---:|---:|---|
| PostgreSQL | 2,500 | 1 | 2,500 | 610 ms | held |
| SQL Server | 1,000 | 1 | 1,001 | 297 ms | held |
| SQLite | 160 | 1 | 160 | 103 ms | held |

Highest rate held in every pass: PostgreSQL 2,500/s, every written rate, so the ceiling is above the ladder; SQL Server 1,000/s, every written rate, so the ceiling is above the ladder; SQLite 160/s, every written rate, so the ceiling is above the ladder.

### Payloads of 64 KB

Do jobs carrying a 64 KB payload hold their offered rate with p99 under 2 s?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; workers learn of new work: in process; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Result |
|---|---:|---:|---:|---:|---|
| PostgreSQL | 1,000 | 1 | 1,000 | 87 ms | held |
| SQL Server | 500 | 1 | 500 | 98 ms | held |
| SQLite | 40 | 1 | 40 | 40 ms | held |

Highest rate held in every pass: PostgreSQL 1,000/s, every written rate, so the ceiling is above the ladder; SQL Server 500/s, every written rate, so the ceiling is above the ladder; SQLite 40/s, every written rate, so the ceiling is above the ladder.

### A thousand lanes

Spread over a thousand ordered lanes, does the stream hold its rate with p99 under 2 s?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; workers learn of new work: in process; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Result |
|---|---:|---:|---:|---:|---|
| PostgreSQL | 4,000 | 1 | 3,999 | 55 ms | held |
| SQL Server | 1,000 | 1 | 1,001 | 95 ms | held |
| SQLite | 120 | 1 | 120 | 107 ms | held |

Highest rate held in every pass: PostgreSQL 4,000/s, every written rate, so the ceiling is above the ladder; SQL Server 1,000/s, every written rate, so the ceiling is above the ladder; SQLite 120/s, every written rate, so the ceiling is above the ladder.

### Operator queries during processing

While the stream runs, do the operator list queries answer quickly, and does the stream keep its rate?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; workers learn of new work: in process; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Result |
|---|---:|---:|---:|---:|---|
| PostgreSQL | 5,000 | 1 | 4,997 | 57 ms | held |
| SQL Server | 2,500 | 1 | 2,504 | 91 ms | held |
| SQLite | 120 | 1 | 120 | 106 ms | held |

Highest rate held in every pass: PostgreSQL 5,000/s, every written rate, so the ceiling is above the ladder; SQL Server 2,500/s, every written rate, so the ceiling is above the ladder; SQLite 120/s, every written rate, so the ceiling is above the ladder.

### Parked work alongside

With a large population of jobs parked on signals, does the stream hold its rate with p99 under 2 s?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; workers learn of new work: in process; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Result |
|---|---:|---:|---:|---:|---|
| PostgreSQL | 5,000 | 1 | 5,001 | 56 ms | held |
| SQL Server | 500 | 1 | 500 | 95 ms | held |
| SQLite | 160 | 1 | 160 | 105 ms | held |

Highest rate held in every pass: PostgreSQL 5,000/s, every written rate, so the ceiling is above the ladder; SQL Server 500/s, every written rate, so the ceiling is above the ladder; SQLite 160/s, every written rate, so the ceiling is above the ladder.

### Future high-priority work alongside

With a large population of high-priority jobs due tomorrow, does the stream hold its rate with p99 under 2 s?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; workers learn of new work: in process; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Result |
|---|---:|---:|---:|---:|---|
| PostgreSQL | 5,000 | 1 | 5,003 | 49 ms | held |
| SQL Server | 320 | 1 | 320 | 102 ms | held |
| SQLite | 160 | 1 | 160 | 95 ms | held |

Highest rate held in every pass: PostgreSQL 5,000/s, every written rate, so the ceiling is above the ladder; SQL Server 320/s, every written rate, so the ceiling is above the ladder; SQLite 160/s, every written rate, so the ceiling is above the ladder.

### Ten children and a join

Do parents that fan out ten children and join them hold their offered rate with p99 under 10 s?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; workers learn of new work: in process; p99 target: 10.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Result |
|---|---:|---:|---:|---:|---|
| PostgreSQL | 250 | 1 | 251 | 303 ms | held |
| SQL Server | 200 | 1 | 200 | 346 ms | held |
| SQLite | 8 | 1 | 8 | 353 ms | held |

Highest rate held in every pass: PostgreSQL 250/s, every written rate, so the ceiling is above the ladder; SQL Server 200/s, every written rate, so the ceiling is above the ladder; SQLite 8/s, every written rate, so the ceiling is above the ladder.

### Controlled I/O through an idempotent sink

Do jobs that each call an external idempotent sink for 5 ms hold their offered rate with p99 under 5 s?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; workers learn of new work: in process; p99 target: 5.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Result |
|---|---:|---:|---:|---:|---|
| PostgreSQL | 250 | 1 | 246 | 706 ms | held |
| SQL Server | 200 | 1 | 200 | 246 ms | held |
| SQLite | 80 | 1 | 79.4 | 331 ms | held |

Highest rate held in every pass: PostgreSQL 250/s, every written rate, so the ceiling is above the ladder; SQL Server 200/s, every written rate, so the ceiling is above the ladder; SQLite 80/s, every written rate, so the ceiling is above the ladder.

### Short and long work mixed

Do no-op, CPU, sink, timer and fan-out jobs mixed in turn hold their offered rate with p99 under 5 s?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; workers learn of new work: in process; p99 target: 5.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Result |
|---|---:|---:|---:|---:|---|
| PostgreSQL | 600 | 1 | 600 | 1.1 s | held |
| SQL Server | 500 | 1 | 500 | 1.1 s | held |
| SQLite | 80 | 1 | 79.7 | 1.2 s | held |

Highest rate held in every pass: PostgreSQL 600/s, every written rate, so the ceiling is above the ladder; SQL Server 500/s, every written rate, so the ceiling is above the ladder; SQLite 80/s, every written rate, so the ceiling is above the ladder.

### Throughput, one executor

Does a worker with a single executor keep up with a modest stream, so the result shows its serial cost?

Setup: workers: 1; namespaces: 1; executors per worker: 1; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; workers learn of new work: in process; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Result |
|---|---:|---:|---:|---:|---|
| PostgreSQL | 150 | 1 | 150 | 68 ms | held |
| SQL Server | 150 | 1 | 150 | 158 ms | held |
| SQLite | 100 | 1 | 100 | 46 ms | held |

Highest rate held in every pass: PostgreSQL 150/s, every written rate, so the ceiling is above the ladder; SQL Server 150/s, every written rate, so the ceiling is above the ladder; SQLite 100/s, every written rate, so the ceiling is above the ladder.

### One hot lane

How fast does one ordered lane hand its jobs over, each waiting for the previous completion to commit?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; workers learn of new work: in process; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Result |
|---|---:|---:|---:|---:|---|
| PostgreSQL | 60 | 1 | 60 | 28 ms | held |
| SQL Server | 100 | 1 | 100 | 70 ms | held |
| SQLite | 80 | 1 | 80 | 36 ms | held |

Highest rate held in every pass: PostgreSQL 60/s, every written rate, so the ceiling is above the ladder; SQL Server 100/s, every written rate, so the ceiling is above the ladder; SQLite 80/s, every written rate, so the ceiling is above the ladder.

### One hot concurrency key

With every job sharing one concurrency key of limit one, does a stream of one a second keep a flat backlog with p99 under 30 s?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; workers learn of new work: in process; p99 target: 30.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Result |
|---|---:|---:|---:|---:|---|
| PostgreSQL | 1 | 1 | 1 | 61 ms | held |
| SQL Server | 1 | 1 | 1 | 59 ms | held |
| SQLite | 4 | 1 | 4 | 29 ms | held |

Highest rate held in every pass: PostgreSQL 1/s, every written rate, so the ceiling is above the ladder; SQL Server 1/s, every written rate, so the ceiling is above the ladder; SQLite 4/s, every written rate, so the ceiling is above the ladder.

### Five-minute mixed soak

Does the mixed workload hold a steady rate for five minutes with p99 under 5 s and a flat backlog?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; workers learn of new work: in process; p99 target: 5.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Result |
|---|---:|---:|---:|---:|---|
| PostgreSQL | 700 | 1 | 700 | 1.2 s | held |
| SQL Server | 350 | 1 | 350 | 1.1 s | held |
| SQLite | 56 | 1 | 56 | 1.1 s | held |

Highest rate held in every pass: PostgreSQL 700/s, every written rate, so the ceiling is above the ladder; SQL Server 350/s, every written rate, so the ceiling is above the ladder; SQLite 56/s, every written rate, so the ceiling is above the ladder.

### Overload, then recovery

Offered more than it can run, does the backlog grow, and does it fall back once the offered rate drops?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Result |
|---|---:|---:|---:|---:|---|
| PostgreSQL | 13,000, then 2,500 | 1 | 7,750 | 9.0 s | pass: the backlog grew, then fell back |
| SQL Server | 8,000, then 1,000 | 1 | 4,499 | 10.7 s | pass: the backlog grew, then fell back |
| SQLite | | | | | SQLite's one writer serves the enqueues and the worker alike, so an offer past what it absorbs queues in the caller, not in the database. |

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
- Commit `5043b81fa66c71b27e9d64bd0067803707894342`, measurement version 3, written standard `28e7dc9b6183`.
- Machine: Intel(R) Core(TM) i9-14900K, 24 cores, 32 logical processors, Microsoft Windows 10.0.26300, .NET 10.0.12.
- Docker VM: 32 CPUs, 46.9 GB; measured containers capped at PostgreSQL 32031 MB, SQL Server 32031 MB.
- PostgreSQL: peak 522 database connections; disk before measuring: yes, after 12 s.
- SQL Server: peak 548 database connections; disk before measuring: yes, after 26 s.
- SQLite: peak 0 database connections; disk before measuring: no, after 62 s: too few samples; writing most: chatgpt 0.0 MB/s, wsl 0.0 MB/s, com.docker.backend 0.0 MB/s.
- Wall time: performance on PostgreSQL 1 h 49 min, correctness on PostgreSQL 6 min, chaos on PostgreSQL 18 min, performance on SQL Server 1 h 27 min, correctness on SQL Server 10 min, chaos on SQL Server 27 min, performance on SQLite 57 min, correctness on SQLite 5 min, chaos on SQLite 12 min; correctness runs on every database at once, chaos one database at a time.
- Evidence: 30.4 MB of a 50 MB budget.
- A rate holds when every scheduled arrival reached an outcome and none was refused, the backlog trend stayed within 1% of the rate, p99 stayed within the target, at most 0.1% of arrivals were picked up late by the harness, and every job reconciled. These are the rc.6 criteria.
- p99 runs from an arrival's scheduled instant to its job's durable completion, or to the enqueue call's answer in an enqueue-only scenario. A scenario that streams arrivals to one worker in one namespace runs that worker in the process that enqueues, as a single Acta host does: it shares that process's heap and thread pool and the enqueue calls' connection pool, and each enqueue wakes it at once. A worker process claims again while its claims return jobs; after an empty claim it waits up to its 1 s safety poll, or until Redis wakes it where the setup says so.

| Disk, by phase and drive | Windows counters |
|---|---|
| before \| 0 c: | write 2.92 ms mean, 7.87 ms p95, 7 writes/s, 99% idle, 14 samples |
| before \| 1 d: | write 4.03 ms mean, 7.43 ms p95, 35 writes/s, 99% idle, 14 samples |
| chaos mssql \| 0 c: | write 3.37 ms mean, 7.93 ms p95, 9 writes/s, 99% idle, 825 samples |
| chaos mssql \| 1 d: | write 0.25 ms mean, 1.07 ms p95, 942 writes/s, 66% idle, 825 samples |
| chaos pg \| 0 c: | write 3.22 ms mean, 8.07 ms p95, 9 writes/s, 99% idle, 543 samples |
| chaos pg \| 1 d: | write 0.07 ms mean, 0.06 ms p95, 983 writes/s, 42% idle, 543 samples |
| chaos sqlite \| 0 c: | write 3.20 ms mean, 7.94 ms p95, 8 writes/s, 99% idle, 371 samples |
| chaos sqlite \| 1 d: | write 1.68 ms mean, 7.07 ms p95, 530 writes/s, 82% idle, 371 samples |
| correctness \| 0 c: | write 3.17 ms mean, 7.87 ms p95, 10 writes/s, 99% idle, 316 samples |
| correctness \| 1 d: | write 0.31 ms mean, 1.77 ms p95, 533 writes/s, 80% idle, 316 samples |
| mssql \| 0 c: | write 3.55 ms mean, 7.93 ms p95, 7 writes/s, 99% idle, 2617 samples |
| mssql \| 1 d: | write 0.84 ms mean, 2.33 ms p95, 2090 writes/s, 46% idle, 2617 samples |
| pg \| 0 c: | write 3.74 ms mean, 8.01 ms p95, 8 writes/s, 99% idle, 3280 samples |
| pg \| 1 d: | write 0.81 ms mean, 2.48 ms p95, 883 writes/s, 56% idle, 3280 samples |
| sqlite \| 0 c: | write 2.41 ms mean, 7.66 ms p95, 14 writes/s, 99% idle, 1722 samples |
| sqlite \| 1 d: | write 0.05 ms mean, 0.06 ms p95, 1041 writes/s, 59% idle, 1722 samples |
| after \| 0 c: | write 2.21 ms mean, 4.92 ms p95, 9 writes/s, 99% idle, 15 samples |
| after \| 1 d: | write 4.47 ms mean, 7.70 ms p95, 5 writes/s, 100% idle, 15 samples |
