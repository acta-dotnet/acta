# rc.3 full matrix: the certified tree against rc.2, same machine, same night

The `1.0.0-rc.3` release round: `full` matrices from clean worktrees of the certified commit and of
`v1.0.0-rc.2` (`4c19e61b`) as the control, taken on 2026-09-21 and 22 as candidate, control,
candidate per server provider and candidate, control on SQLite, so a pair shares whatever state the
machine is in. The `full` preset: 10,000 jobs, 500,000 batch jobs, 100,000 rows, one warmup and the
median of three measured runs per cell. Every JSON records its commit and `gitDirty = false`; the
candidate worktree records `84ea8fda`, whose tree differs from the certified commit `d3def975` by one
conformance test file alone, `RateLimitSpec.cs`, whose two timing premises were measured rather than
assumed after the CI runner failed to meet them; no source, routine, or schema differs. PostgreSQL autovacuum was off for the round on both trees, and the bench schemas were
dropped before the round and after every matrix.

| file | tree | cells |
| --- | --- | --- |
| `baseline-20260921T184939Z` | rc.3 sample a | PostgreSQL |
| `baseline-20260921T191936Z` | rc.2 control | PostgreSQL |
| `baseline-20260921T200156Z` | rc.3 sample b | PostgreSQL |
| `baseline-20260921T204149Z` | rc.3 sample a | SQL Server |
| `baseline-20260921T211129Z` | rc.2 control a | SQL Server |
| `baseline-20260921T215301Z` | rc.3 sample b | SQL Server |
| `baseline-20260922T005657Z` | rc.2 control b | SQL Server, second pair |
| `baseline-20260922T013957Z` | rc.3 sample c | SQL Server, second pair |
| `baseline-20260921T225157Z` | rc.3 | SQLite, bench file on D: |
| `baseline-20260921T232834Z` | rc.2 control | SQLite, bench file on D: |
| `baseline-20260922T015952Z` | rc.3 | SQLite, the Buffered throughput cell re-run |
| `baseline-20260922T021641Z` | rc.2 control | SQLite, the Buffered throughput cell re-run |

**How to read a pair.** The drive under the Docker disk image is QLC and flaps between about
2,500 and 500 flushes per second on minute timescales, so cells that commit on every job read the
state they ran in, not only the tree. Direct and Bulk throughput, drain, enqueue, and the list query
reproduce within their own spread across states and carry the verdict; a Buffered cell is reported
and read only where both trees landed in the same state.

## PostgreSQL: level or ahead

Direct throughput, jobs/s, one worker, executors 1 to 32:

| tree | e=1 | e=2 | e=4 | e=8 | e=16 | e=32 |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| rc.3 a | 176 | 301 | 402 | 779 | 1,582 | 2,845 |
| rc.2 | 155 | 216 | 439 | 894 | 1,626 | 2,875 |
| rc.3 b | 171 | 213 | 549 | 891 | 1,710 | 3,149 |

Bulk throughput, same shape:

| tree | e=1 | e=2 | e=4 | e=8 | e=16 | e=32 |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| rc.3 a | 304 | 685 | 1,277 | 2,464 | 4,497 | 7,917 |
| rc.2 | 282 | 572 | 1,183 | 2,283 | 4,320 | 8,073 |
| rc.3 b | 343 | 706 | 1,391 | 2,532 | 4,703 | 9,271 |

Drain, jobs/s, workers 1, 4, 16 of 16 executors:

| tree | Direct w=1 | w=4 | w=16 | Bulk w=1 | w=4 | w=16 |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| rc.3 a | 3,193 | 8,894 | 13,075 | 7,015 | 25,049 | 33,658 |
| rc.2 | 2,821 | 8,191 | 12,344 | 6,228 | 21,630 | 37,672 |
| rc.3 b | 3,401 | 9,217 | 13,402 | 7,942 | 25,837 | 37,197 |

Direct latency, per-job round trip in ms at 1, 8, 32 executors: rc.3 a 6.80 / 8.43 / 9.38, rc.2
5.88 / 7.56 / 8.01, rc.3 b 5.74 / 7.27 / 7.52. Batch enqueue at 16 producers: rc.3 b 211,857 jobs/s
against rc.2's 185,737; the 100,000-row list query 135 ms against 155 ms.

Sample a ran first, on a drive that had just taken the Anvil build and the schema drops, and its
latency reads a tenth above the control on every profile; sample b, taken after the control in the
same state, reads below it on every profile. Direct throughput is level within its spread past four
executors and ahead below it, in both samples. Drain is ahead on Direct at every worker count and
level on Bulk. The claim change this release carries, the sixth `claim_batch` parameter with the
worker's exclusion set, costs the healthy-fleet claim nothing PostgreSQL can measure: the custom
plan folds the term away and the generic plan never executes its sub-plan, and a quick-preset pair
taken earlier the same day read the single-executor cell four percent under rc.3 where these two
full matrices read it ahead of rc.2.

## SQL Server: one control run in a different state, otherwise level

Direct throughput, jobs/s:

| tree | e=1 | e=2 | e=4 | e=8 | e=16 | e=32 |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| rc.3 a | 381 | 259 | 491 | 814 | 1,829 | 3,662 |
| rc.2 a | 221 | 415 | 468 | 811 | 2,686 | 3,989 |
| rc.3 b | 290 | 272 | 579 | 908 | 1,607 | 4,245 |
| rc.2 b | 210 | 275 | 1,106 | 1,894 | 3,177 | 2,679 |
| rc.3 c | 205 | 478 | 487 | 880 | 2,881 | 2,597 |

Bulk throughput, jobs/s:

| tree | e=1 | e=2 | e=4 | e=8 | e=16 | e=32 |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| rc.3 a | 591 | 981 | 2,061 | 3,324 | 3,573 | 5,561 |
| rc.2 a | 375 | 634 | 1,198 | 3,444 | 5,406 | 9,961 |
| rc.3 b | 663 | 1,098 | 2,094 | 3,632 | 6,355 | 6,023 |
| rc.2 b | 378 | 1,094 | 2,114 | 2,209 | 3,720 | 6,959 |
| rc.3 c | 381 | 1,040 | 2,062 | 2,144 | 3,618 | 6,190 |

Drain, jobs/s: rc.3 a Direct 2,607 / 5,005 / 6,947 and Bulk 5,095 / 13,820 / 19,384; rc.2 a Direct
2,344 / 4,854 / 6,488 and Bulk 5,026 / 13,796 / 21,319; rc.3 b Direct 2,375 / 4,950 / 7,012 and Bulk
5,267 / 12,967 / 18,974; rc.2 b Direct 3,019 / 5,307 / 6,879 and Bulk 5,456 / 8,521 / 18,696; rc.3 c
Direct 3,000 / 5,215 / 6,644 and Bulk 5,804 / 14,488 / 18,174. Direct latency at 1, 8, 32 executors:
rc.3 a 6.28 / 8.23 / 11.83, rc.2 a 3.33 / 4.73 / 13.40, rc.3 b 5.52 / 9.01 / 15.94, rc.2 b 5.55 /
6.72 / 8.09, rc.3 c 5.38 / 6.59 / 7.86.

The first pair read as a regression: both candidate samples a third under the control on Bulk at 32
executors and on Direct at 16, and latency at one and eight executors nearly double. The control's
first matrix is the reading that stands apart. Its single-executor latency of 3.33 ms is below every
earlier measurement of rc.2 itself on this machine, 5.62 ms in the rc.3 round of 2026-09-20; its
9,961 jobs/s Bulk at 32 executors is above rc.2's 6,512 in that round; and the candidate's numbers
sit where both trees measured before. A second pair, control first, was taken after the
certification quartet: the control moved to 5.55 ms and 6,959 jobs/s, the same distance as the gap
between the trees, and the candidate's third sample, taken right after it, reads 5.38 ms and 6,190
jobs/s, inside the control's own range on every Direct and Bulk cell and ahead of it on one-worker
drain. The claim procedure itself was timed in isolation as on PostgreSQL, the pre-change
procedure installed beside the current one in the conformance schema: 105 against 120 microseconds
per empty claim, three thousand calls each, twice; fifteen microseconds cannot move a millisecond
cell. A pair split across states is discarded whichever way it points.

## SQLite: ahead at every executor count, one cell to re-run

Direct and Bulk throughput, jobs/s, one worker:

| tree | profile | e=1 | e=2 | e=4 | e=8 | e=16 | e=32 |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: |
| rc.3 | Direct | 847 | 943 | 1,041 | 1,098 | 1,109 | 1,125 |
| rc.2 | Direct | 666 | 847 | 1,017 | 1,090 | 1,123 | 1,137 |
| rc.3 | Bulk | 865 | 983 | 1,064 | 1,109 | 1,148 | 1,164 |
| rc.2 | Bulk | 625 | 811 | 910 | 973 | 987 | 1,049 |

Drain at one worker: rc.3 Direct 1,198 and Bulk 1,206 against rc.2's 1,113 and 1,108. Direct latency
at 1, 8, 32 executors: rc.3 2.45 / 3.90 / 16.47, rc.2 2.56 / 4.06 / 16.30.

The Buffered profile at 32 executors did not complete on rc.3: its two measured runs came in at 457
and 371 jobs/s against the control's 509, and the third stalled at 17 jobs/s until the ten-minute
cell timeout. That profile commits three times per job against SQLite's single writer and its p99
sits near twenty seconds when it completes, so a stall is the harness meeting SQLite's lock rather
than a claim-path effect, but it landed on one tree and not the other, so the cell was re-run on both
trees after the round. Both completed every run: rc.3 at 525, 391, and 426 jobs/s, median 426,
the figure rc.3 posted in its own earlier round; rc.2 at 457, 415, and 413, median 415. Level, and
the stall does not reproduce.

## Verdict

No Direct, Bulk, drain, enqueue, or query cell regresses against rc.2 beyond the machine's own
movement between two runs of the same tree, on any provider, and PostgreSQL and SQLite read ahead
on most of them. The SQL Server first pair is discarded as split across states and replaced by the
second. The round is the release gate for `1.0.0-rc.3`, beside the certification quartet in
`docs/certification`.
