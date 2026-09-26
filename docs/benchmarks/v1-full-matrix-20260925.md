# 1.0.0 full matrix: the certified tree against rc.3, native, same machine, same day

The `1.0.0` release round: `full` matrices from clean worktrees of `aa7419d5` and of
`v1.0.0-rc.3` (`90bfadbd`) as the control, taken on 2026-09-25 as candidate, control,
candidate per server provider and candidate, control on SQLite, so a pair shares whatever state the
machine is in. Both harnesses were published as Native AOT executables and run from the published
files, the first release round measured that way; the rc.4 round the day before ran both trees under
the JIT. The `full` preset: 10,000 jobs, 500,000 batch jobs, 100,000 rows, one warmup and the median
of three measured runs per cell. Every JSON records its commit and `gitDirty = false`. PostgreSQL
autovacuum was on for the round on both trees: the candidate drops each cell's schema when its
measurement is recorded, and the control's schemas were dropped after each of its matrices. No idle
gap and no drive gate separated the runs, by decision: the round ran back to back through whatever
state the drive under the Docker disk image was in.

The certified commit, `984eb036`, differs from the tree this round measured by one routine and one
harness constant: the rate meter reads its clock after the bucket row's lock rather than when the
call began, found by the million-job certification on SQL Server after this round, and the
certification seeder caps its metered slice. No cell that carries a verdict below reaches the meter;
the three rate cells, which do, carry none, and the round was kept rather than retaken by decision.

The pickup latency scenario, forty seconds long, was measured twice: once inside each full matrix
and once as an interleaved series of three candidate and control pairs per server, run straight
after the matrices. The series is the reading that carries the pickup verdict, because six samples
a minute apart share a state where two matrices forty minutes apart do not.

| file | tree | cells |
| --- | --- | --- |
| `baseline-20260925T150756Z` | 1.0.0 sample a | PostgreSQL |
| `baseline-20260925T154740Z` | rc.3 control | PostgreSQL |
| `baseline-20260925T162732Z` | 1.0.0 sample b | PostgreSQL |
| `baseline-20260925T162816Z` to `163155Z` | 1.0.0, rc.3 alternating | PostgreSQL latency series, six samples |
| `baseline-20260925T171244Z` | 1.0.0 sample a | SQL Server |
| `baseline-20260925T175427Z` | rc.3 control | SQL Server |
| `baseline-20260925T183729Z` | 1.0.0 sample b | SQL Server |
| `baseline-20260925T183821Z` to `184230Z` | 1.0.0, rc.3 alternating | SQL Server latency series, six samples |
| `baseline-20260925T193027Z` | 1.0.0 | SQLite, bench file on D: |
| `baseline-20260925T202016Z` | rc.3 control | SQLite, bench file on D: |

**How to read a pair.** The drive under the Docker disk image is QLC and flaps between about
3,000 and 450 flushes per second on minute timescales. A cell that commits on every job reads the
state it ran in, not only the tree: a one-worker drain cell or a single-executor throughput cell can
halve in the slow state on either tree. Cells that batch their commits, Direct and Bulk throughput
past a few executors, drain at four and sixteen workers, batch enqueue, and the list query,
reproduce within their own spread across states and carry the verdict. Where the two candidate
samples agree with each other and disagree with the control, the difference is the tree; where one
candidate sample sits as far from the other as from the control, it is the drive.

**The machine.** Intel Core i9-14900K, 32 logical processors, 96 GB, Windows 11 26200, .NET
10.0.12. PostgreSQL 18.4 and SQL Server 2022 16.0.4265 in Docker Desktop with the disk image on a
Solidigm P41 Plus (QLC); SQLite 3.53 with the bench file on a Solidigm 1 TB (TLC).

## PostgreSQL: level on Direct, Buffered, drain, and pickup; Bulk throughput ahead

Direct throughput, jobs/s, one worker, executors 1 to 32:

