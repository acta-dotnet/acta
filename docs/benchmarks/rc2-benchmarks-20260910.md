# rc.2 benchmarks: one round on the release tree, against August

Three matrices captured on 2026-09-10, one per provider, on an idle machine, from the tree
`5b31fcd1`: `baseline-20260910T183340Z` (SQLite), `baseline-20260910T185459Z` (PostgreSQL),
`baseline-20260910T191550Z` (SQL Server). That tree differs from the certified tree `22d74be6` by
the certification pages only; `git diff 22d74be6 5b31fcd1 -- src/ tools/ anvil/` is empty, and each
file's engine version carries the SHA of the tree its binaries were built from. The comparison is
`baseline-20260822T134457Z`, the rc.1-era matrix committed beside them, captured on the same machine.

**One round establishes what each cell did once.** Nothing here is a noise band, and no percentage
below is a threshold a future change can hide under. Earlier rc.2 rounds were taken on trees that
changed afterwards and are not on the branch; this page cites only what is. The machine's own limits
are in [known limitations](../technical/known-limitations.md).

## SQL Server: the locking work is visible, and the shape changed

| profile | e=1 | e=2 | e=4 | e=8 | e=16 | e=32 |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| Buffered, August | 147 | 275 | 517 | 2,496 | 3,941 | 4,218 |
| **Buffered, now** | **419** | **825** | **1,524** | 2,597 | 4,110 | 4,135 |
| Direct, August | 379 | 589 | **162** | **317** | 2,610 | 4,085 |
| **Direct, now** | 390 | 625 | **1,039** | **1,819** | 2,981 | 4,334 |
| Bulk, August | **89** | **174** | 2,015 | 3,629 | 6,048 | 8,722 |
| **Bulk, now** | **649** | **1,173** | 2,311 | 3,805 | 6,594 | 10,388 |

The bold August cells are the interesting ones. That sweep does not rise with executors: Direct falls
from 589 to 162 between two and four, and Bulk opens at 89 and 174 before jumping to 2,015. Those are
the cells whose `runtimes` update compiled a scan-sourced plan, took an update lock per row,
escalated to a table lock and held it to commit. Which cells did that was not stable between runs,
so averaging a sweep read as enormous variance.

All three profiles now rise monotonically in executors. That change of shape is the result worth
reporting; the peak moving from 8,722 to 10,388 is the smaller half of it.

**Seventeen of the eighteen cells are above August. One is not:** Buffered at 32 executors reads
4,135 against 4,218, about 2% lower. Drain is mixed: Bulk reads 4,921 / 14,130 / 19,079 against
5,409 / 15,460 / 19,939 at 1 / 4 / 16 workers, 4% to 9% under; Direct at one worker is 19% up and at
sixteen 18% down. Latency p50 sits between 3.22 and 4.31 ms across profiles, and enqueue runs 829 /
3,038 / 7,801 jobs/s at 1 / 4 / 16 producers. Every cell in the matrix completed and none aborted on
a deadlock, which is where the heartbeat lock-ordering change would show if it were wrong.

## PostgreSQL: no gain, and a possible small cost

| profile | e=1 | e=2 | e=4 | e=8 | e=16 | e=32 |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| Bulk, August | 482 | 959 | 1,860 | 3,643 | 6,890 | 12,242 |
| Bulk, now | 456 | 902 | 1,804 | 3,480 | 6,640 | 12,078 |

Buffered lands within 3% below and 4% above August across the sweep; Bulk sits 1% to 5% below it
across the whole sweep; drain is 0% to 8% under. The direction is expected: the escalation and
lock-ordering work was SQL Server's, and PostgreSQL 18 does not have that pathology. No improvement
was predicted here and none appeared.

The consistent Bulk dip has a plausible mechanism rather than only a plausible dismissal. Fixing the
PostgreSQL deadlock made batch completion sort by `job_id` before its ordinals are assigned, and a
sort is not free. One round cannot separate a small real cost from this workload's own movement, so
this is recorded as open rather than explained. Deciding it needs a paired comparison against a
build with the sort removed, which is a measurement nobody has run.

**Direct at two and four executors read 323 and 427 against August's 444 and 877**, 27% and 51%
under, while the rest of the Direct sweep is within 5% of August in either direction. No change on
the branch touches a path only the Direct profile takes, and the neighbouring cells do not move, so
these two are recorded as observed and not attributed. They are the cells to watch in the next round
before anything is read into them.

Drain peaks at 40,080 jobs/s (Bulk, 16 workers); batch enqueue at 235,682 jobs/s at 16 producers.

## SQLite: level with August, and the stall did not recur

| profile | e=1 | e=2 | e=4 | e=8 | e=16 | e=32 |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| Direct, August | 678 | 838 | 969 | 1,071 | 1,152 | 1,171 |
| Direct, now | 691 | 887 | 1,024 | 1,095 | 1,133 | 1,157 |
| Bulk, August | 627 | 826 | 968 | 1,033 | 1,090 | 1,104 |
| Bulk, now | 695 | 824 | 902 | 987 | 1,038 | 1,035 |

Direct runs within 5% of August in either direction; Bulk is 10% up at one executor and 4% to 6%
down from four; Buffered is mixed, within about 11% either way. Drain is within 4%. Peak throughput
is 1,157 jobs/s (Direct, 32 executors).

Every cell completed. An earlier rc.2 round marked SQLite Buffered at 32 executors `incomplete`, with
a handful of jobs never reaching handler entry inside a ten-minute window. That did not happen here.
One round not reproducing an intermittent stall is not the same as the stall being fixed, and nothing
in this round explains it, so it stays worth watching rather than closed.

## Wall clock

One provider at a time, on an idle machine, immediately after the certification quartet: SQLite
36m 38s, PostgreSQL 21m 18s, SQL Server 20m 51s, each for the full 148-cell matrix (124 for SQLite,
which has fewer scenarios).

## Reproducing

`dotnet run --project anvil/Anvil.Bench -c Release -- full --db <provider>`, one provider at a time,
on an idle machine. Per-provider rather than `--db all`: the harness writes its files only on
completion, so a machine reset during a combined run loses every provider that had not finished. A
busy machine halves the server cells above one executor without any change to the engine; the
harness records SQL Server's log-flush wait per cell as `extraMetrics.writeLogWaitMs`, and a round
whose wait is several times the idle figure is measuring the disk, not the code.
