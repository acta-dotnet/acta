# Acta 1.0.0-rc.12: benchmark and release validation

The 1.0.0-rc.12 release evidence, measured by the project on its own machine: one Acta Verify release run on `892222333` on PostgreSQL, SQL Server, and SQLite, with reruns of two cells on the same commit. Every correctness, fault, and chaos scenario passed, every trial's ledger passed the ledger checks, and every performance floor held, one of them after reruns: sixteen PostgreSQL workers on one namespace at 15,000/s, which sits at this machine's edge with rc.11's tree as well.

- The run's commit, `892222333`, is in the tagged history, and the commit after it changes documentation and the site, so the evidence stands for the tag.
- Sixteen PostgreSQL workers on one namespace completed 16,112 jobs a second at 15,000/s offered, with p99 2.7 s against the 2.0 s target. Rerun three times on the same commit, the cell missed twice more, p99 2.3 s and 2.6 s, and then held, 15,586 jobs a second with p99 2.0 s; the reruns are listed with the scenario below. Rerun back to back on 2026-11-16, on the same machine and runtime, rc.11's tree held the cell once in two runs and an earlier rc.12 candidate's twice, and sixteen workers on two namespaces, which held in this run, missed in all four of those runs. The next release's run starts both PostgreSQL sixteen-worker ladders at 12,500/s, with 15,000 above it.
- An earlier rc.12 candidate's group wait parked on the last child still running, and SQLite's mixed-work trial fell behind its schedule for tens of seconds in about half its runs, where rc.11's tree held it in eleven runs of eleven. The wait asks for each child in turn again, and this run held mixed work on every database.
- This is measurement version 3, as in rc.11, and `acta-verify compare` sets the run beside rc.11's, both on .NET 10.0.15. All 221 scenario runs passed both times; of the measured cells, 8 read faster, 68 the same, and 18 slower, each from one run per release.
- PostgreSQL's four workers held 15,000/s offered in all three 30 s passes, but the 120 s confirmation at 15,000 missed its p99 target, 3.7 s against 2.0 s, and the one at 12,500 held, completing 12,496 jobs a second, as rc.11's completed 12,512 at 12,500.
- SQL Server's one worker held 5,000/s in all three passes, but its 120 s confirmation at 5,000 missed, p99 11 s, and the one at 2,500 held, where rc.11's held 5,000. Rerun on the same commit, the confirmation at 5,000 held, 5,000 jobs a second with p99 559 ms.
- PostgreSQL's four-worker drain read 37,478 jobs a second against rc.11's 46,761. Rerun back to back on 2026-11-16, rc.11's tree drained at 37,590 and 46,199 a second and an earlier rc.12 candidate's at 46,697 and 46,429. The other cells that read slower come from one run each and were not rerun: fifteen p99s at rates both runs held, each inside its floor.

# Acta Verify: PASS after reruns

The release suite on PostgreSQL, SQL Server, SQLite, commit `892222333`, started 2026-11-16 22:10 UTC, 7 h 9 min, on Intel(R) Core(TM) i9-14900K.

| | PostgreSQL | SQL Server | SQLite |
|---|---|---|---|
| Correctness | pass | pass | pass |
| Crash-recovery chaos | pass, 1,000,000 jobs, 84 busy kills reclaimed | pass, 1,000,000 jobs, 82 busy kills reclaimed | pass, 10,000 jobs, 50 busy kills reclaimed |
| Ledger checks | pass, 158 trials checked | pass, 149 trials checked | pass, 124 trials checked |
| Performance floors | missed 1, held on rerun | held | held |
| Drain | 85,531 jobs/s | 44,334 jobs/s | 6,812 jobs/s |
| Throughput | 12,496 jobs/s | 5,001 jobs/s | 250 jobs/s |
| Throughput, one worker | 7,499 jobs/s | 2,501 jobs/s | not written |

> **12,496 fully executed, durably completed jobs per second** on PostgreSQL: 4 workers polling for work, one job per enqueue call, held 3 × 30 s and 120 s at 12,500/s offered, p99 1.0 s, Intel(R) Core(TM) i9-14900K.

> **5,001 fully executed, durably completed jobs per second** on SQL Server: 4 workers polling for work, one job per enqueue call, held 3 × 30 s and 120 s at 5,000/s offered, p99 962 ms, Intel(R) Core(TM) i9-14900K.

> **250 fully executed, durably completed jobs per second** on SQLite: 1 worker, woken in process, one job per enqueue call, held 3 × 30 s and 120 s at 250/s offered, p99 128 ms, Intel(R) Core(TM) i9-14900K.

> **7,499 fully executed, durably completed jobs per second** on PostgreSQL: 1 worker, woken in process, one job per enqueue call, held 3 × 30 s and 120 s at 7,500/s offered, p99 52 ms, Intel(R) Core(TM) i9-14900K.

> **2,501 fully executed, durably completed jobs per second** on SQL Server: 1 worker, woken in process, one job per enqueue call, held 3 × 30 s and 120 s at 2,500/s offered, p99 94 ms, Intel(R) Core(TM) i9-14900K.

## Rates to raise

These written rates ask less than the database ran; they decide nothing in this run.