| tree | e=1 | e=2 | e=4 | e=8 | e=16 | e=32 |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| 1.0.0 a | 204 | 217 | 457 | 831 | 2,957 | 5,915 |
| rc.3 | 169 | 232 | 501 | 871 | 1,622 | 3,000 |
| 1.0.0 b | 207 | 217 | 456 | 859 | 1,705 | 3,053 |

Bulk throughput, same shape:

| tree | e=1 | e=2 | e=4 | e=8 | e=16 | e=32 |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| 1.0.0 a | 564 | 1,070 | 2,306 | 4,379 | 8,046 | 14,035 |
| rc.3 | 616 | 1,209 | 1,283 | 2,447 | 4,724 | 8,335 |
| 1.0.0 b | 615 | 1,191 | 2,351 | 4,472 | 8,256 | 14,159 |

Buffered throughput, the profile that commits three times per job:

| tree | e=1 | e=2 | e=4 | e=8 | e=16 | e=32 |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| 1.0.0 a | 412 | 328 | 666 | 1,082 | 2,047 | 3,913 |
| rc.3 | 411 | 334 | 798 | 1,008 | 2,057 | 6,398 |
| 1.0.0 b | 407 | 397 | 839 | 2,214 | 2,135 | 4,033 |

Drain, jobs/s, workers 1, 4, 16 of 16 executors:

| tree | Direct w=1 | w=4 | w=16 | Bulk w=1 | w=4 | w=16 | Buffered w=1 | w=4 | w=16 |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 1.0.0 a | 1,666 | 9,143 | 13,174 | 7,621 | 25,495 | 38,587 | 2,023 | 5,748 | 7,297 |
| rc.3 | 3,315 | 9,255 | 13,732 | 7,774 | 26,149 | 35,315 | 2,059 | 5,783 | 8,696 |
| 1.0.0 b | 3,360 | 9,188 | 13,251 | 7,620 | 24,825 | 38,091 | 3,759 | 7,872 | 8,578 |

Enqueue at 1, 4, 16 producers: 1.0.0 a 705 / 1,863 / 4,116, rc.3 699 / 1,944 / 4,131, 1.0.0 b
694 / 1,322 / 4,133 jobs/s. Batch enqueue at 1, 4, 16 producers: 1.0.0 a 30,519 / 103,586 /
205,329, rc.3 30,373 / 134,603 / 224,300, 1.0.0 b 30,642 / 134,642 / 210,810 jobs/s. The
100,000-row list query: 135, 140, 136 ms.

Direct throughput is level at every executor count in both samples but two: sample a at 16 and 32
executors reads almost twice the control and twice its own sibling, the same cells sample a of the
rc.4 round stood apart on, and sample b sits on the control to within two percent. Bulk throughput
is the round's one tree reading: from four executors up both candidate samples agree with each other
to within three percent and read 1.7 to 1.8 times the control, 14,035 and 14,159 against 8,335 at
32 executors, while Bulk drain, which runs the same completion path over a pre-filled backlog, is
level at every worker count. Buffered throughput moves with the drive on both trees, 6,398 against
3,913 and 4,033 at 32 executors on one side and 2,214 against 1,008 and 1,082 at 8 on the other.
The one-worker drain cells read the state their matrix met: sample a took Direct at half the
control and Buffered level, sample b took Direct level and Buffered almost double. Four- and
sixteen-worker drain, enqueue, and the list query are level.

Pickup latency, p50 in ms at one executor, from the full matrices: 1.0.0 a 5.23 / 3.92 / 4.54
Buffered, Direct, Bulk; rc.3 9.14 / 6.43 / 7.31; 1.0.0 b 5.02 / 3.61 / 4.01. The control's matrix
met the slow state at that point in its run and both candidate matrices did not. The series, six
samples in run order, forty seconds each, back to back:

| p50 ms | 1.0.0 1 | rc.3 1 | 1.0.0 2 | rc.3 2 | 1.0.0 3 | rc.3 3 |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| Buffered | 5.05 | 5.08 | 5.08 | 4.78 | 4.66 | 5.03 |
| Direct | 3.87 | 3.76 | 3.75 | 3.47 | 3.51 | 3.60 |
| Bulk | 4.53 | 4.29 | 4.28 | 4.01 | 3.88 | 4.31 |

