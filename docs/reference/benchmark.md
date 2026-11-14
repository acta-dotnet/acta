# Acta 1.0.0-rc.11: benchmark and release validation

The 1.0.0-rc.11 release evidence, measured by the project on its own machine: one Acta Verify release run on `1bb9ec4e0` on PostgreSQL, SQL Server, and SQLite. Every correctness, fault, and chaos scenario passed, every trial's ledger passed the ledger checks, and every performance floor held in the run; no cell was rerun to pass.

- The run's commit, `1bb9ec4e0`, is in the tagged history. The commits after it change how `acta-verify compare` matches a runtime, with its test and README; the dashboard's floor for a list's name column, which the release run does not exercise; and documentation and the site, so the evidence stands for the tag.
- This is measurement version 3, as in rc.10, and `acta-verify compare` sets the run beside rc.10's, on .NET 10.0.15 against 10.0.14 with the same container images. All 221 scenario runs passed both times; of the measured cells, 21 read faster, 57 the same, and 16 slower, each from one run per release.
- PostgreSQL's four workers held 15,000/s offered in all three 30 s passes, but the 120 s confirmation at 15,000 missed its p99 target, 2.5 s against 2.0 s, and the one at 12,500 held, completing 12,512 jobs a second where rc.10's completed 14,857 at 15,000. The cell sits at this machine's edge. Rerun the same morning, back to back on the same machine and runtime, rc.10's tree held the 15,000 confirmation once (14,996) and missed it once (12,454 at 12,500), and this release's tree missed it twice (12,503 and 12,600 at 12,500). Every confirmation at 15,000 that missed, on either tree, ended 25,000 to 48,000 jobs behind after 125 s, rc.10's furthest.
- The other cells that read slower come from one run each and were not rerun: SQLite's drain (7,940 then 6,946 jobs a second in the comparison) and fourteen p99s at rates both runs held, each inside its floor. Faster, from one run each: PostgreSQL's four-worker drain (37,545 then 46,761) and the one-worker drain on PostgreSQL (16,306 then 19,185) and on SQL Server (11,883 then 13,893).

# Acta Verify: PASS

The release suite on PostgreSQL, SQL Server, SQLite, commit `1bb9ec4e0`, started 2026-11-14 00:45 UTC, 7 h 12 min, on Intel(R) Core(TM) i9-14900K.

| | PostgreSQL | SQL Server | SQLite |
|---|---|---|---|
| Correctness | pass | pass | pass |
| Crash-recovery chaos | pass, 1,000,000 jobs, 72 busy kills reclaimed | pass, 1,000,000 jobs, 81 busy kills reclaimed | pass, 10,000 jobs, 40 busy kills reclaimed |
| Ledger checks | pass, 160 trials checked | pass, 148 trials checked | pass, 125 trials checked |
| Performance floors | held | held | held |
| Drain | 81,391 jobs/s | 46,656 jobs/s | 7,231 jobs/s |
| Throughput | 12,512 jobs/s | 4,986 jobs/s | 250 jobs/s |
| Throughput, one worker | 7,500 jobs/s | 5,000 jobs/s | not written |

> **12,512 fully executed, durably completed jobs per second** on PostgreSQL: 4 workers polling for work, one job per enqueue call, held 3 × 30 s and 120 s at 12,500/s offered, p99 1.0 s, Intel(R) Core(TM) i9-14900K.

> **4,986 fully executed, durably completed jobs per second** on SQL Server: 4 workers polling for work, one job per enqueue call, held 3 × 30 s and 120 s at 5,000/s offered, p99 946 ms, Intel(R) Core(TM) i9-14900K.

> **250 fully executed, durably completed jobs per second** on SQLite: 1 worker, woken in process, one job per enqueue call, held 3 × 30 s and 120 s at 250/s offered, p99 102 ms, Intel(R) Core(TM) i9-14900K.

> **7,500 fully executed, durably completed jobs per second** on PostgreSQL: 1 worker, woken in process, one job per enqueue call, held 3 × 30 s and 120 s at 7,500/s offered, p99 50 ms, Intel(R) Core(TM) i9-14900K.

> **5,000 fully executed, durably completed jobs per second** on SQL Server: 1 worker, woken in process, one job per enqueue call, held 3 × 30 s and 120 s at 5,000/s offered, p99 714 ms, Intel(R) Core(TM) i9-14900K.

## Rates to raise

These written rates ask less than the database ran; they decide nothing in this run.

- **Enqueue in batches of 100** on PostgreSQL: the floor of 10,000/s also held at 40,000/s.
- **Enqueue in batches of 1,000** on PostgreSQL: the floor of 20,000/s also held at 80,000/s.
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
| PostgreSQL | 1 | 1,000,000 | 81,391 jobs/s | 13.6 s | 1.72 / 11.7 ms | held |
| PostgreSQL | 2 | 1,000,000 | 70,299 jobs/s | 15.1 s | 5.79 / 24.9 ms | held |
| PostgreSQL | 3 | 1,000,000 | 86,766 jobs/s | 12.3 s | 5.43 / 33.7 ms | held |
| SQL Server | 1 | 1,000,000 | 33,587 jobs/s | 29.2 s | 0.317 / 0.29 ms | held |
| SQL Server | 2 | 1,000,000 | 46,656 jobs/s | 23.7 s | 1.42 / 1.24 ms | held |
| SQL Server | 3 | 1,000,000 | 50,541 jobs/s | 20.8 s | 2.97 / 0.335 ms | held |
| SQLite | 1 | 100,000 | 8,336 jobs/s | 12.1 s | 0.0382 / 0.199 ms | held |
| SQLite | 2 | 100,000 | 7,231 jobs/s | 14 s | 0.0425 / 0.159 ms | held |
| SQLite | 3 | 100,000 | 6,698 jobs/s | 15.5 s | 0.0425 / 0.0612 ms | held |

