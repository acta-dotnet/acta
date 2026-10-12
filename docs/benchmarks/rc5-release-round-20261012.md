# rc.5 release round: the candidate beside rc.4 on the same machine

The `1.0.0-rc.5` benchmark round on the certified commit `4f410f68`: the `release` preset (every
execution profile at the quick matrix's points, 5,000 jobs, one warmup and one measured run) once per
database, from a clean worktree of that commit, so every file records `gitDirty = false`. The harness
ran as a Native AOT executable, PostgreSQL autovacuum on, SQLite's bench file on D:.

rc.4 numbers come from its own round on this machine, 2026-10-02 (`rc4-release-round-20261002.md`);
no rc.4 cell was rerun for the comparison. Every cell that looked like a finding was then rerun on
both trees back to back, twice, with the harness's `--scenario` filter, rc.4's harness built from its
tag; the reruns decide the findings below. rc.5 drops the Bulk profile, and its harness drains at 64
executors where rc.4's drained at 16, so the drain cells are reported for the candidate only.

| file | tree | provider | fdatasync/s at start |
| --- | --- | --- | ---: |
| `baseline-20261012T100249Z` | rc.5 `4f410f68` | PostgreSQL | 2,832 |
| `baseline-20261002T160453Z` | rc.4 `d616c72b` | PostgreSQL | |
| `baseline-20261012T101340Z` | rc.5 `4f410f68` | SQL Server | 2,764 |
| `baseline-20261002T163347Z` | rc.4 `d616c72b` | SQL Server | |
| `baseline-20261012T102944Z` | rc.5 `4f410f68` | SQLite, bench file on D: | 2,811 |
| `baseline-20261002T172838Z` | rc.4 `d616c72b` | SQLite, bench file on D: | |

**How to read it.** One measured run per cell makes a single cell noisy, and the throughput cells at 1
and 8 executors are bimodal on this machine in every tree measured, so the verdict reads drain,
enqueue, latency, and the list query, and quotes the throughput cells without weighing them. The
fdatasync rate is `pg_test_fsync` inside the PostgreSQL container, read twice before each database;
each start waited for two readings above 2,500.

**The rate cells are void.** The harness declares its metered job at 1,000/s and retunes the meter
through the operator override just before it enqueues. rc.5 reloads a worker's policy on its
heartbeat rather than every second, so an override reaches every worker within one heartbeat (about
15 seconds in the bench host), and both rate cells drain at the declared 1,000/s before the override
lands: zero re-arms, every window over its contract. The meter itself held in all six certification
runs of the round, which declare their rates on the job. The harness has to wait for the override, or
declare the cell's rate, before the cells mean anything; they are left out of the tables below.

## PostgreSQL

| cell | unit | rc.4 | rc.5 | change |
| --- | --- | ---: | ---: | ---: |
| throughput Buffered e=1 | jobs/s | 163 | 578 | +254% |
| throughput Buffered e=8 | jobs/s | 1,034 | 3,433 | +232% |
| throughput Buffered e=32 | jobs/s | 3,571 | 8,487 | +138% |
| throughput Direct e=1 | jobs/s | 166 | 613 | +269% |
| throughput Direct e=8 | jobs/s | 1,621 | 3,878 | +139% |
| throughput Direct e=32 | jobs/s | 4,787 | 13,916 | +191% |
| latency Buffered | p50 ms | 9.08 | 4.80 | +47% |
| latency Direct | p50 ms | 6.44 | 3.70 | +42% |
| drain Buffered w=1 (e=64) | jobs/s | | 8,283 | |
| drain Buffered w=16 (e=64) | jobs/s | | 11,916 | |
| drain Direct w=1 (e=64) | jobs/s | | 14,878 | |
| drain Direct w=16 (e=64) | jobs/s | | 33,126 | |
| enqueue w=1 | enq/s | 335 | 682 | +103% |
| enqueue w=16 | enq/s | 4,099 | 6,718 | +64% |
| enqueue-batch w=1 | ms | 8,116 | 8,688 | -7% |
| enqueue-batch w=16 | ms | 1,113 | 1,221 | -10% |
| query-list 50000 rows | p50 ms | 84.00 | 91.13 | -8% |
| lanes Buffered unlaned | jobs/s | 2,133 | 3,361 | +58% |
| lanes Buffered lane-wide | jobs/s | 1,892 | 3,228 | +71% |
| lanes Buffered key-hot | jobs/s | 15.14 | 12.77 | -16% |
| lanes Buffered lane-deep | jobs/s | 226 | 181 | -20% |
| lanes Direct unlaned | jobs/s | 2,054 | 3,945 | +92% |
| lanes Direct lane-wide | jobs/s | 1,794 | 3,733 | +108% |
| lanes Direct key-hot | jobs/s | 21.78 | 17.32 | -20% |
| lanes Direct lane-deep | jobs/s | 331 | 245 | -26% |
| claim-skew Buffered none | p50 ms | 1,359 | 918 | +33% |
| claim-skew Buffered parked | p50 ms | 1,289 | 836 | +35% |
| claim-skew Buffered delayed-high | p50 ms | 1,320 | 899 | +32% |
| claim-skew Direct none | p50 ms | 1,570 | 672 | +57% |
| claim-skew Direct parked | p50 ms | 1,612 | 696 | +57% |
| claim-skew Direct delayed-high | p50 ms | 1,640 | 667 | +59% |

## SQL Server

| cell | unit | rc.4 | rc.5 | change |
| --- | --- | ---: | ---: | ---: |
| throughput Buffered e=1 | jobs/s | 168 | 518 | +209% |
| throughput Buffered e=8 | jobs/s | 997 | 3,110 | +212% |
| throughput Buffered e=32 | jobs/s | 2,962 | 5,767 | +95% |
| throughput Direct e=1 | jobs/s | 180 | 605 | +236% |
| throughput Direct e=8 | jobs/s | 818 | 3,297 | +303% |
| throughput Direct e=32 | jobs/s | 2,491 | 5,069 | +104% |
| latency Buffered | p50 ms | 5.30 | 4.78 | +10% |
| latency Direct | p50 ms | 4.04 | 3.63 | +10% |
| drain Buffered w=1 (e=64) | jobs/s | | 3,994 | |
| drain Buffered w=16 (e=64) | jobs/s | | 10,680 | |
| drain Direct w=1 (e=64) | jobs/s | | 12,841 | |
| drain Direct w=16 (e=64) | jobs/s | | 22,360 | |
| enqueue w=1 | enq/s | 395 | 757 | +92% |
| enqueue w=16 | enq/s | 4,136 | 7,068 | +71% |
| enqueue-batch w=1 | ms | 5,854 | 5,048 | +14% |
| enqueue-batch w=16 | ms | 9,529 | 8,051 | +16% |
| query-list 50000 rows | p50 ms | 8.67 | 7.82 | +10% |
| lanes Buffered unlaned | jobs/s | 2,544 | 3,245 | +28% |
| lanes Buffered lane-wide | jobs/s | 2,032 | 2,585 | +27% |
| lanes Buffered key-hot | jobs/s | 13.72 | 15.55 | +13% |
| lanes Buffered lane-deep | jobs/s | 219 | 173 | -21% |
| lanes Direct unlaned | jobs/s | 1,395 | 3,085 | +121% |
| lanes Direct lane-wide | jobs/s | 1,582 | 2,639 | +67% |
| lanes Direct key-hot | jobs/s | 24.40 | 21.26 | -13% |
| lanes Direct lane-deep | jobs/s | 276 | 239 | -13% |
| claim-skew Buffered none | p50 ms | 1,353 | 1,135 | +16% |
| claim-skew Buffered parked | p50 ms | 1,293 | 1,050 | +19% |
| claim-skew Buffered delayed-high | p50 ms | 1,335 | 1,407 | -5% |
| claim-skew Direct none | p50 ms | 1,790 | 1,141 | +36% |
| claim-skew Direct parked | p50 ms | 1,864 | 974 | +48% |
| claim-skew Direct delayed-high | p50 ms | 2,580 | 997 | +61% |

## SQLite

Direct now syncs every commit, as Buffered already did (release notes, "SQLite syncs every commit under both profiles").

| cell | unit | rc.4 | rc.5 | change |
| --- | --- | ---: | ---: | ---: |
| throughput Buffered e=1 | jobs/s | 124 | 106 | -14% |
| throughput Buffered e=8 | jobs/s | 358 | 281 | -21% |
| throughput Buffered e=32 | jobs/s | 365 | 251 | -31% |
| throughput Direct e=1 | jobs/s | 761 | 1,058 | +39% |
| throughput Direct e=8 | jobs/s | 963 | 3,721 | +286% |
| throughput Direct e=32 | jobs/s | 971 | 6,146 | +533% |
| latency Buffered | p50 ms | 17.28 | 16.80 | +3% |
| latency Direct | p50 ms | 2.01 | 19.60 | -873% |
| drain Buffered w=1 (e=64) | jobs/s | | 92.91 | |
| drain Direct w=1 (e=64) | jobs/s | | 9,659 | |
| enqueue w=1 | enq/s | 2,394 | 1,260 | -47% |
| enqueue w=16 | enq/s | 2,394 | 1,279 | -47% |
| enqueue-batch w=1 | ms | 8,658 | 7,931 | +8% |
| enqueue-batch w=16 | ms | 8,737 | 7,943 | +9% |
| query-list 50000 rows | p50 ms | 30.19 | 31.85 | -5% |
| lanes Buffered unlaned | jobs/s | 362 | 290 | -20% |
| lanes Buffered lane-wide | jobs/s | 272 | 291 | +7% |
| lanes Buffered key-hot | jobs/s | 255 | 133 | -48% |
| lanes Buffered lane-deep | jobs/s | 73.20 | 64.03 | -13% |
| lanes Direct unlaned | jobs/s | 984 | 3,788 | +285% |
| lanes Direct lane-wide | jobs/s | 568 | 3,633 | +539% |
| lanes Direct key-hot | jobs/s | 521 | 147 | -72% |
| lanes Direct lane-deep | jobs/s | 724 | 64.33 | -91% |
| claim-skew Buffered none | p50 ms | 15,665 | 11,784 | +25% |
| claim-skew Buffered parked | p50 ms | 13,215 | 19,048 | -44% |
| claim-skew Buffered delayed-high | p50 ms | 14,640 | 18,862 | -29% |
| claim-skew Direct none | p50 ms | 4,866 | 712 | +85% |
| claim-skew Direct parked | p50 ms | 4,838 | 727 | +85% |
| claim-skew Direct delayed-high | p50 ms | 4,896 | 757 | +85% |

## Findings that survived the reruns

Each line is rc.4 against rc.5 in jobs/s, first rerun pass then second, both trees back to back.

- **Serial lane cells on PostgreSQL.** One deep lane: Direct 332 against 251, then 291 against 253;
  Buffered 207 against 177, then 207 against 176. One hot concurrency key: Direct 20.9 against 16.8,
  then 20.0 against 16.2; Buffered 15.6 against 13.9, then 16.1 against 13.8. Every job in these cells
  waits for the one before it to land, so the cell is the per-job cost of a completion, a promotion,
  and the next claim in series: about one millisecond more per job on the deep lane in rc.5, and about
  ten on the hot key, whose jobs also wait out a concurrency-key bounce. The wide cells, where lanes
  run side by side, are 68% to 117% faster.
- **The deep lane on SQL Server, Buffered.** 222 against 179, then 222 against 176. Its Direct deep
  lane and both hot-key cells split between the passes (Direct deep lane 239 against 237, then 337
  against 235; hot key within 13% either way) and are not findings.
- **SQLite Buffered.** Throughput at one executor 128 against 107, then 128 against 106; at eight, 354
  against 280, then 358 against 263; unlaned lanes 348 against 261, then 373 against 301. Buffered
  already synced every commit in rc.4, so the new FULL sync does not explain it. At 32 executors the
  passes split (+31%, then even), and the deep lane is even.

The rerun files stay in the harness's own output folders and are not filed here.

## Not findings

- PostgreSQL's `enqueue-batch` and list query, 7% to 10% slower in the round, reran within 6% either
  way (5,700 against 6,066 ms, then 5,825 against 5,832 at one producer; 83.0 against 84.1 ms, then
  83.0 against 83.8 for the list).
- SQLite Direct latency (2.01 to 19.60 ms), enqueue (2,394 to about 1,270 per second), and its serial
  lane cells are the cost of `synchronous = FULL`, which rc.5 applies to Direct as well: each of those
  cells is a chain of single-row commits, and each commit now waits for the drive.

## Open

SQLite's Buffered drain reads 93 jobs/s at 64 executors and one worker, against 9,659 for Direct in the
same round and 394 for Buffered at rc.4's 16 executors. The shapes differ, so it is not a comparison,
but a Buffered worker holding 96 permits on one SQLite writer is the shape the round has not explored.