- **Enqueue in batches of 100** on PostgreSQL: the floor of 10,000/s also held at 40,000/s.
- **Enqueue in batches of 1,000** on PostgreSQL: the floor of 20,000/s also held at 80,000/s.
- **Throughput, 32 executors** on SQLite: the floor of 160/s also held at 640/s.
- **Fixed CPU work** on SQLite: the floor of 160/s also held at 640/s.
- **Payloads of 4 KB** on SQLite: the floor of 120/s also held at 480/s.
- **Payloads of 256 B, 4 KB and 64 KB mixed** on SQLite: the floor of 160/s also held at 640/s.
- **Payloads of 64 KB** on SQLite: the floor of 40/s also held at 160/s.
- **A thousand lanes** on SQL Server: the floor of 1,000/s also held at 4,000/s.
- **A thousand lanes** on SQLite: the floor of 120/s also held at 480/s.
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
| PostgreSQL | 1 | 1,000,000 | 83,231 jobs/s | 14 s | 0.725 / 4.82 ms | held |
| PostgreSQL | 2 | 1,000,000 | 85,531 jobs/s | 13.2 s | 2 / 16.8 ms | held |
| PostgreSQL | 3 | 1,000,000 | 86,880 jobs/s | 12.6 s | 0.119 / 0.406 ms | held |
| SQL Server | 1 | 1,000,000 | 39,739 jobs/s | 29.2 s | 0.555 / 0.274 ms | held |
| SQL Server | 2 | 1,000,000 | 45,592 jobs/s | 22.3 s | 0.224 / 0.641 ms | held |
| SQL Server | 3 | 1,000,000 | 44,334 jobs/s | 23.1 s | 2.98 / 0.975 ms | held |
| SQLite | 1 | 100,000 | 6,914 jobs/s | 14.6 s | 0.0435 / 0.322 ms | held |
| SQLite | 2 | 100,000 | 6,531 jobs/s | 15.4 s | 0.0423 / 0.0594 ms | held |
| SQLite | 3 | 100,000 | 6,812 jobs/s | 15.1 s | 0.0403 / 0.0606 ms | held |

### Sustained throughput

How many jobs per second, one per enqueue call, does Acta enqueue, run and durably complete with a flat backlog and p99 under 2 s?

Setup: workers: 4 on PostgreSQL, 4 on SQL Server, 1 on SQLite; namespaces: 2 on PostgreSQL, 2 on SQL Server, 1 on SQLite; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; workers learn of new work: by polling on PostgreSQL, by polling on SQL Server, in process on SQLite; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Disk write, mean / p95 | Result |
|---|---:|---:|---:|---:|---:|---|
| PostgreSQL | 10,000 | 1 | 10,087 | 1.0 s | 0.0481 / 0.0537 ms | held |
| PostgreSQL | 10,000 | 2 | 10,071 | 1.0 s | 0.0388 / 0.0411 ms | held |
| PostgreSQL | 10,000 | 3 | 10,104 | 1.0 s | 0.0444 / 0.0517 ms | held |
| PostgreSQL | 12,500 | 1 | 12,477 | 1.0 s | 0.216 / 3.35 ms | held |
| PostgreSQL | 12,500 | 2 | 12,525 | 1.0 s | 0.0495 / 0.213 ms | held |
| PostgreSQL | 12,500 | 3 | 12,439 | 1.0 s | 0.227 / 2.45 ms | held |
| PostgreSQL | 15,000 | 1 | 15,246 | 1.0 s | 0.419 / 5.55 ms | held |
| PostgreSQL | 15,000 | 2 | 14,824 | 1.0 s | 0.326 / 5.39 ms | held |
| PostgreSQL | 15,000 | 3 | 14,913 | 1.0 s | 0.0432 / 0.0798 ms | held |
| PostgreSQL | 17,500 | 1 | 16,849 | 2.3 s | 0.0608 / 0.0729 ms | missed: backlog grew 714 jobs/s, 175 allowed |
| PostgreSQL | 17,500 | 2 | 16,501 | 3.4 s | 0.057 / 0.179 ms | missed: backlog grew 826 jobs/s, 175 allowed |
| PostgreSQL | 17,500 | 3 | 17,004 | 2.6 s | 0.0645 / 0.359 ms | missed: backlog grew 608 jobs/s, 175 allowed |
| PostgreSQL | 20,000 | | | | | not run by rule |
| PostgreSQL | 25,000 | | | | | not run by rule |
| PostgreSQL | 30,000 | | | | | not run by rule |
| PostgreSQL | 15,000 | confirm, 120 s | 14,569 | 3.7 s | 0.117 / 0.311 ms | missed: p99 3.7 s against a 2.0 s target |
| PostgreSQL | 12,500 | confirm, 120 s | 12,496 | 1.0 s | 0.0479 / 0.057 ms | held |
| SQL Server | 5,000 | 1 | 4,938 | 887 ms | 0.67 / 14.3 ms | held |
| SQL Server | 5,000 | 2 | 5,002 | 804 ms | 0.246 / 2.28 ms | held |
| SQL Server | 5,000 | 3 | 5,030 | 926 ms | 0.636 / 15.8 ms | held |
| SQL Server | 7,500 | 1 | 7,044 | 2.9 s | 0.0379 / 0.0735 ms | missed: backlog grew 496 jobs/s, 75 allowed |
| SQL Server | 7,500 | 2 | 6,934 | 3.8 s | 0.191 / 0.718 ms | missed: backlog grew 573 jobs/s, 75 allowed |
| SQL Server | 7,500 | 3 | 6,740 | 4.5 s | 0.246 / 0.361 ms | missed: backlog grew 785 jobs/s, 75 allowed |
| SQL Server | 10,000 | | | | | not run by rule |
| SQL Server | 12,500 | | | | | not run by rule |
| SQL Server | 15,000 | | | | | not run by rule |
| SQL Server | 5,000 | confirm, 120 s | 5,001 | 962 ms | 0.552 / 2.25 ms | held |
| SQLite | 250 | 1 | 250 | 107 ms | 0.0285 / 0.0624 ms | held |
| SQLite | 250 | 2 | 250 | 106 ms | 0.0302 / 0.0408 ms | held |
| SQLite | 250 | 3 | 250 | 116 ms | 0.0292 / 0.0297 ms | held |
| SQLite | 500 | 1 | 466 | 5.3 s | 0.0263 / 0.0304 ms | missed: backlog grew 25.3 jobs/s, 5 allowed |
| SQLite | 500 | 2 | 478 | 4.1 s | 0.025 / 0.0283 ms | missed: p99 4.1 s against a 2.0 s target |
| SQLite | 500 | 3 | 490 | 4.5 s | 0.0247 / 0.0341 ms | missed: p99 4.5 s against a 2.0 s target |
| SQLite | 1,000 | | | | | not run by rule |
| SQLite | 2,000 | | | | | not run by rule |
| SQLite | 250 | confirm, 120 s | 250 | 128 ms | 0.0297 / 0.0308 ms | held |