### Sustained throughput

How many jobs per second, one per enqueue call, does Acta enqueue, run and durably complete with a flat backlog and p99 under 2 s?

Setup: workers: 4 on PostgreSQL, 4 on SQL Server, 1 on SQLite; namespaces: 2 on PostgreSQL, 2 on SQL Server, 1 on SQLite; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; workers learn of new work: by polling on PostgreSQL, by polling on SQL Server, in process on SQLite; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Disk write, mean / p95 | Result |
|---|---:|---:|---:|---:|---:|---|
| PostgreSQL | 10,000 | 1 | 10,193 | 1.0 s | 0.0367 / 0.0409 ms | held |
| PostgreSQL | 10,000 | 2 | 9,836 | 1.0 s | 0.0806 / 0.634 ms | held |
| PostgreSQL | 10,000 | 3 | 10,121 | 1.0 s | 0.0502 / 0.226 ms | held |
| PostgreSQL | 12,500 | 1 | 12,423 | 1.0 s | 0.138 / 2.78 ms | held |
| PostgreSQL | 12,500 | 2 | 12,385 | 1.0 s | 0.0381 / 0.041 ms | held |
| PostgreSQL | 12,500 | 3 | 12,442 | 1.0 s | 0.0382 / 0.0399 ms | held |
| PostgreSQL | 15,000 | 1 | 14,993 | 1.0 s | 0.547 / 10 ms | held |
| PostgreSQL | 15,000 | 2 | 15,298 | 926 ms | 0.0509 / 0.0606 ms | held |
| PostgreSQL | 15,000 | 3 | 15,197 | 921 ms | 0.0514 / 0.0614 ms | held |
| PostgreSQL | 17,500 | 1 | 16,820 | 1.9 s | 0.541 / 7.18 ms | missed: backlog grew 642 jobs/s, 175 allowed |
| PostgreSQL | 17,500 | 2 | 17,689 | 1.0 s | 0.393 / 5.03 ms | held |
| PostgreSQL | 17,500 | 3 | 16,636 | 2.4 s | 0.374 / 4.8 ms | missed: backlog grew 908 jobs/s, 175 allowed |
| PostgreSQL | 20,000 | 2 | 17,959 | 4.6 s | 0.121 / 0.0654 ms | missed: backlog grew 2,189 jobs/s, 200 allowed |
| PostgreSQL | 25,000 | | | | | not run by rule |
| PostgreSQL | 30,000 | | | | | not run by rule |
| PostgreSQL | 15,000 | confirm, 120 s | 14,706 | 2.5 s | 0.0902 / 0.396 ms | missed: p99 2.5 s against a 2.0 s target |
| PostgreSQL | 12,500 | confirm, 120 s | 12,512 | 1.0 s | 0.0475 / 0.0558 ms | held |
| SQL Server | 5,000 | 1 | 5,001 | 926 ms | 0.849 / 28.9 ms | held |
| SQL Server | 5,000 | 2 | 5,013 | 891 ms | 0.07 / 0.528 ms | held |
| SQL Server | 5,000 | 3 | 4,998 | 911 ms | 0.119 / 4.54 ms | held |
| SQL Server | 7,500 | 1 | 6,734 | 4.4 s | 0.11 / 1.16 ms | missed: backlog grew 731 jobs/s, 75 allowed |
| SQL Server | 7,500 | 2 | 6,632 | 5.0 s | 0.188 / 0.763 ms | missed: backlog grew 899 jobs/s, 75 allowed |
| SQL Server | 7,500 | 3 | 6,812 | 4.2 s | 0.0994 / 0.563 ms | missed: backlog grew 711 jobs/s, 75 allowed |
| SQL Server | 10,000 | | | | | not run by rule |
| SQL Server | 12,500 | | | | | not run by rule |
| SQL Server | 15,000 | | | | | not run by rule |
| SQL Server | 5,000 | confirm, 120 s | 4,986 | 946 ms | 0.174 / 0.989 ms | held |
| SQLite | 250 | 1 | 250 | 100 ms | 0.0294 / 0.0308 ms | held |
| SQLite | 250 | 2 | 251 | 100 ms | 0.0301 / 0.0308 ms | held |
| SQLite | 250 | 3 | 250 | 101 ms | 0.0299 / 0.0324 ms | held |
| SQLite | 500 | 1 | 499 | 88 ms | 0.0237 / 0.0289 ms | held |
| SQLite | 500 | 2 | 345 | 14.7 s | 0.0283 / 0.0306 ms | missed: backlog grew 152 jobs/s, 5 allowed |
| SQLite | 500 | 3 | 342 | 14.5 s | 0.0287 / 0.0301 ms | missed: backlog grew 155 jobs/s, 5 allowed |
| SQLite | 1,000 | 1 | 791 | 17.7 s | 0.0242 / 0.0301 ms | missed: backlog grew 150 jobs/s, 10 allowed |
| SQLite | 2,000 | | | | | not run by rule |
| SQLite | 250 | confirm, 120 s | 250 | 102 ms | 0.0297 / 0.0306 ms | held |

Highest rate held in every pass: PostgreSQL 15,000/s; SQL Server 5,000/s; SQLite 250/s.

### Sustained throughput, woken through Redis