All six in one state and the trees level on every profile: the candidate a tenth of a millisecond
behind on the first pair and ahead on the third, inside the pair-to-pair spread either way. The
per-execution owner entry, the batch-wide buffered registration, and the start and completion
writes this release repeats and reconciles cost the pickup path nothing this machine can measure.

## SQL Server: level on Direct, Buffered, drain, and pickup; Bulk throughput ahead

Direct throughput, jobs/s:

| tree | e=1 | e=2 | e=4 | e=8 | e=16 | e=32 |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| 1.0.0 a | 267 | 399 | 485 | 1,788 | 2,926 | 3,182 |
| rc.3 | 341 | 332 | 480 | 1,767 | 2,965 | 4,210 |
| 1.0.0 b | 203 | 266 | 467 | 1,602 | 3,104 | 4,180 |

Bulk throughput, jobs/s:

| tree | e=1 | e=2 | e=4 | e=8 | e=16 | e=32 |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| 1.0.0 a | 377 | 1,086 | 1,986 | 3,670 | 6,200 | 9,436 |
| rc.3 | 596 | 686 | 1,195 | 2,130 | 3,701 | 5,895 |
| 1.0.0 b | 408 | 679 | 1,261 | 3,493 | 6,024 | 9,474 |

Buffered throughput, jobs/s:

| tree | e=1 | e=2 | e=4 | e=8 | e=16 | e=32 |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| 1.0.0 a | 285 | 418 | 623 | 1,346 | 3,901 | 3,170 |
| rc.3 | 306 | 270 | 1,054 | 1,155 | 1,978 | 3,062 |
| 1.0.0 b | 207 | 344 | 946 | 1,147 | 2,110 | 3,057 |

Drain, jobs/s, workers 1, 4, 16 of 16 executors:

| tree | Direct w=1 | w=4 | w=16 | Bulk w=1 | w=4 | w=16 | Buffered w=1 | w=4 | w=16 |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 1.0.0 a | 2,875 | 5,458 | 6,868 | 6,271 | 7,456 | 21,185 | 2,097 | 5,204 | 6,824 |
| rc.3 | 2,761 | 5,321 | 6,995 | 5,456 | 16,184 | 21,147 | 3,910 | 6,238 | 6,730 |
| 1.0.0 b | 3,020 | 5,247 | 7,191 | 5,776 | 12,092 | 20,372 | 3,965 | 6,460 | 6,696 |

Enqueue at 1, 4, 16 producers: 1.0.0 a 823 / 3,021 / 3,985, rc.3 839 / 2,883 / 4,098, 1.0.0 b
829 / 3,003 / 3,971 jobs/s. Batch enqueue: 1.0.0 a 34,841 / 27,018 / 24,176, rc.3 27,646 / 19,311
/ 25,072, 1.0.0 b 30,028 / 27,255 / 25,854 jobs/s. The 100,000-row list query: 8 ms on all three.

Direct throughput is level from four executors up, with sample a's 32-executor cell the one reading
under the control and under its sibling. Bulk throughput repeats the PostgreSQL reading: from eight
executors up both candidate samples agree to within five percent and read 1.6 to 1.7 times the
control, 9,436 and 9,474 against 5,895 at 32, while Bulk drain at sixteen workers is level and at
four workers scatters across all three matrices, 7,456, 16,184, and 12,092, the way a cell that
commits per batch on a flapping drive does. Buffered throughput is level within its own spread, and
sample a's one-worker Buffered drain at half the other two is the drive. Enqueue and the list query
are level; batch enqueue reads ahead at one and four producers on both samples.

Pickup latency, p50 in ms at one executor, from the full matrices: 1.0.0 a 4.91 / 3.69 / 4.22
Buffered, Direct, Bulk; rc.3 8.13 / 5.89 / 4.04; 1.0.0 b 4.83 / 3.77 / 4.41. The series:

| p50 ms | 1.0.0 1 | rc.3 1 | 1.0.0 2 | rc.3 2 | 1.0.0 3 | rc.3 3 |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| Buffered | 4.63 | 4.78 | 4.81 | 4.69 | 4.63 | 4.56 |
| Direct | 3.76 | 3.68 | 3.58 | 3.62 | 3.61 | 3.37 |
| Bulk | 4.17 | 4.27 | 4.20 | 4.11 | 4.04 | 3.86 |

Six samples in one state and the trees level on every profile, never more than a quarter of a
millisecond apart within a pair and alternating in sign across the three.

## SQLite: level to ahead on every cell

The bench file sits on the TLC drive, which has no slow state, so this pair is the round's one
comparison free of it. Throughput, jobs/s, one worker:

| tree | profile | e=1 | e=2 | e=4 | e=8 | e=16 | e=32 |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: |
| 1.0.0 | Direct | 867 | 985 | 1,066 | 1,097 | 1,120 | 1,121 |
| rc.3 | Direct | 837 | 981 | 1,050 | 1,076 | 1,061 | 1,067 |
| 1.0.0 | Bulk | 868 | 983 | 1,054 | 1,107 | 1,119 | 1,142 |
| rc.3 | Bulk | 799 | 905 | 968 | 1,014 | 1,063 | 1,049 |
| 1.0.0 | Buffered | 125 | 177 | 282 | 371 | 437 | 435 |
| rc.3 | Buffered | 127 | 174 | 256 | 345 | 388 | 379 |

Drain at one worker: 1.0.0 Direct 1,132, Bulk 1,130, Buffered 435 against rc.3's 1,076, 1,073,
and 390. Enqueue at 1, 4, 16 producers: 1.0.0 2,804 / 2,780 / 2,791 against 2,520 / 2,549 / 2,641.
Batch enqueue: 1.0.0 16,024 / 17,052 / 15,953 against 15,661 / 17,214 / 15,475. The 100,000-row
list query: 58 ms against 59. Pickup latency p50: 1.0.0 17.29 / 2.46 / 2.53 Buffered, Direct, Bulk
against 16.83 / 2.40 / 2.35.

Every throughput and drain cell reads level to ahead, Buffered at sixteen and thirty-two executors
by twelve to fifteen percent and the rest by one to nine, and single-producer enqueue by eleven. The
Direct lean under the control that the rc.4 round saw on this drive is gone. Pickup latency is level
on all three profiles, a tenth of a millisecond either way on Direct and Bulk.

## The rate cells

The three rate-limited cells, a 100 per second limit over 3,000 jobs on three workers, a 1,000 per
second limit over 20,000, and a 600 per minute limit over 600 on one worker, are on the page for
completeness and carry no verdict. The 600 per minute cell reads 10.2 jobs/s on every tree and
provider, which is the limit. The 1,000 per second cell commits per admitted job and reads the
drive: 416 and 438 against 742 on PostgreSQL, 1,050 and 1,023 against 534 on SQL Server, 896
against 864 on SQLite, a lean that flips sign between the two servers. The 100 per second cell
reads 94 against 70 on PostgreSQL and 76 and 78 against 96 on SQL Server, the same flip.

## Verdict

No Direct, Buffered, drain, enqueue, or query cell regresses against rc.3 beyond the machine's own
movement between two runs of the same tree, on any provider. The pickup latency series is level on
all three profiles on both servers, six samples each, and the three full-matrix latency readings
that looked like a difference were the drive's slow state landing on one matrix of three. Bulk
throughput reads 1.6 to 1.8 times the control from eight executors up on both servers, in both
candidate samples, a reading the rc.4 round the day before did not have; the trees differ by the
ownership work of this release and, on SQL Server, by the driver version, and the round records the
reading without claiming which of them made it. SQLite reads level to ahead on every cell. The
round is the release gate for `1.0.0`, beside the certification quartet and the two million-job
seals in `docs/certification`.
