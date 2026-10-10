# rc.5 engine round: the rebuilt execution engine at full scale

The drain cells of the execution-engine rebuild, measured the night it landed, on three commits:
the engine with per-job completion (`0ca7abc8`, ENGINE-PLAN steps 1 to 12), the first finalizer
batching plain completions through `complete_executions` (`b1d5a9f5`, step 13), and the final cut
with one finalizer, batches to 128, and the SQL Server batch bound as a table type (`817ad615`).
`full --scenario drain --drain-workers 16,24,32 --jobs 100000`, one warmup and the median of three
runs per cell, sixteen executors per worker, the harness published as a native executable from a
clean worktree, client pool raised to 400 connections. The baseline is the per-job engine measured
on 2026-10-03 on the rc.5 Phase 1 commit with the same command, one run each (the numbers
ENGINE-PLAN.md records); the rounds are days apart on the same machine, which is the comparison
the benchmark round policy allows for drain cells.

| file | tree | provider | note |
| --- | --- | --- | --- |
| `baseline-20261010T035606Z` | `0ca7abc8` per job | PostgreSQL | drive in its fast state, about 3,000 flushes/s |
| `baseline-20261010T040746Z` | `0ca7abc8` per job | SQL Server | discarded: `acta-dev` had been recreated without `READ_COMMITTED_SNAPSHOT`; 28 to 84 deadlocks per cell between the heartbeat's index read and completions |
| (rerun 06:10 local, same harness) | `0ca7abc8` per job | SQL Server | snapshot reads on, no deadlocks; the numbers below |
| `baseline-20261010T044617Z` | `b1d5a9f5` first batching | PostgreSQL | |
| `baseline-20261010T045449Z` | `b1d5a9f5` first batching | SQL Server | |
| `baseline-20261010T053410Z` | `817ad615` final | PostgreSQL | |
| `baseline-20261010T054159Z` | `817ad615` final | SQL Server | |

## PostgreSQL

Drain, jobs/s, 100,000 jobs, 16 executors per worker:

| tree | Direct w=16 | w=24 | w=32 | Buffered w=16 | w=24 | w=32 |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| final `817ad615`: one finalizer, batches to 128 | 23,349 | 17,812 | 16,704 | 13,752 | 12,962 | 13,010 |
| first batching `b1d5a9f5`: four loops, batches to 16 | 18,983 | 16,587 | 15,300 | 11,398 | 10,835 | 10,425 |
| per job `0ca7abc8` | 11,457 | 10,954 | 10,497 | 7,987 | 7,148 | 6,740 |
| per-job baseline (2026-10-03) | 9,333 | 10,844 | 10,674 | | | |

The engine alone lifts Direct at sixteen workers 23 percent above the baseline and leaves 24 and
32 level: the executor is freed the moment its completion is built and the finalizer writes while
the next job runs, and above sixteen workers the commit rate is the ceiling, not the worker.
Batching moves that ceiling: the finalizer takes whatever is waiting in one `complete_executions`
call, so the commits per job fall as the writes fall behind. The first cut ran four finalizer loops
with batches capped at the executor count, and the loops were fast enough to leave most batches at
a row or two; one loop with the cap at 128 forms batches on its own, because everything that
arrives while it writes is the next batch. Direct then reads two and a half times the baseline at
sixteen workers, and Buffered 72 percent over its own per-job cell. The three runs of each cell sit
within three percent of each other. Buffered has no 100,000-job baseline; the rc.4 release round
read 6,762 at sixteen workers on 5,000 jobs.

## SQL Server

| tree | Direct w=16 | w=24 | w=32 | Buffered w=16 | w=24 | w=32 |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| final `817ad615`: one finalizer, batches to 128, table type | 6,952 | 7,492 | 7,435 | 9,881 | 10,867 | 11,392 |
| first batching `b1d5a9f5`: four loops, batches to 16, JSON | 6,389 | 6,589 | 6,776 | 9,274 | 8,753 | 9,148 |
| per job `0ca7abc8` | 4,410 | 4,038 | 4,786 | 5,941 | 6,151 | 6,245 |
| per-job baseline (2026-10-03) | 4,554 | 4,112 | 3,757 | 5,943 | 6,369 | 7,034 |

Per job, the engine reads level within a run's spread on both profiles: the `pk_runtimes` page
latches that bound SQL Server's completion are untouched by the rebuild. Batched, Buffered reads
62 to 71 percent above the baseline and Direct 53 to 98 percent, and both now climb with workers
where Direct used to fall, since one batch takes the latch once for many rows. The final cut binds
the batch as the table type `complete_executions_batch`, where the first cut passed JSON text; the
two changes (the cap with one loop, and the table type) were measured together, so their shares are
not separated here.

## Verdict

Batching justifies itself on every cell of both providers; steps 12 and 13 stay. The per-job
engine is a smaller but real gain on PostgreSQL at the worker count the site quotes, and level
elsewhere. Not measured in this round: the multi-process fleet drain and the per-namespace split
the plan's step 14 also asks for.

## What the round found

- A database created by hand without `READ_COMMITTED_SNAPSHOT` deadlocks under load between
  `extend_worker_leases`'s read of `ix_runtimes_worker_inflight` and `complete_execution`'s row
  update. The migrator flips the setting when it creates the database and the test fixtures do too;
  the bench's schema reset does not, so a bench database must be created with it on. Not a product
  defect, but worth a line in the operator guide: the SQL Server provider requires snapshot reads.
- Several finalizer loops defeat batching: four loops behind sixteen executors landed batches
  faster than completions arrived, so most batches were a row or two. One loop forms batches by
  itself, with no wait for more, and a lone completion is still written at once, so the latency
  cost at low load is nil.
- The deleted Bulk profile's 29,000 (5,000 jobs) and 31,000 to 38,000 (10,000 jobs) were smaller
  cells on a different contract: Bulk waited up to 250 ms to fill a batch of 100 and let the executor
  go before the write committed. The final cut keeps every completion durable before anyone is woken
  and never waits to fill a batch.