With Redis carrying each enqueue's wake to the worker processes, how many jobs per second, one per enqueue call, does Acta enqueue, run and durably complete with a flat backlog and p99 under 2 s?

Setup: workers: 4 on PostgreSQL, 4 on SQL Server, 1 on SQLite; namespaces: 2 on PostgreSQL, 2 on SQL Server, 1 on SQLite; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; workers learn of new work: through Redis on PostgreSQL, through Redis on SQL Server, in process on SQLite; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Disk write, mean / p95 | Result |
|---|---:|---:|---:|---:|---:|---|
| PostgreSQL | 10,000 | 1 | 9,995 | 102 ms | 0.0412 / 0.0839 ms | held |
| PostgreSQL | 12,500 | 1 | 12,504 | 166 ms | 0.054 / 0.316 ms | held |
| PostgreSQL | 15,000 | 1 | 15,008 | 107 ms | 0.22 / 2.99 ms | held |
| PostgreSQL | 17,500 | 1 | 16,235 | 3.1 s | 0.664 / 8.99 ms | missed: backlog grew 1,254 jobs/s, 175 allowed |
| PostgreSQL | 20,000 | | | | | not run by rule |
| PostgreSQL | 25,000 | | | | | not run by rule |
| PostgreSQL | 30,000 | | | | | not run by rule |
| SQL Server | 5,000 | 1 | 5,002 | 148 ms | 0.0294 / 0.0555 ms | held |
| SQL Server | 7,500 | 1 | 6,695 | 4.9 s | 0.0808 / 0.521 ms | missed: backlog grew 819 jobs/s, 75 allowed |
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
| PostgreSQL | 5,000 | 1 | 5,001 | 55 ms | 0.0364 / 0.0377 ms | held |
| PostgreSQL | 5,000 | 2 | 5,001 | 56 ms | 0.0368 / 0.0384 ms | held |
| PostgreSQL | 5,000 | 3 | 5,001 | 54 ms | 0.0382 / 0.0544 ms | held |
| PostgreSQL | 7,500 | 1 | 7,497 | 41 ms | 0.0331 / 0.0335 ms | held |
| PostgreSQL | 7,500 | 2 | 7,497 | 40 ms | 0.0342 / 0.0397 ms | held |
| PostgreSQL | 7,500 | 3 | 7,500 | 89 ms | 0.0342 / 0.0456 ms | held |
| PostgreSQL | 10,000 | 1 | 9,318 | 2.1 s | 0.0411 / 0.0527 ms | missed: backlog grew 750 jobs/s, 100 allowed |
| PostgreSQL | 10,000 | 2 | 9,472 | 1.6 s | 0.0441 / 0.0474 ms | missed: backlog grew 472 jobs/s, 100 allowed |
| PostgreSQL | 10,000 | 3 | 10,001 | 55 ms | 0.0351 / 0.0356 ms | held |
| PostgreSQL | 12,500 | 3 | 8,704 | 9.7 s | 0.188 / 0.0503 ms | missed: backlog grew 4,194 jobs/s, 125 allowed |
| PostgreSQL | 15,000 | | | | | not run by rule |
| PostgreSQL | 7,500 | confirm, 120 s | 7,500 | 50 ms | 0.0556 / 0.113 ms | held |
| SQL Server | 2,500 | 1 | 2,502 | 97 ms | 0.195 / 6.26 ms | held |
| SQL Server | 2,500 | 2 | 2,500 | 104 ms | 0.605 / 5.24 ms | held |
| SQL Server | 2,500 | 3 | 2,499 | 94 ms | 0.243 / 10.1 ms | held |
| SQL Server | 5,000 | 1 | 4,996 | 489 ms | 0.0279 / 0.0507 ms | held |
| SQL Server | 5,000 | 2 | 5,018 | 138 ms | 0.317 / 9.38 ms | held |
| SQL Server | 5,000 | 3 | 4,992 | 150 ms | 0.34 / 3.68 ms | held |
| SQL Server | 7,500 | 1 | 6,362 | 7.2 s | 0.207 / 0.762 ms | missed: backlog grew 1,021 jobs/s, 75 allowed |
| SQL Server | 7,500 | 2 | 6,333 | 7.7 s | 0.183 / 0.614 ms | missed: backlog grew 1,038 jobs/s, 75 allowed |
| SQL Server | 7,500 | 3 | 6,242 | 7.7 s | 0.148 / 0.541 ms | missed: backlog grew 1,155 jobs/s, 75 allowed |
| SQL Server | 10,000 | | | | | not run by rule |
| SQL Server | 5,000 | confirm, 120 s | 5,000 | 714 ms | 0.208 / 0.614 ms | held |
| SQLite | | | | | | The scenario writes no rate for sqlite. |

Highest rate held in every pass: PostgreSQL 7,500/s; SQL Server 5,000/s.

### Enqueue, one job per call

How fast are single-job enqueue calls acknowledged, each in its own transaction, before any worker runs?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; workers learn of new work: by polling; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Disk write, mean / p95 | Result |
|---|---:|---:|---:|---:|---:|---|
| PostgreSQL | 10,000 | 1 | 0 | 26 ms | 0.0667 / 0.0471 ms | held |
| SQL Server | 5,000 | 1 | 0 | 29 ms | 0.138 / 1.12 ms | held |
| SQLite | 200 | 1 | 0 | 23 ms | 0.0413 / 0.0755 ms | held |

Highest rate held in every pass: PostgreSQL 10,000/s, every written rate, so the ceiling is above the ladder; SQL Server 5,000/s, every written rate, so the ceiling is above the ladder; SQLite 200/s, every written rate, so the ceiling is above the ladder.