Highest rate held in every pass: PostgreSQL 15,000/s; SQL Server 5,000/s; SQLite 250/s.

### Sustained throughput, woken through Redis

With Redis carrying each enqueue's wake to the worker processes, how many jobs per second, one per enqueue call, does Acta enqueue, run and durably complete with a flat backlog and p99 under 2 s?

Setup: workers: 4 on PostgreSQL, 4 on SQL Server, 1 on SQLite; namespaces: 2 on PostgreSQL, 2 on SQL Server, 1 on SQLite; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; workers learn of new work: through Redis on PostgreSQL, through Redis on SQL Server, in process on SQLite; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Disk write, mean / p95 | Result |
|---|---:|---:|---:|---:|---:|---|
| PostgreSQL | 10,000 | 1 | 9,998 | 101 ms | 0.0377 / 0.0493 ms | held |
| PostgreSQL | 12,500 | 1 | 12,494 | 99 ms | 0.0379 / 0.0706 ms | held |
| PostgreSQL | 15,000 | 1 | 15,028 | 152 ms | 0.0539 / 0.065 ms | held |
| PostgreSQL | 17,500 | 1 | 16,769 | 2.3 s | 0.0595 / 0.276 ms | missed: backlog grew 666 jobs/s, 175 allowed |
| PostgreSQL | 20,000 | | | | | not run by rule |
| PostgreSQL | 25,000 | | | | | not run by rule |
| PostgreSQL | 30,000 | | | | | not run by rule |
| SQL Server | 5,000 | 1 | 4,999 | 148 ms | 0.0696 / 1.58 ms | held |
| SQL Server | 7,500 | 1 | 6,788 | 4.4 s | 0.0987 / 0.767 ms | missed: backlog grew 706 jobs/s, 75 allowed |
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
| PostgreSQL | 5,000 | 1 | 5,002 | 52 ms | 0.0342 / 0.0434 ms | held |
| PostgreSQL | 5,000 | 2 | 5,001 | 57 ms | 0.0356 / 0.0373 ms | held |
| PostgreSQL | 5,000 | 3 | 5,001 | 56 ms | 0.0512 / 0.263 ms | held |
| PostgreSQL | 7,500 | 1 | 7,497 | 41 ms | 0.0338 / 0.0364 ms | held |
| PostgreSQL | 7,500 | 2 | 7,497 | 45 ms | 0.0344 / 0.0354 ms | held |
| PostgreSQL | 7,500 | 3 | 7,495 | 297 ms | 0.0337 / 0.0352 ms | held |
| PostgreSQL | 10,000 | 1 | 9,016 | 3.5 s | 0.0531 / 0.16 ms | missed: backlog grew 933 jobs/s, 100 allowed |
| PostgreSQL | 10,000 | 2 | 9,289 | 2.2 s | 0.044 / 0.0481 ms | missed: backlog grew 676 jobs/s, 100 allowed |
| PostgreSQL | 10,000 | 3 | 9,992 | 95 ms | 0.0354 / 0.039 ms | held |
| PostgreSQL | 12,500 | 3 | 10,419 | 5.9 s | 0.0456 / 0.0386 ms | missed: backlog grew 2,150 jobs/s, 125 allowed |
| PostgreSQL | 15,000 | | | | | not run by rule |
| PostgreSQL | 7,500 | confirm, 120 s | 7,499 | 52 ms | 0.0556 / 0.0457 ms | held |
| SQL Server | 2,500 | 1 | 2,500 | 91 ms | 0.0268 / 0.113 ms | held |
| SQL Server | 2,500 | 2 | 2,500 | 94 ms | 0.346 / 9.34 ms | held |
| SQL Server | 2,500 | 3 | 2,499 | 88 ms | 0.0848 / 2.36 ms | held |
| SQL Server | 5,000 | 1 | 5,003 | 600 ms | 0.134 / 0.835 ms | held |
| SQL Server | 5,000 | 2 | 4,999 | 604 ms | 0.192 / 8.65 ms | held |
| SQL Server | 5,000 | 3 | 4,995 | 143 ms | 0.0954 / 0.569 ms | held |
| SQL Server | 7,500 | 1 | 6,302 | 7.6 s | 0.144 / 0.77 ms | missed: backlog grew 1,096 jobs/s, 75 allowed |
| SQL Server | 7,500 | 2 | 6,532 | 6.4 s | 0.171 / 0.9 ms | missed: backlog grew 881 jobs/s, 75 allowed |
| SQL Server | 7,500 | 3 | 6,268 | 8.0 s | 0.194 / 0.848 ms | missed: backlog grew 1,117 jobs/s, 75 allowed |
| SQL Server | 10,000 | | | | | not run by rule |
| SQL Server | 5,000 | confirm, 120 s | 4,966 | 11.0 s | 0.21 / 1.26 ms | missed: p99 11.0 s against a 2.0 s target |
| SQL Server | 2,500 | confirm, 120 s | 2,501 | 94 ms | 0.146 / 0.719 ms | held |
| SQLite | | | | | | The scenario writes no rate for sqlite. |

Highest rate held in every pass: PostgreSQL 7,500/s; SQL Server 5,000/s.

Reruns of this scenario on the same commit, in order:

| Database | Rerun | Offered/s | Completed/s | p99 | Disk write, mean / p95 | Result |
|---|---:|---:|---:|---:|---:|---|
| SQL Server | 2 | 2,500 | 2,500 | 90 ms | 0.0311 / 0.0742 ms | held |
| SQL Server | 2 | 5,000 | 5,002 | 1.0 s | 0.0354 / 0.407 ms | held |
| SQL Server | 2 | 7,500 | 6,171 | 8.7 s | 0.114 / 0.467 ms | missed: backlog grew 1,187 jobs/s, 75 allowed |
| SQL Server | 2 | 2,500 | 2,502 | 97 ms | 0.121 / 0.577 ms | held |
| SQL Server | 2 | 5,000 | 5,000 | 379 ms | 0.0928 / 0.336 ms | held |
| SQL Server | 2 | 7,500 | 6,280 | 7.5 s | 0.101 / 0.36 ms | missed: backlog grew 1,095 jobs/s, 75 allowed |
| SQL Server | 2 | 2,500 | 2,499 | 100 ms | 0.174 / 1.31 ms | held |
| SQL Server | 2 | 5,000 | 5,004 | 188 ms | 0.126 / 0.859 ms | held |
| SQL Server | 2 | 7,500 | 6,259 | 8.0 s | 0.204 / 0.671 ms | missed: backlog grew 1,156 jobs/s, 75 allowed |
| SQL Server | 2, confirm | 5,000 | 5,000 | 559 ms | 0.252 / 1.02 ms | held |

