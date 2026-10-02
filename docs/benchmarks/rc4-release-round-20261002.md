# rc.4 release round: the candidate beside rc.3 on the same machine

The `1.0.0-rc.4` benchmark round on the certified commit `d616c72b`: the `release` preset (every
execution profile at the quick matrix's points, 5,000 jobs, one warmup and one measured run) once per
database. Both harnesses ran as Native AOT executables, PostgreSQL autovacuum on.

rc.3 numbers come from the same machine: PostgreSQL's from a run back to back with the candidate,
SQL Server's and SQLite's from the 2026-10-01 round, whose rc.3 runs reproduce today wherever a cell
is stable. Every cell below that looks like a finding was then rerun on both trees, back to back, with
the harness's `--scenario` filter; the reruns are quoted where they decide a cell. rc.3's harness
predates the `release` preset, so its worktree carried the preset as a local patch to
`anvil/Anvil.Bench/Baseline.cs` alone, which is why its files record `gitDirty = true`; the rc.3
engine is untouched. rc.3 has no lanes or claim-skew cells, so those are reported for the candidate
only.

| file | tree | provider | drive at start (flushes/s) |
| --- | --- | --- | ---: |
| `baseline-20261002T160453Z` | rc.4 `d616c72b` | PostgreSQL | 3,278 |
| `baseline-20261002T161319Z` | rc.3 `90bfadbd` | PostgreSQL | |
| `baseline-20261002T163347Z` | rc.4 `d616c72b` | SQL Server | 3,237 |
| `baseline-20261001T083050Z` | rc.3 `90bfadbd` | SQL Server | |
| `baseline-20261002T172838Z` | rc.4 `d616c72b` | SQLite, bench file on D: | 3,218 |
| `baseline-20261001T085933Z` | rc.3 `90bfadbd` | SQLite, bench file on D: | |

**How to read it.** One measured run per cell makes a single cell noisy, and some cells on this
machine are not one number at all. The end-to-end throughput cells at 1 and 8 executors land in one of
two modes per run, in both trees: rc.3's own binary, rerun alone, read Buffered 166, 165, 169 jobs/s at
one executor against the 408 it read inside the full run, and rc.4's read 171, 224, 165. SQL Server's
Bulk drain at sixteen workers read anywhere from 5,713 to 20,884 across twelve runs of six builds
between the 2026-10-01 candidate and this one, none of which changes the unlaned Bulk path. The verdict therefore reads drain, enqueue, latency, and the list query, and quotes the
throughput cells without weighing them.

## PostgreSQL: level, the list query a little slower

Drain, jobs/s, workers 1 and 16 of 16 executors:

| tree | Direct w=1 | w=16 | Bulk w=1 | w=16 | Buffered w=1 | w=16 |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| rc.4 | 1,496 | 9,846 | 4,148 | 29,007 | 1,968 | 6,762 |
| rc.3 | 1,523 | 10,320 | 3,930 | 28,481 | 1,954 | 6,693 |

Pickup latency at one executor, ms, p50 / p95 / p99: rc.4 Direct 6.44 / 7.65 / 8.48, Bulk 7.10 / 8.35 /
9.16, Buffered 9.08 / 10.56 / 12.00; rc.3 Direct 6.67 / 7.66 / 9.28, Bulk 7.31 / 8.68 / 9.51, Buffered
10.83 / 15.20 / 19.91. Single enqueue at 1 and 16 producers: rc.4 335 and 4,099 jobs/s, rc.3 341 and
4,152. Batch enqueue: rc.4 24,643 and 179,639, rc.3 28,332 and 172,556. The 50,000-row list query
p95: 87.1 ms against 82.2.

Drain, single enqueue, and latency are level, latency a little ahead on every profile. Batch enqueue
is level at sixteen producers and reads 13 percent below at one, a cell rc.4 itself read at 28,666 on
2026-10-01. The list query reads 6 percent slower here and read 12 percent slower in the 2026-10-01
round.
Throughput, jobs/s at 1, 8, 32 executors: rc.4 Buffered 163 / 1,034 / 3,571, Direct 166 / 1,621 /
4,787, Bulk 328 / 2,423 / 7,948; rc.3 Buffered 408 / 2,488 / 6,275, Direct 203 / 793 / 2,723, Bulk
315 / 2,275 / 7,711.

## SQL Server: level, one-worker drain a little slower

Drain from the full runs, jobs/s:

| tree | Direct w=1 | w=16 | Bulk w=1 | w=16 | Buffered w=1 | w=16 |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| rc.4 | 1,416 | 5,786 | 3,237 | 12,325 | 3,952 | 6,256 |
| rc.3 | 1,479 | 6,481 | 3,962 | 15,431 | 2,015 | 5,888 |

The drain cells were rerun twice on each tree, back to back, in the same hour: Direct at one worker
1,177 and 1,467 against 1,557 and 1,592; Direct at sixteen 5,591 and 4,964 against 6,130 and 5,770;
Buffered at one 1,831 and 1,805 against 2,046 and 2,024, at sixteen 5,972 and 5,673 against 6,099
and 5,862. Bulk at sixteen read 12,151 and 7,553 against 15,400 and 19,537, and the same cell, rerun
on six commits between the 2026-10-01 candidate and this one, read anywhere from 5,713 to 20,884, so
it carries no verdict.

Pickup latency p50: rc.4 Direct 4.04, Bulk 4.54, Buffered 5.30 ms; rc.3 6.21, 7.04, 9.30. Single
enqueue at 1 and 16 producers: 395 and 4,136 against 359 and 3,940. Batch enqueue at 1 producer: 34,167
against 36,083; at 16, 20,988 against 30,068, a cell that has read anywhere from 19,000 to 30,000 for
both trees across rounds. The list query p95: 29.4 ms against 26.9.

Latency and enqueue are level or ahead. One-worker drain reads a tenth to a sixth below rc.3 on Direct
and Buffered in the reruns, and one-worker Direct read below rc.3 in the 2026-10-01 and 2026-09-30
rounds too, so it is taken as real: lanes add work to every claim and settle, and one worker is where a
fixed cost per job shows most. Sixteen-worker drain is level within a few percent on Buffered and
reads about a tenth below on Direct. The list query reads 9 percent slower. Throughput, jobs/s at 1, 8,
32 executors: rc.4 Buffered 168 / 997 / 2,962, Direct 180 / 818 / 2,491, Bulk 594 / 3,801 / 7,362;
rc.3 Buffered 415 / 2,429 / 3,995, Direct 386 / 881 / 1,925, Bulk 344 / 2,106 / 5,441.

## SQLite: the cost of lanes, about an eighth

| tree | Direct e=1 | e=8 | e=32 | Bulk e=1 | e=8 | e=32 | drain Direct | Bulk | Buffered |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| rc.4 | 761 | 963 | 971 | 744 | 988 | 664 | 998 | 1,025 | 394 |
| rc.3 | 903 | 1,095 | 1,152 | 903 | 1,126 | 1,153 | 1,175 | 1,172 | 453 |

Single enqueue at 1 and 16 producers: 2,394 and 2,394 against 2,946 and 2,881; batch enqueue 23,100 and
22,892 against 20,174 and 21,851. Pickup latency p50 is level or ahead: Direct 2.01 against 2.30 ms,
Bulk 2.32 against 2.29. Buffered throughput is level at one and eight executors.

SQLite pays on every job: throughput and drain on Direct and Bulk read 12 to 18 percent below rc.3,
and single enqueue 17 to 19 percent, where the server providers absorb the same statements. The
2026-10-01 and 2026-09-30 rounds measured the same gap, so it is the cost, not the drive. Batch enqueue
is 5 to 15 percent faster. Bulk at 32 executors read 664 in this run against 987 for the 2026-10-01
candidate; SQLite runs Bulk as Direct, and Direct at 32 executors is in line, so the cell is taken as
the run.

## Lanes and the claim beside a skewed backlog

New in this release, so the candidate only. Lanes, jobs/s at 8 executors: unlaned, a thousand lanes
round-robin, one hot concurrency key (limit 1), and one lane holding every job.

| provider | profile | unlaned | lane-wide | key-hot | lane-deep |
| --- | --- | ---: | ---: | ---: | ---: |
| PostgreSQL | Direct | 2,054 | 1,794 | 22 | 331 |
| PostgreSQL | Bulk | 4,501 | 4,220 | 21 | 4 |
| SQL Server | Direct | 1,395 | 1,582 | 24 | 276 |
| SQL Server | Bulk | 3,674 | 2,941 | 24 | 4 |
| SQLite | Direct | 990 | 938 | 856 | 728 |
| SQLite | Bulk | 983 | 918 | 873 | 699 |

The SQLite rows are a rerun of the lanes cells alone: the full run read Direct lane-wide 568 and
key-hot 521, two reruns read 905 and 938, and 875 and 856, in line with the 2026-10-01 round's 961 and
866.

A thousand lanes run at 80 to 95 percent of unlaned work, and SQL Server Direct read above its unlaned
cell in this run. One lane is serial by design, 276 to 331 jobs/s on the servers with Direct, while a
hot key with limit 1 manages about twenty, because every waiting job is claimed, bounced, and re-armed;
a lane's followers are never claimed at all. The Bulk deep lane runs four a second on the servers
because each handoff waits for the completion flush, which is why a deep lane belongs on Direct or
Buffered. The serial cells run a thousand jobs, the Bulk deep lane fifty, the rest 5,000.

The claim-skew cells drain due jobs alone, beside 50,000 parked waits, and beside 200,000 delayed jobs
in a higher band. PostgreSQL drains all three within a few percent of each other on Buffered and
Direct and within about a tenth on Bulk, 8,018, 7,170, and 7,552 jobs/s. SQL Server reads up to a fifth
lower beside the delayed band on Direct and Bulk (2,195 against 2,665; 4,758 against 5,615), and SQLite
is level on Direct and Bulk.

## Verdict

No drain, enqueue, latency, or list-query cell on PostgreSQL moves beyond a single run's spread except
the list query, about 6 percent slower. SQL Server is level on latency, enqueue, and Buffered drain at
sixteen workers, and reads a tenth to a sixth slower on Direct drain, one-worker Buffered drain, and the
list query. SQLite pays about an
eighth on its per-job paths, where lanes add work to every claim and settle, and gains on batch
enqueue. The round is the benchmark gate for `1.0.0-rc.4`, beside the certification seals in
`docs/certification`.