### Enqueue in batches of 100

How fast are batches of 100 jobs acknowledged, before any worker runs?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 100; workers learn of new work: by polling; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Disk write, mean / p95 | Result |
|---|---:|---:|---:|---:|---:|---|
| PostgreSQL | 10,000 | 1 | 0 | 38 ms | 0.116 / 0.0377 ms | held |
| SQL Server | 5,000 | 1 | 0 | 45 ms | 0.0508 / 0.093 ms | held |
| SQLite | 5,000 | 1 | 0 | 30 ms | 0.0334 / 0.0431 ms | held |

Highest rate held in every pass: PostgreSQL 10,000/s, every written rate, so the ceiling is above the ladder; SQL Server 5,000/s, every written rate, so the ceiling is above the ladder; SQLite 5,000/s, every written rate, so the ceiling is above the ladder.

### Enqueue in batches of 1,000

How fast are batches of 1,000 jobs acknowledged, before any worker runs?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1,000; workers learn of new work: by polling; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Disk write, mean / p95 | Result |
|---|---:|---:|---:|---:|---:|---|
| PostgreSQL | 20,000 | 1 | 0 | 158 ms | 0.0556 / 0.113 ms | held |
| SQL Server | 10,000 | 1 | 0 | 142 ms | 1.88 / 0.0826 ms | held |
| SQLite | 10,000 | 1 | 0 | 81 ms | 0.0592 / 0.139 ms | held |

Highest rate held in every pass: PostgreSQL 20,000/s, every written rate, so the ceiling is above the ladder; SQL Server 10,000/s, every written rate, so the ceiling is above the ladder; SQLite 10,000/s, every written rate, so the ceiling is above the ladder.

### Drain, one worker

How fast does one worker run and durably complete a backlog enqueued before it starts?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 8; jobs per call: 1,000.

| Database | Pass | Jobs | Steady rate, database clock | Whole cohort | Disk write, mean / p95 | Result |
|---|---:|---:|---:|---:|---:|---|
| PostgreSQL | 1 | 200,000 | 20,970 jobs/s | 9.74 s | 0.0532 / 0.194 ms | held |
| SQL Server | 1 | 200,000 | 15,014 jobs/s | 13.6 s | 0.0706 / 0.363 ms | held |
| SQLite | 1 | 50,000 | 6,559 jobs/s | 7.83 s | 0.0507 / 0.147 ms | held |

### Drain, four workers

How fast do four workers over two namespaces drain a backlog enqueued before they start?

Setup: workers: 4; namespaces: 2; executors per worker: the product default; enqueue calls in flight: 8; jobs per call: 1,000.

| Database | Pass | Jobs | Steady rate, database clock | Whole cohort | Disk write, mean / p95 | Result |
|---|---:|---:|---:|---:|---:|---|
| PostgreSQL | 1 | 200,000 | 58,076 jobs/s | 3.59 s | 0.0916 / 0.262 ms | held |
| SQL Server | 1 | 200,000 | 38,898 jobs/s | 5.1 s | 0.0341 / 0.0551 ms | held |
| SQLite | | | | | | The scenario writes no cohort for sqlite. |

### Drain, sixteen workers

How fast do sixteen workers over two namespaces drain a backlog enqueued before they start?

Setup: workers: 16; namespaces: 2; executors per worker: the product default; enqueue calls in flight: 8; jobs per call: 1,000.

| Database | Pass | Jobs | Steady rate, database clock | Whole cohort | Disk write, mean / p95 | Result |
|---|---:|---:|---:|---:|---:|---|
| PostgreSQL | 1 | 1,000,000 | 73,062 jobs/s | 14.5 s | 1.19 / 6.78 ms | held |
| SQL Server | 1 | 1,000,000 | 45,378 jobs/s | 22.7 s | 1.01 / 0.799 ms | held |
| SQLite | | | | | | The scenario writes no cohort for sqlite. |

### Throughput, sixteen workers

Do sixteen worker processes over two namespaces hold a high offered rate with p99 under 2 s?

Setup: workers: 16; namespaces: 2; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; workers learn of new work: by polling; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Disk write, mean / p95 | Result |
|---|---:|---:|---:|---:|---:|---|
| PostgreSQL | 15,000 | 1 | 15,464 | 1.5 s | 0.142 / 1.04 ms | held |
| SQL Server | 4,000 | 1 | 3,967 | 868 ms | 0.073 / 2.45 ms | held |
| SQLite | | | | | | The scenario writes no rate for sqlite. |

Highest rate held in every pass: PostgreSQL 15,000/s, every written rate, so the ceiling is above the ladder; SQL Server 4,000/s, every written rate, so the ceiling is above the ladder.

### Throughput, sixteen workers, woken through Redis

With Redis carrying each enqueue's wake, do sixteen worker processes over two namespaces hold a high offered rate with p99 under 2 s?

Setup: workers: 16; namespaces: 2; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; workers learn of new work: through Redis; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Disk write, mean / p95 | Result |
|---|---:|---:|---:|---:|---:|---|
| PostgreSQL | 12,500 | 1 | 12,987 | 1.3 s | 0.0447 / 0.0796 ms | held |
| SQL Server | 4,000 | 1 | 4,017 | 355 ms | 0.0428 / 0.173 ms | held |
| SQLite | | | | | | SQLite is one process's database; sixteen worker processes are not a SQLite topology. |

Highest rate held in every pass: PostgreSQL 12,500/s, every written rate, so the ceiling is above the ladder; SQL Server 4,000/s, every written rate, so the ceiling is above the ladder.