### Enqueue, one job per call

How fast are single-job enqueue calls acknowledged, each in its own transaction, before any worker runs?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; workers learn of new work: by polling; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Disk write, mean / p95 | Result |
|---|---:|---:|---:|---:|---:|---|
| PostgreSQL | 10,000 | 1 | 0 | 26 ms | 0.0771 / 0.0478 ms | held |
| SQL Server | 5,000 | 1 | 0 | 32 ms | 0.084 / 0.515 ms | held |
| SQLite | 200 | 1 | 0 | 24 ms | 0.04 / 0.0536 ms | held |

Highest rate held in every pass: PostgreSQL 10,000/s, every written rate, so the ceiling is above the ladder; SQL Server 5,000/s, every written rate, so the ceiling is above the ladder; SQLite 200/s, every written rate, so the ceiling is above the ladder.

### Enqueue in batches of 100

How fast are batches of 100 jobs acknowledged, before any worker runs?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 100; workers learn of new work: by polling; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Disk write, mean / p95 | Result |
|---|---:|---:|---:|---:|---:|---|
| PostgreSQL | 10,000 | 1 | 0 | 31 ms | 0.0992 / 0.0471 ms | held |
| SQL Server | 5,000 | 1 | 0 | 46 ms | 3.08 / 0.0516 ms | held |
| SQLite | 5,000 | 1 | 0 | 29 ms | 0.0267 / 0.0287 ms | held |

Highest rate held in every pass: PostgreSQL 10,000/s, every written rate, so the ceiling is above the ladder; SQL Server 5,000/s, every written rate, so the ceiling is above the ladder; SQLite 5,000/s, every written rate, so the ceiling is above the ladder.

### Enqueue in batches of 1,000

How fast are batches of 1,000 jobs acknowledged, before any worker runs?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1,000; workers learn of new work: by polling; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Disk write, mean / p95 | Result |
|---|---:|---:|---:|---:|---:|---|
| PostgreSQL | 20,000 | 1 | 0 | 126 ms | 0.602 / 0.109 ms | held |
| SQL Server | 10,000 | 1 | 0 | 202 ms | 0.893 / 0.0454 ms | held |
| SQLite | 10,000 | 1 | 0 | 85 ms | 0.0581 / 0.131 ms | held |

Highest rate held in every pass: PostgreSQL 20,000/s, every written rate, so the ceiling is above the ladder; SQL Server 10,000/s, every written rate, so the ceiling is above the ladder; SQLite 10,000/s, every written rate, so the ceiling is above the ladder.

### Drain, one worker

How fast does one worker run and durably complete a backlog enqueued before it starts?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 8; jobs per call: 1,000.

| Database | Pass | Jobs | Steady rate, database clock | Whole cohort | Disk write, mean / p95 | Result |
|---|---:|---:|---:|---:|---:|---|
| PostgreSQL | 1 | 200,000 | 20,870 jobs/s | 9.74 s | 0.0438 / 0.185 ms | held |
| SQL Server | 1 | 200,000 | 14,243 jobs/s | 14.7 s | 3.43 / 35.9 ms | held |
| SQLite | 1 | 50,000 | 6,545 jobs/s | 7.76 s | 0.0612 / 0.145 ms | held |

### Drain, four workers

How fast do four workers over two namespaces drain a backlog enqueued before they start?

Setup: workers: 4; namespaces: 2; executors per worker: the product default; enqueue calls in flight: 8; jobs per call: 1,000.

| Database | Pass | Jobs | Steady rate, database clock | Whole cohort | Disk write, mean / p95 | Result |
|---|---:|---:|---:|---:|---:|---|
| PostgreSQL | 1 | 200,000 | 42,648 jobs/s | 4.76 s | 0.132 / 0.243 ms | held |
| SQL Server | 1 | 200,000 | 36,810 jobs/s | 5.69 s | 0.135 / 0.694 ms | held |
| SQLite | | | | | | The scenario writes no cohort for sqlite. |

### Drain, sixteen workers

How fast do sixteen workers over two namespaces drain a backlog enqueued before they start?

Setup: workers: 16; namespaces: 2; executors per worker: the product default; enqueue calls in flight: 8; jobs per call: 1,000.

| Database | Pass | Jobs | Steady rate, database clock | Whole cohort | Disk write, mean / p95 | Result |
|---|---:|---:|---:|---:|---:|---|
| PostgreSQL | 1 | 1,000,000 | 74,057 jobs/s | 14.4 s | 5.39 / 21.1 ms | held |
| SQL Server | 1 | 1,000,000 | 50,088 jobs/s | 21.7 s | 0.866 / 0.92 ms | held |
| SQLite | | | | | | The scenario writes no cohort for sqlite. |

### Throughput, sixteen workers

Do sixteen worker processes over two namespaces hold a high offered rate with p99 under 2 s?

Setup: workers: 16; namespaces: 2; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; workers learn of new work: by polling; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Disk write, mean / p95 | Result |
|---|---:|---:|---:|---:|---:|---|
| PostgreSQL | 15,000 | 1 | 15,798 | 1.8 s | 0.744 / 10.5 ms | held |
| SQL Server | 4,000 | 1 | 4,157 | 1.1 s | 0.155 / 6.05 ms | held |
| SQLite | | | | | | The scenario writes no rate for sqlite. |

