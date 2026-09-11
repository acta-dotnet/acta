# rc.3: SQL Server page-latch attribution

Captured 2026-09-11 on the rc.3 working tree (Tracks A, B, C, D applied; Track E not yet), SQL
Server 2022 in Docker on the local bench box, `full --db mssql --scenario throughput --scenario
drain`, median of three measured runs after one warmup. Raw file:
`anvil/Anvil.Bench/.benchmarks/baseline-20260911T102651Z.json`.

## Why

The rc.2 baseline showed `Buffered` throughput on one worker going flat from 16 to 32 executors
(4,110 to 4,135 jobs/s) while the server-wide `PAGELATCH` wait grew about ninefold. The commit that
reordered the heartbeat's row locks (`4c04668f`) suspected `ix_runtimes_worker_inflight` and said the
contention "needs a DDL change". `sys.dm_os_wait_stats` cannot say which page is hot, so the bench
harness now samples `sys.dm_db_index_operational_stats` per index around every SQL Server cell and
records the busiest five as `pageLatchByIndex`.

## What the data says

| Cell | jobs/s | Page-latch wait, total | Busiest index | Share |
| --- | ---: | ---: | --- | ---: |
| throughput Buffered, 16 executors | 3,831 | 2.2 s | `runtimes.pk_runtimes` | 98% |
| throughput Buffered, 32 executors | 4,271 | 19.1 s | `runtimes.pk_runtimes` | 98% |
| throughput Direct, 32 executors | 4,367 | 3.0 s | `runtimes.pk_runtimes` | 91% |
| throughput Bulk, 32 executors | 10,409 | 0.5 s | `results.pk_results` | 40% |
| drain Buffered, 16 executors x 4 workers | | 42.8 s | `runtimes.pk_runtimes` | 93% |
| drain Buffered, 16 executors x 16 workers | | 36.3 s | `results.pk_results` 17.1 s, `ix_runtimes_retention` 9.5 s, `pk_runtimes` 7.8 s | |
| drain Direct, 16 executors x 16 workers | | 48.8 s | `runtimes.pk_runtimes` | 77% |

`ix_runtimes_worker_inflight` never exceeds 24 ms in any cell. The suspicion in the commit message was
wrong; the heartbeat index is not the hot page.

## Reading

**`pk_runtimes`, the clustered key of the hot table.** Job ids are sequential, and the jobs a single
worker's executors claim, start, and complete at the same moment are neighbours, so their runtime rows
share a handful of leaf pages and every update takes an exclusive page latch. `Buffered` pays twice
per job (claim to `Dispatched`, then start to `Executing`) before the completion write, which is why it
plateaus first and hardest. This is not an insert convoy: the key already carries
`OPTIMIZE_FOR_SEQUENTIAL_KEY`, which only paces inserts. The DDL that would spread the rows is a
physical redesign, a hash bucket leading the clustered key or a memory-optimized table, and either
changes every routine's seek on `runtimes`. Decision for rc.3: document it as the single-worker
ceiling of the `Buffered` profile on SQL Server and leave the key alone.

**`pk_results` and `ix_runtimes_retention`, two insert convoys.** Both take rows in key order: results
arrive by ascending job id, and the retention index by ascending retention instant. Neither carried the
sequential-key option before rc.3; both do now, and the option is exactly what SQL Server offers for a
tail that many sessions insert into at once. The effect is measured in the rc.3 benchmark round.

## Method note

Wait times are cumulative DMV counters sampled before and after each cell and differenced. A negative
delta (an index rebuilt mid-cell) drops that entry rather than reporting a negative wait. The box was
not idle during this run, so absolute throughput here is lower than the rc.2 baseline; the attribution
is a ranking within each cell and does not depend on that.