### Throughput, sixteen workers on one namespace

Do sixteen worker processes sharing one namespace hold a high offered rate with p99 under 2 s?

Setup: workers: 16; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; workers learn of new work: by polling; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Disk write, mean / p95 | Result |
|---|---:|---:|---:|---:|---:|---|
| PostgreSQL | 15,000 | 1 | 16,077 | 1.7 s | 0.0425 / 0.0684 ms | held |
| SQL Server | 4,000 | 1 | 4,222 | 1.6 s | 0.336 / 2.95 ms | held |
| SQLite | | | | | | The scenario writes no rate for sqlite. |

Highest rate held in every pass: PostgreSQL 15,000/s, every written rate, so the ceiling is above the ladder; SQL Server 4,000/s, every written rate, so the ceiling is above the ladder.

### Throughput, 32 executors

Does one worker with 32 executors hold the stream its default configuration holds?

Setup: workers: 1; namespaces: 1; executors per worker: 32; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; workers learn of new work: in process; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Disk write, mean / p95 | Result |
|---|---:|---:|---:|---:|---:|---|
| PostgreSQL | 5,000 | 1 | 5,001 | 50 ms | 0.032 / 0.0359 ms | held |
| SQL Server | 2,500 | 1 | 2,500 | 92 ms | 0.0421 / 0.824 ms | held |
| SQLite | 160 | 1 | 160 | 96 ms | 0.0346 / 0.0504 ms | held |

Highest rate held in every pass: PostgreSQL 5,000/s, every written rate, so the ceiling is above the ladder; SQL Server 2,500/s, every written rate, so the ceiling is above the ladder; SQLite 160/s, every written rate, so the ceiling is above the ladder.

### Three persisted steps

Do jobs that commit three checkpoints each hold their offered rate with p99 under 5 s?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; workers learn of new work: in process; p99 target: 5.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Disk write, mean / p95 | Result |
|---|---:|---:|---:|---:|---:|---|
| PostgreSQL | 1,000 | 1 | 1,000 | 102 ms | 0.0248 / 0.0412 ms | held |
| SQL Server | 1,000 | 1 | 1,001 | 118 ms | 0.0243 / 0.0317 ms | held |
| SQLite | 40 | 1 | 40 | 50 ms | 0.0342 / 0.0476 ms | held |

Highest rate held in every pass: PostgreSQL 1,000/s, every written rate, so the ceiling is above the ladder; SQL Server 1,000/s, every written rate, so the ceiling is above the ladder; SQLite 40/s, every written rate, so the ceiling is above the ladder.

### Transient failure and retry

With one job in four failing twice before it succeeds, does the stream hold its rate with p99 under 10 s?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; workers learn of new work: in process; p99 target: 10.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Disk write, mean / p95 | Result |
|---|---:|---:|---:|---:|---:|---|
| PostgreSQL | 5,000 | 1 | 4,998 | 2.1 s | 0.0347 / 0.0547 ms | held |
| SQL Server | 2,500 | 1 | 2,498 | 2.2 s | 0.207 / 1.66 ms | held |
| SQLite | 160 | 1 | 160 | 2.2 s | 0.0336 / 0.0656 ms | held |

Highest rate held in every pass: PostgreSQL 5,000/s, every written rate, so the ceiling is above the ladder; SQL Server 2,500/s, every written rate, so the ceiling is above the ladder; SQLite 160/s, every written rate, so the ceiling is above the ladder.

### Fixed CPU work

Do jobs that each hash for a fixed number of rounds hold their offered rate with p99 under 2 s?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; workers learn of new work: in process; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Disk write, mean / p95 | Result |
|---|---:|---:|---:|---:|---:|---|
| PostgreSQL | 4,000 | 1 | 4,000 | 280 ms | 0.0542 / 0.275 ms | held |
| SQL Server | 2,500 | 1 | 2,497 | 96 ms | 0.17 / 2.54 ms | held |
| SQLite | 160 | 1 | 160 | 81 ms | 0.0316 / 0.0329 ms | held |

Highest rate held in every pass: PostgreSQL 4,000/s, every written rate, so the ceiling is above the ladder; SQL Server 2,500/s, every written rate, so the ceiling is above the ladder; SQLite 160/s, every written rate, so the ceiling is above the ladder.

### Payloads of 256 B

Do jobs carrying a 256-byte payload hold their offered rate with p99 under 2 s?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; workers learn of new work: in process; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Disk write, mean / p95 | Result |
|---|---:|---:|---:|---:|---:|---|
| PostgreSQL | 4,000 | 1 | 4,003 | 58 ms | 0.0678 / 0.594 ms | held |
| SQL Server | 2,500 | 1 | 2,503 | 85 ms | 0.0435 / 0.262 ms | held |
| SQLite | 120 | 1 | 120 | 99 ms | 0.0366 / 0.75 ms | held |

Highest rate held in every pass: PostgreSQL 4,000/s, every written rate, so the ceiling is above the ladder; SQL Server 2,500/s, every written rate, so the ceiling is above the ladder; SQLite 120/s, every written rate, so the ceiling is above the ladder.

### Payloads of 4 KB

Do jobs carrying a 4 KB payload hold their offered rate with p99 under 2 s?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; workers learn of new work: in process; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Disk write, mean / p95 | Result |
|---|---:|---:|---:|---:|---:|---|
| PostgreSQL | 5,000 | 1 | 4,996 | 55 ms | 0.0463 / 0.0489 ms | held |
| SQL Server | 2,500 | 1 | 2,501 | 88 ms | 0.0968 / 0.888 ms | held |
| SQLite | 120 | 1 | 120 | 82 ms | 0.0361 / 0.092 ms | held |