Highest rate held in every pass: PostgreSQL 15,000/s, every written rate, so the ceiling is above the ladder; SQL Server 4,000/s, every written rate, so the ceiling is above the ladder.

### Throughput, sixteen workers, woken through Redis

With Redis carrying each enqueue's wake, do sixteen worker processes over two namespaces hold a high offered rate with p99 under 2 s?

Setup: workers: 16; namespaces: 2; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; workers learn of new work: through Redis; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Disk write, mean / p95 | Result |
|---|---:|---:|---:|---:|---:|---|
| PostgreSQL | 12,500 | 1 | 12,527 | 120 ms | 0.0704 / 0.411 ms | held |
| SQL Server | 4,000 | 1 | 4,029 | 478 ms | 0.0971 / 0.798 ms | held |
| SQLite | | | | | | SQLite is one process's database; sixteen worker processes are not a SQLite topology. |

Highest rate held in every pass: PostgreSQL 12,500/s, every written rate, so the ceiling is above the ladder; SQL Server 4,000/s, every written rate, so the ceiling is above the ladder.

### Throughput, sixteen workers on one namespace

Do sixteen worker processes sharing one namespace hold a high offered rate with p99 under 2 s?

Setup: workers: 16; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; workers learn of new work: by polling; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Disk write, mean / p95 | Result |
|---|---:|---:|---:|---:|---:|---|
| PostgreSQL | 15,000 | 1 | 16,112 | 2.7 s | 0.0553 / 0.149 ms | missed: p99 2.7 s against a 2.0 s target |
| SQL Server | 4,000 | 1 | 3,984 | 1.2 s | 0.0395 / 0.0924 ms | held |
| SQLite | | | | | | The scenario writes no rate for sqlite. |

Highest rate held in every pass: PostgreSQL none, the floor missed; SQL Server 4,000/s, every written rate, so the ceiling is above the ladder.

Reruns of this scenario on the same commit, in order:

| Database | Rerun | Offered/s | Completed/s | p99 | Disk write, mean / p95 | Result |
|---|---:|---:|---:|---:|---:|---|
| PostgreSQL | 1 | 15,000 | 15,419 | 2.3 s | 0.0376 / 0.0462 ms | missed: p99 2.3 s against a 2.0 s target |
| PostgreSQL | 3 | 15,000 | 15,311 | 2.6 s | 0.0449 / 0.0571 ms | missed: p99 2.6 s against a 2.0 s target |
| PostgreSQL | 4 | 15,000 | 15,586 | 2.0 s | 0.0381 / 0.0434 ms | held |

### Throughput, 32 executors

Does one worker with 32 executors hold the stream its default configuration holds?

Setup: workers: 1; namespaces: 1; executors per worker: 32; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; workers learn of new work: in process; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Disk write, mean / p95 | Result |
|---|---:|---:|---:|---:|---:|---|
| PostgreSQL | 5,000 | 1 | 5,001 | 50 ms | 0.0321 / 0.0357 ms | held |
| SQL Server | 2,500 | 1 | 2,499 | 89 ms | 0.0301 / 0.0809 ms | held |
| SQLite | 160 | 1 | 160 | 90 ms | 0.0347 / 0.0906 ms | held |

Highest rate held in every pass: PostgreSQL 5,000/s, every written rate, so the ceiling is above the ladder; SQL Server 2,500/s, every written rate, so the ceiling is above the ladder; SQLite 160/s, every written rate, so the ceiling is above the ladder.

### Three persisted steps

Do jobs that commit three checkpoints each hold their offered rate with p99 under 5 s?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; workers learn of new work: in process; p99 target: 5.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Disk write, mean / p95 | Result |
|---|---:|---:|---:|---:|---:|---|
| PostgreSQL | 1,000 | 1 | 1,001 | 463 ms | 0.0252 / 0.0407 ms | held |
| SQL Server | 1,000 | 1 | 1,001 | 122 ms | 0.0398 / 1.79 ms | held |
| SQLite | 40 | 1 | 40 | 47 ms | 0.0335 / 0.826 ms | held |

Highest rate held in every pass: PostgreSQL 1,000/s, every written rate, so the ceiling is above the ladder; SQL Server 1,000/s, every written rate, so the ceiling is above the ladder; SQLite 40/s, every written rate, so the ceiling is above the ladder.

### Transient failure and retry

With one job in four failing twice before it succeeds, does the stream hold its rate with p99 under 10 s?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; workers learn of new work: in process; p99 target: 10.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Disk write, mean / p95 | Result |
|---|---:|---:|---:|---:|---:|---|
| PostgreSQL | 5,000 | 1 | 5,004 | 2.1 s | 0.0389 / 0.0713 ms | held |
| SQL Server | 2,500 | 1 | 2,502 | 2.2 s | 0.402 / 3.63 ms | held |
| SQLite | 160 | 1 | 160 | 2.3 s | 0.0351 / 0.0536 ms | held |

Highest rate held in every pass: PostgreSQL 5,000/s, every written rate, so the ceiling is above the ladder; SQL Server 2,500/s, every written rate, so the ceiling is above the ladder; SQLite 160/s, every written rate, so the ceiling is above the ladder.

### Fixed CPU work

Do jobs that each hash for a fixed number of rounds hold their offered rate with p99 under 2 s?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; workers learn of new work: in process; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Disk write, mean / p95 | Result |
|---|---:|---:|---:|---:|---:|---|
| PostgreSQL | 4,000 | 1 | 4,003 | 63 ms | 0.0362 / 0.0387 ms | held |
| SQL Server | 2,500 | 1 | 2,498 | 101 ms | 0.241 / 3.09 ms | held |
| SQLite | 160 | 1 | 160 | 81 ms | 0.0335 / 0.0494 ms | held |

Highest rate held in every pass: PostgreSQL 4,000/s, every written rate, so the ceiling is above the ladder; SQL Server 2,500/s, every written rate, so the ceiling is above the ladder; SQLite 160/s, every written rate, so the ceiling is above the ladder.

### Payloads of 256 B

Do jobs carrying a 256-byte payload hold their offered rate with p99 under 2 s?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; workers learn of new work: in process; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Disk write, mean / p95 | Result |
|---|---:|---:|---:|---:|---:|---|
| PostgreSQL | 4,000 | 1 | 3,999 | 57 ms | 0.0548 / 0.324 ms | held |
| SQL Server | 2,500 | 1 | 2,497 | 93 ms | 0.141 / 1.02 ms | held |
| SQLite | 120 | 1 | 120 | 90 ms | 0.0369 / 0.0539 ms | held |

Highest rate held in every pass: PostgreSQL 4,000/s, every written rate, so the ceiling is above the ladder; SQL Server 2,500/s, every written rate, so the ceiling is above the ladder; SQLite 120/s, every written rate, so the ceiling is above the ladder.

### Payloads of 4 KB

Do jobs carrying a 4 KB payload hold their offered rate with p99 under 2 s?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; workers learn of new work: in process; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Disk write, mean / p95 | Result |
|---|---:|---:|---:|---:|---:|---|
| PostgreSQL | 5,000 | 1 | 4,997 | 52 ms | 0.0362 / 0.0491 ms | held |
| SQL Server | 2,500 | 1 | 2,503 | 98 ms | 0.426 / 6.27 ms | held |
| SQLite | 120 | 1 | 120 | 83 ms | 0.0378 / 0.0681 ms | held |

Highest rate held in every pass: PostgreSQL 5,000/s, every written rate, so the ceiling is above the ladder; SQL Server 2,500/s, every written rate, so the ceiling is above the ladder; SQLite 120/s, every written rate, so the ceiling is above the ladder.

### Payloads of 256 B, 4 KB and 64 KB mixed

Do jobs carrying payloads of three sizes in turn hold their offered rate with p99 under 2 s?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; workers learn of new work: in process; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Disk write, mean / p95 | Result |
|---|---:|---:|---:|---:|---:|---|
| PostgreSQL | 2,500 | 1 | 2,500 | 494 ms | 0.261 / 2.33 ms | held |
| SQL Server | 1,000 | 1 | 1,001 | 123 ms | 0.893 / 11.5 ms | held |
| SQLite | 160 | 1 | 160 | 87 ms | 0.0331 / 0.081 ms | held |

Highest rate held in every pass: PostgreSQL 2,500/s, every written rate, so the ceiling is above the ladder; SQL Server 1,000/s, every written rate, so the ceiling is above the ladder; SQLite 160/s, every written rate, so the ceiling is above the ladder.

### Payloads of 64 KB

Do jobs carrying a 64 KB payload hold their offered rate with p99 under 2 s?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; workers learn of new work: in process; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Disk write, mean / p95 | Result |
|---|---:|---:|---:|---:|---:|---|
| PostgreSQL | 1,000 | 1 | 1,001 | 96 ms | 3.93 / 29.3 ms | held |
| SQL Server | 500 | 1 | 500 | 109 ms | 0.459 / 4.89 ms | held |
| SQLite | 40 | 1 | 40 | 29 ms | 0.0465 / 0.0738 ms | held |

Highest rate held in every pass: PostgreSQL 1,000/s, every written rate, so the ceiling is above the ladder; SQL Server 500/s, every written rate, so the ceiling is above the ladder; SQLite 40/s, every written rate, so the ceiling is above the ladder.

### A thousand lanes

Spread over a thousand ordered lanes, does the stream hold its rate with p99 under 2 s?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; workers learn of new work: in process; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Disk write, mean / p95 | Result |
|---|---:|---:|---:|---:|---:|---|
| PostgreSQL | 4,000 | 1 | 3,998 | 58 ms | 0.0382 / 0.0401 ms | held |
| SQL Server | 1,000 | 1 | 1,000 | 108 ms | 2.02 / 12.7 ms | held |
| SQLite | 120 | 1 | 120 | 91 ms | 0.038 / 0.134 ms | held |

Highest rate held in every pass: PostgreSQL 4,000/s, every written rate, so the ceiling is above the ladder; SQL Server 1,000/s, every written rate, so the ceiling is above the ladder; SQLite 120/s, every written rate, so the ceiling is above the ladder.

### Operator queries during processing

While the stream runs, do the operator list queries answer quickly, and does the stream keep its rate?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; workers learn of new work: in process; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Disk write, mean / p95 | Result |
|---|---:|---:|---:|---:|---:|---|
| PostgreSQL | 5,000 | 1 | 5,001 | 63 ms | 0.0377 / 0.0388 ms | held |
| SQL Server | 2,500 | 1 | 2,498 | 94 ms | 0.0249 / 0.0601 ms | held |
| SQLite | 120 | 1 | 120 | 92 ms | 0.0373 / 0.0531 ms | held |

Highest rate held in every pass: PostgreSQL 5,000/s, every written rate, so the ceiling is above the ladder; SQL Server 2,500/s, every written rate, so the ceiling is above the ladder; SQLite 120/s, every written rate, so the ceiling is above the ladder.

### Parked work alongside

With a large population of jobs parked on signals, does the stream hold its rate with p99 under 2 s?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; workers learn of new work: in process; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Disk write, mean / p95 | Result |
|---|---:|---:|---:|---:|---:|---|
| PostgreSQL | 5,000 | 1 | 5,001 | 55 ms | 0.0562 / 0.305 ms | held |
| SQL Server | 500 | 1 | 501 | 95 ms | 0.033 / 0.0653 ms | held |
| SQLite | 160 | 1 | 160 | 81 ms | 0.0336 / 0.0422 ms | held |

Highest rate held in every pass: PostgreSQL 5,000/s, every written rate, so the ceiling is above the ladder; SQL Server 500/s, every written rate, so the ceiling is above the ladder; SQLite 160/s, every written rate, so the ceiling is above the ladder.

### Future high-priority work alongside

With a large population of high-priority jobs due tomorrow, does the stream hold its rate with p99 under 2 s?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; workers learn of new work: in process; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Disk write, mean / p95 | Result |
|---|---:|---:|---:|---:|---:|---|
| PostgreSQL | 5,000 | 1 | 5,000 | 54 ms | 0.0366 / 0.0439 ms | held |
| SQL Server | 320 | 1 | 320 | 96 ms | 1.2 / 8.66 ms | held |
| SQLite | 160 | 1 | 160 | 91 ms | 0.0341 / 0.0838 ms | held |