Highest rate held in every pass: PostgreSQL 5,000/s, every written rate, so the ceiling is above the ladder; SQL Server 2,500/s, every written rate, so the ceiling is above the ladder; SQLite 120/s, every written rate, so the ceiling is above the ladder.

### Payloads of 256 B, 4 KB and 64 KB mixed

Do jobs carrying payloads of three sizes in turn hold their offered rate with p99 under 2 s?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; workers learn of new work: in process; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Disk write, mean / p95 | Result |
|---|---:|---:|---:|---:|---:|---|
| PostgreSQL | 2,500 | 1 | 2,497 | 519 ms | 0.0661 / 0.331 ms | held |
| SQL Server | 1,000 | 1 | 999 | 119 ms | 0.301 / 4.16 ms | held |
| SQLite | 160 | 1 | 160 | 80 ms | 0.0337 / 0.0512 ms | held |

Highest rate held in every pass: PostgreSQL 2,500/s, every written rate, so the ceiling is above the ladder; SQL Server 1,000/s, every written rate, so the ceiling is above the ladder; SQLite 160/s, every written rate, so the ceiling is above the ladder.

### Payloads of 64 KB

Do jobs carrying a 64 KB payload hold their offered rate with p99 under 2 s?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; workers learn of new work: in process; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Disk write, mean / p95 | Result |
|---|---:|---:|---:|---:|---:|---|
| PostgreSQL | 1,000 | 1 | 999 | 92 ms | 0.148 / 1.3 ms | held |
| SQL Server | 500 | 1 | 500 | 101 ms | 0.0474 / 0.25 ms | held |
| SQLite | 40 | 1 | 40 | 28 ms | 0.0452 / 0.11 ms | held |

Highest rate held in every pass: PostgreSQL 1,000/s, every written rate, so the ceiling is above the ladder; SQL Server 500/s, every written rate, so the ceiling is above the ladder; SQLite 40/s, every written rate, so the ceiling is above the ladder.

### A thousand lanes

Spread over a thousand ordered lanes, does the stream hold its rate with p99 under 2 s?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; workers learn of new work: in process; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Disk write, mean / p95 | Result |
|---|---:|---:|---:|---:|---:|---|
| PostgreSQL | 4,000 | 1 | 3,998 | 59 ms | 0.0389 / 0.0405 ms | held |
| SQL Server | 1,000 | 1 | 1,000 | 100 ms | 0.714 / 7.31 ms | held |
| SQLite | 120 | 1 | 120 | 85 ms | 0.0372 / 0.0599 ms | held |

Highest rate held in every pass: PostgreSQL 4,000/s, every written rate, so the ceiling is above the ladder; SQL Server 1,000/s, every written rate, so the ceiling is above the ladder; SQLite 120/s, every written rate, so the ceiling is above the ladder.

### Operator queries during processing

While the stream runs, do the operator list queries answer quickly, and does the stream keep its rate?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; workers learn of new work: in process; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Disk write, mean / p95 | Result |
|---|---:|---:|---:|---:|---:|---|
| PostgreSQL | 5,000 | 1 | 5,000 | 56 ms | 0.166 / 2.05 ms | held |
| SQL Server | 2,500 | 1 | 2,502 | 94 ms | 0.0282 / 0.0397 ms | held |
| SQLite | 120 | 1 | 120 | 85 ms | 0.0364 / 0.064 ms | held |

Highest rate held in every pass: PostgreSQL 5,000/s, every written rate, so the ceiling is above the ladder; SQL Server 2,500/s, every written rate, so the ceiling is above the ladder; SQLite 120/s, every written rate, so the ceiling is above the ladder.

### Parked work alongside

With a large population of jobs parked on signals, does the stream hold its rate with p99 under 2 s?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; workers learn of new work: in process; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Disk write, mean / p95 | Result |
|---|---:|---:|---:|---:|---:|---|
| PostgreSQL | 5,000 | 1 | 5,000 | 56 ms | 0.511 / 3.2 ms | held |
| SQL Server | 500 | 1 | 499 | 96 ms | 0.774 / 9.28 ms | held |
| SQLite | 160 | 1 | 160 | 80 ms | 0.0331 / 0.0544 ms | held |

Highest rate held in every pass: PostgreSQL 5,000/s, every written rate, so the ceiling is above the ladder; SQL Server 500/s, every written rate, so the ceiling is above the ladder; SQLite 160/s, every written rate, so the ceiling is above the ladder.

### Future high-priority work alongside

With a large population of high-priority jobs due tomorrow, does the stream hold its rate with p99 under 2 s?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; workers learn of new work: in process; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Disk write, mean / p95 | Result |
|---|---:|---:|---:|---:|---:|---|
| PostgreSQL | 5,000 | 1 | 4,997 | 56 ms | 0.0386 / 0.0522 ms | held |
| SQL Server | 320 | 1 | 320 | 104 ms | 1.69 / 14.8 ms | held |
| SQLite | 160 | 1 | 160 | 84 ms | 0.0341 / 0.566 ms | held |

Highest rate held in every pass: PostgreSQL 5,000/s, every written rate, so the ceiling is above the ladder; SQL Server 320/s, every written rate, so the ceiling is above the ladder; SQLite 160/s, every written rate, so the ceiling is above the ladder.

### Ten children and a join

Do parents that fan out ten children and join them hold their offered rate with p99 under 10 s?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; workers learn of new work: in process; p99 target: 10.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Disk write, mean / p95 | Result |
|---|---:|---:|---:|---:|---:|---|
| PostgreSQL | 250 | 1 | 250 | 326 ms | 0.239 / 3.36 ms | held |
| SQL Server | 200 | 1 | 200 | 344 ms | 0.0916 / 0.78 ms | held |
| SQLite | 8 | 1 | 8 | 209 ms | 0.0307 / 0.0696 ms | held |

Highest rate held in every pass: PostgreSQL 250/s, every written rate, so the ceiling is above the ladder; SQL Server 200/s, every written rate, so the ceiling is above the ladder; SQLite 8/s, every written rate, so the ceiling is above the ladder.

### Controlled I/O through an idempotent sink

Do jobs that each call an external idempotent sink for 5 ms hold their offered rate with p99 under 5 s?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; workers learn of new work: in process; p99 target: 5.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Disk write, mean / p95 | Result |
|---|---:|---:|---:|---:|---:|---|
| PostgreSQL | 250 | 1 | 250 | 172 ms | 0.0219 / 0.0349 ms | held |
| SQL Server | 200 | 1 | 200 | 265 ms | 0.0229 / 0.0383 ms | held |
| SQLite | 80 | 1 | 80.2 | 367 ms | 0.0351 / 0.0774 ms | held |

Highest rate held in every pass: PostgreSQL 250/s, every written rate, so the ceiling is above the ladder; SQL Server 200/s, every written rate, so the ceiling is above the ladder; SQLite 80/s, every written rate, so the ceiling is above the ladder.

### Short and long work mixed

Do no-op, CPU, sink, timer and fan-out jobs mixed in turn hold their offered rate with p99 under 5 s?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; workers learn of new work: in process; p99 target: 5.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Disk write, mean / p95 | Result |
|---|---:|---:|---:|---:|---:|---|
| PostgreSQL | 600 | 1 | 600 | 1.1 s | 0.0252 / 0.0624 ms | held |
| SQL Server | 500 | 1 | 500 | 1.1 s | 0.0755 / 3.93 ms | held |
| SQLite | 80 | 1 | 80 | 1.2 s | 0.0366 / 0.058 ms | held |

Highest rate held in every pass: PostgreSQL 600/s, every written rate, so the ceiling is above the ladder; SQL Server 500/s, every written rate, so the ceiling is above the ladder; SQLite 80/s, every written rate, so the ceiling is above the ladder.

### Throughput, one executor

Does a worker with a single executor keep up with a modest stream, so the result shows its serial cost?

Setup: workers: 1; namespaces: 1; executors per worker: 1; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; workers learn of new work: in process; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Disk write, mean / p95 | Result |
|---|---:|---:|---:|---:|---:|---|
| PostgreSQL | 150 | 1 | 150 | 32 ms | 0.0231 / 0.0418 ms | held |
| SQL Server | 150 | 1 | 150 | 61 ms | 0.0222 / 0.0227 ms | held |
| SQLite | 100 | 1 | 100 | 30 ms | 0.0327 / 0.076 ms | held |

Highest rate held in every pass: PostgreSQL 150/s, every written rate, so the ceiling is above the ladder; SQL Server 150/s, every written rate, so the ceiling is above the ladder; SQLite 100/s, every written rate, so the ceiling is above the ladder.

### One hot lane

How fast does one ordered lane hand its jobs over, each waiting for the previous completion to commit?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; workers learn of new work: in process; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Disk write, mean / p95 | Result |
|---|---:|---:|---:|---:|---:|---|
| PostgreSQL | 60 | 1 | 60 | 25 ms | 0.0233 / 0.0243 ms | held |
| SQL Server | 100 | 1 | 100 | 94 ms | 0.32 / 3.27 ms | held |
| SQLite | 80 | 1 | 80 | 33 ms | 0.0376 / 0.354 ms | held |

Highest rate held in every pass: PostgreSQL 60/s, every written rate, so the ceiling is above the ladder; SQL Server 100/s, every written rate, so the ceiling is above the ladder; SQLite 80/s, every written rate, so the ceiling is above the ladder.

### One hot concurrency key

With every job sharing one concurrency key of limit one, does a stream of one a second keep a flat backlog with p99 under 30 s?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; workers learn of new work: in process; p99 target: 30.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Disk write, mean / p95 | Result |
|---|---:|---:|---:|---:|---:|---|
| PostgreSQL | 1 | 1 | 1 | 47 ms | 0.994 / 5.04 ms | held |
| SQL Server | 1 | 1 | 1 | 42 ms | 0.444 / 2.99 ms | held |
| SQLite | 4 | 1 | 4 | 25 ms | 0.123 / 0.893 ms | held |

Highest rate held in every pass: PostgreSQL 1/s, every written rate, so the ceiling is above the ladder; SQL Server 1/s, every written rate, so the ceiling is above the ladder; SQLite 4/s, every written rate, so the ceiling is above the ladder.

### Five-minute mixed soak

Does the mixed workload hold a steady rate for five minutes with p99 under 5 s and a flat backlog?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; workers learn of new work: in process; p99 target: 5.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Disk write, mean / p95 | Result |
|---|---:|---:|---:|---:|---:|---|
| PostgreSQL | 700 | 1 | 700 | 1.2 s | 0.0482 / 0.285 ms | held |
| SQL Server | 350 | 1 | 350 | 1.1 s | 0.0894 / 0.44 ms | held |
| SQLite | 56 | 1 | 56 | 1.1 s | 0.0335 / 0.0409 ms | held |