Highest rate held in every pass: PostgreSQL 5,000/s, every written rate, so the ceiling is above the ladder; SQL Server 320/s, every written rate, so the ceiling is above the ladder; SQLite 160/s, every written rate, so the ceiling is above the ladder.

### Ten children and a join

Do parents that fan out ten children and join them hold their offered rate with p99 under 10 s?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; workers learn of new work: in process; p99 target: 10.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Disk write, mean / p95 | Result |
|---|---:|---:|---:|---:|---:|---|
| PostgreSQL | 250 | 1 | 250 | 327 ms | 0.0337 / 0.0504 ms | held |
| SQL Server | 200 | 1 | 200 | 334 ms | 0.0513 / 2.95 ms | held |
| SQLite | 8 | 1 | 8 | 149 ms | 0.0314 / 0.0628 ms | held |

Highest rate held in every pass: PostgreSQL 250/s, every written rate, so the ceiling is above the ladder; SQL Server 200/s, every written rate, so the ceiling is above the ladder; SQLite 8/s, every written rate, so the ceiling is above the ladder.

### Controlled I/O through an idempotent sink

Do jobs that each call an external idempotent sink for 5 ms hold their offered rate with p99 under 5 s?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; workers learn of new work: in process; p99 target: 5.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Disk write, mean / p95 | Result |
|---|---:|---:|---:|---:|---:|---|
| PostgreSQL | 250 | 1 | 250 | 235 ms | 0.0249 / 0.0641 ms | held |
| SQL Server | 200 | 1 | 200 | 261 ms | 0.0228 / 0.0313 ms | held |
| SQLite | 80 | 1 | 80.6 | 334 ms | 0.0347 / 0.0474 ms | held |

Highest rate held in every pass: PostgreSQL 250/s, every written rate, so the ceiling is above the ladder; SQL Server 200/s, every written rate, so the ceiling is above the ladder; SQLite 80/s, every written rate, so the ceiling is above the ladder.

### Short and long work mixed

Do no-op, CPU, sink, timer and fan-out jobs mixed in turn hold their offered rate with p99 under 5 s?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; workers learn of new work: in process; p99 target: 5.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Disk write, mean / p95 | Result |
|---|---:|---:|---:|---:|---:|---|
| PostgreSQL | 600 | 1 | 599 | 1.1 s | 0.0303 / 0.037 ms | held |
| SQL Server | 500 | 1 | 499 | 1.1 s | 0.123 / 6.84 ms | held |
| SQLite | 80 | 1 | 80.5 | 1.2 s | 0.0276 / 0.0499 ms | held |

Highest rate held in every pass: PostgreSQL 600/s, every written rate, so the ceiling is above the ladder; SQL Server 500/s, every written rate, so the ceiling is above the ladder; SQLite 80/s, every written rate, so the ceiling is above the ladder.

### Throughput, one executor

Does a worker with a single executor keep up with a modest stream, so the result shows its serial cost?

Setup: workers: 1; namespaces: 1; executors per worker: 1; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; workers learn of new work: in process; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Disk write, mean / p95 | Result |
|---|---:|---:|---:|---:|---:|---|
| PostgreSQL | 150 | 1 | 150 | 30 ms | 0.0222 / 0.0248 ms | held |
| SQL Server | 150 | 1 | 158 | 1.0 s | 0.044 / 0.301 ms | held |
| SQLite | 100 | 1 | 100 | 22 ms | 0.0312 / 0.0489 ms | held |

Highest rate held in every pass: PostgreSQL 150/s, every written rate, so the ceiling is above the ladder; SQL Server 150/s, every written rate, so the ceiling is above the ladder; SQLite 100/s, every written rate, so the ceiling is above the ladder.

### One hot lane

How fast does one ordered lane hand its jobs over, each waiting for the previous completion to commit?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; workers learn of new work: in process; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Disk write, mean / p95 | Result |
|---|---:|---:|---:|---:|---:|---|
| PostgreSQL | 60 | 1 | 60 | 26 ms | 0.0252 / 0.0606 ms | held |
| SQL Server | 100 | 1 | 100 | 25 ms | 0.0265 / 0.0751 ms | held |
| SQLite | 80 | 1 | 80 | 31 ms | 0.0341 / 0.0548 ms | held |

Highest rate held in every pass: PostgreSQL 60/s, every written rate, so the ceiling is above the ladder; SQL Server 100/s, every written rate, so the ceiling is above the ladder; SQLite 80/s, every written rate, so the ceiling is above the ladder.

### One hot concurrency key

With every job sharing one concurrency key of limit one, does a stream of one a second keep a flat backlog with p99 under 30 s?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; workers learn of new work: in process; p99 target: 30.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Disk write, mean / p95 | Result |
|---|---:|---:|---:|---:|---:|---|
| PostgreSQL | 1 | 1 | 1 | 43 ms | 1.38 / 4.82 ms | held |
| SQL Server | 1 | 1 | 1 | 50 ms | 0.277 / 3.67 ms | held |
| SQLite | 4 | 1 | 4 | 34 ms | 0.128 / 0.92 ms | held |

Highest rate held in every pass: PostgreSQL 1/s, every written rate, so the ceiling is above the ladder; SQL Server 1/s, every written rate, so the ceiling is above the ladder; SQLite 4/s, every written rate, so the ceiling is above the ladder.

### Five-minute mixed soak

Does the mixed workload hold a steady rate for five minutes with p99 under 5 s and a flat backlog?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; workers learn of new work: in process; p99 target: 5.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Disk write, mean / p95 | Result |
|---|---:|---:|---:|---:|---:|---|
| PostgreSQL | 700 | 1 | 700 | 1.2 s | 0.103 / 1.16 ms | held |
| SQL Server | 350 | 1 | 350 | 1.1 s | 0.126 / 0.414 ms | held |
| SQLite | 56 | 1 | 56 | 1.1 s | 0.0309 / 0.0365 ms | held |