Highest rate held in every pass: PostgreSQL 700/s, every written rate, so the ceiling is above the ladder; SQL Server 350/s, every written rate, so the ceiling is above the ladder; SQLite 56/s, every written rate, so the ceiling is above the ladder.

### Overload, then recovery

Offered more than it can run, does the backlog grow, and does it fall back once the offered rate drops?

Setup: workers: 1; namespaces: 1; executors per worker: the product default; enqueue calls in flight: 256 on PostgreSQL, 256 on SQL Server, 8 on SQLite; jobs per call: 1; p99 target: 2.0 s.

| Database | Offered/s | Pass | Completed/s | p99 | Disk write, mean / p95 | Result |
|---|---:|---:|---:|---:|---:|---|
| PostgreSQL | 13,000, then 2,500 | 1 | 7,751 | 11.9 s | 0.0677 / 0.0505 ms | pass: the backlog grew, then fell back |
| SQL Server | 8,000, then 1,000 | 1 | 4,500 | 10.9 s | 0.0981 / 0.125 ms | pass: the backlog grew, then fell back |
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
- Commit `1bb9ec4e0c58582efa596e98325b55f203ab3d4e`, measurement version 3, written standard `d839704c720d`.
- Machine: Intel(R) Core(TM) i9-14900K, 24 cores, 32 logical processors, Microsoft Windows 10.0.26300, .NET 10.0.15.
- Docker VM: 32 CPUs, 46.9 GB; measured containers capped at PostgreSQL 32031 MB, SQL Server 32031 MB.
- PostgreSQL: peak 518 database connections; disk before measuring: quiet before 33 of 33 cells.
- SQL Server: peak 519 database connections; disk before measuring: quiet before 31 of 33 cells.
- SQLite: peak 0 database connections; disk before measuring: quiet before 25 of 25 cells.
- Wall time: performance on PostgreSQL 2 h 15 min, correctness on PostgreSQL 6 min, chaos on PostgreSQL 19 min, performance on SQL Server 2 h 3 min, correctness on SQL Server 10 min, chaos on SQL Server 47 min, performance on SQLite 1 h 20 min, correctness on SQLite 5 min, chaos on SQLite 12 min; correctness runs on every database at once, chaos one database at a time.
- Evidence: 29.9 MB of a 50 MB budget.
- A rate holds when every scheduled arrival reached an outcome and none was refused, the backlog trend stayed within 1% of the rate, p99 stayed within the target, at most 0.1% of arrivals were picked up late by the harness, and every job reconciled. These are the rc.6 criteria.
- p99 runs from an arrival's scheduled instant to its job's durable completion, or to the enqueue call's answer in an enqueue-only scenario. A scenario that streams arrivals to one worker in one namespace runs that worker in the process that enqueues, as a single Acta host does: it shares that process's heap and thread pool and the enqueue calls' connection pool, and each enqueue wakes it at once. A worker process claims again while its claims return jobs; after an empty claim it waits up to its 1 s safety poll, or until Redis wakes it where the setup says so.

| Disk, by phase and drive | Windows counters |
|---|---|
| before \| 0 c: | write 2.95 ms mean, 8.30 ms p95, 8 writes/s, 99% idle, 14 samples |
| before \| 1 d: | write 5.60 ms mean, 7.80 ms p95, 12 writes/s, 99% idle, 14 samples |
| chaos mssql \| 0 c: | write 3.42 ms mean, 7.85 ms p95, 7 writes/s, 99% idle, 1420 samples |
| chaos mssql \| 1 d: | write 0.17 ms mean, 0.35 ms p95, 621 writes/s, 71% idle, 1420 samples |
| chaos pg \| 0 c: | write 3.41 ms mean, 7.96 ms p95, 6 writes/s, 99% idle, 592 samples |
| chaos pg \| 1 d: | write 0.07 ms mean, 0.07 ms p95, 804 writes/s, 53% idle, 592 samples |
| chaos sqlite \| 0 c: | write 3.55 ms mean, 8.11 ms p95, 7 writes/s, 99% idle, 385 samples |
| chaos sqlite \| 1 d: | write 1.56 ms mean, 7.27 ms p95, 697 writes/s, 77% idle, 385 samples |
| correctness \| 0 c: | write 3.48 ms mean, 7.89 ms p95, 8 writes/s, 99% idle, 324 samples |
| correctness \| 1 d: | write 0.31 ms mean, 1.66 ms p95, 502 writes/s, 81% idle, 324 samples |
| mssql \| 0 c: | write 3.43 ms mean, 7.91 ms p95, 7 writes/s, 99% idle, 3688 samples |
| mssql \| 1 d: | write 1.09 ms mean, 6.23 ms p95, 1534 writes/s, 64% idle, 3688 samples |
| pg \| 0 c: | write 3.47 ms mean, 8.07 ms p95, 7 writes/s, 99% idle, 4056 samples |
| pg \| 1 d: | write 0.99 ms mean, 6.19 ms p95, 727 writes/s, 68% idle, 4056 samples |
| sqlite \| 0 c: | write 2.52 ms mean, 7.88 ms p95, 11 writes/s, 99% idle, 2415 samples |
| sqlite \| 1 d: | write 0.63 ms mean, 5.84 ms p95, 736 writes/s, 71% idle, 2415 samples |
| after \| 0 c: | write 3.46 ms mean, 7.58 ms p95, 6 writes/s, 99% idle, 15 samples |
| after \| 1 d: | write 4.33 ms mean, 7.88 ms p95, 5 writes/s, 100% idle, 15 samples |