Highest rate held in every pass: PostgreSQL 700/s, every written rate, so the ceiling is above the ladder; SQL Server 350/s, every written rate, so the ceiling is above the ladder; SQLite 56/s, every written rate, so the ceiling is above the ladder.

### Overload, then recovery

Offered more than it can run, does the backlog grow, and does it fall back once the offered rate drops?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Disk write, mean / p95 | Result |
|---|---:|---:|---:|---:|---:|---|
| PostgreSQL | 13,000, then 2,500 | 1 | 7,749 | 12.7 s | 0.134 / 0.0622 ms | pass: the backlog grew, then fell back |
| SQL Server | 8,000, then 1,000 | 1 | 4,500 | 10.6 s | 0.0407 / 0.163 ms | pass: the backlog grew, then fell back |
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

- **Throughput, sixteen workers on one namespace** on PostgreSQL, fail: Pass 1 missed the floor of 15000/s: p99 2.7 s against a 2.0 s target. Reruns on the same commit: held in 1 of 3, the last one included.

## How this run was made

- Command: `acta-verify release --db all`.
- Commit `892222333a07a59f9282be1300f1e6802303dbf1`, measurement version 3, written standard `d839704c720d`.
- Machine: Intel(R) Core(TM) i9-14900K, 24 cores, 32 logical processors, Microsoft Windows 10.0.26300, .NET 10.0.15.
- Docker VM: 32 CPUs, 46.9 GB; measured containers capped at PostgreSQL 32031 MB, SQL Server 32031 MB.
- PostgreSQL: peak 518 database connections; disk before measuring: quiet before 33 of 33 cells.
- SQL Server: peak 519 database connections; disk before measuring: quiet before 31 of 33 cells.
- SQLite: peak 0 database connections; disk before measuring: quiet before 25 of 25 cells.
- Wall time: performance on PostgreSQL 2 h 15 min, correctness on PostgreSQL 6 min, chaos on PostgreSQL 21 min, performance on SQL Server 2 h 6 min, correctness on SQL Server 10 min, chaos on SQL Server 47 min, performance on SQLite 1 h 20 min, correctness on SQLite 4 min, chaos on SQLite 7 min; correctness runs on every database at once, chaos one database at a time.
- Evidence: 29.6 MB of a 50 MB budget.
- A rate holds when every scheduled arrival reached an outcome and none was refused, the backlog trend stayed within 1% of the rate, p99 stayed within the target, at most 0.1% of arrivals were picked up late by the harness, and every job reconciled. These are the rc.6 criteria.
- p99 runs from an arrival's scheduled instant to its job's durable completion, or to the enqueue call's answer in an enqueue-only scenario. A scenario that streams arrivals to one worker in one namespace runs that worker in the process that enqueues, as a single Acta host does: it shares that process's heap and thread pool and the enqueue calls' connection pool, and each enqueue wakes it at once. A worker process claims again while its claims return jobs; after an empty claim it waits up to its 1 s safety poll, or until Redis wakes it where the setup says so.

| Disk, by phase and drive | Windows counters |
|---|---|
| before \| 0 c: | write 1.43 ms mean, 5.17 ms p95, 13 writes/s, 99% idle, 14 samples |
| before \| 1 d: | write 5.11 ms mean, 7.77 ms p95, 11 writes/s, 99% idle, 14 samples |
| chaos mssql \| 0 c: | write 2.84 ms mean, 7.81 ms p95, 5 writes/s, 99% idle, 1410 samples |
| chaos mssql \| 1 d: | write 0.23 ms mean, 0.77 ms p95, 546 writes/s, 72% idle, 1410 samples |
| chaos pg \| 0 c: | write 2.54 ms mean, 8.07 ms p95, 6 writes/s, 99% idle, 635 samples |
| chaos pg \| 1 d: | write 0.09 ms mean, 0.08 ms p95, 799 writes/s, 52% idle, 635 samples |
| chaos sqlite \| 0 c: | write 2.85 ms mean, 7.80 ms p95, 6 writes/s, 99% idle, 213 samples |
| chaos sqlite \| 1 d: | write 0.87 ms mean, 6.86 ms p95, 1252 writes/s, 59% idle, 213 samples |
| correctness \| 0 c: | write 2.98 ms mean, 7.93 ms p95, 7 writes/s, 99% idle, 320 samples |
| correctness \| 1 d: | write 0.34 ms mean, 1.94 ms p95, 502 writes/s, 83% idle, 320 samples |
| mssql \| 0 c: | write 2.97 ms mean, 8.06 ms p95, 7 writes/s, 99% idle, 3790 samples |
| mssql \| 1 d: | write 1.15 ms mean, 6.40 ms p95, 1458 writes/s, 66% idle, 3790 samples |
| pg \| 0 c: | write 2.85 ms mean, 7.86 ms p95, 8 writes/s, 99% idle, 4041 samples |
| pg \| 1 d: | write 1.06 ms mean, 6.23 ms p95, 701 writes/s, 69% idle, 4041 samples |
| sqlite \| 0 c: | write 1.63 ms mean, 6.35 ms p95, 11 writes/s, 99% idle, 2391 samples |
| sqlite \| 1 d: | write 0.60 ms mean, 5.83 ms p95, 749 writes/s, 74% idle, 2391 samples |
| after \| 0 c: | write 2.65 ms mean, 8.55 ms p95, 3 writes/s, 99% idle, 15 samples |
| after \| 1 d: | write 4.61 ms mean, 7.22 ms p95, 7 writes/s, 100% idle, 15 samples |

- Reruns of the missed floors, on the same commit, in order: `20261117T052040151Z-5d060254006545eba65ea1bb8d9de9b7`, `20261117T052431600Z-96a4fc6950a6475d845f3efe64acb02e`, `20261117T062758334Z-8df7ea14cfaa45f2a92a8675fdc74644`, `20261117T063509252Z-281994d37bd4429fa85e131d5bab0e96`.

