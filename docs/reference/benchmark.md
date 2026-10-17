# Benchmark Baseline: release

- Captured: 2026-10-17T14:41:01Z
- Commit: ba12ef95
- Engine: 1.0.0-rc.4.471+ba12ef95ce96fcba8479252b6c45da914803c953
- Runtime: .NET 10.0.12
- OS: Microsoft Windows 10.0.26300
- CPU: Intel(R) Core(TM) i9-14900K (32 logical)
- RAM: 95.8 GB
- Disk: NVMe SOLIDIGM SSDPFKKW010X7 [SSD]; NVMe SOLIDIGM SSDPFKKW020X7 [SSD]
- Policy: 1 warmup, 1 measured run
- Providers: mssql, pg, sqlite

## Throughput: end-to-end jobs/s (higher is better)

One worker process; executors swept per worker. Cells are median jobs/s.

| provider | e=1 | e=8 | e=32 |
| --- | ---: | ---: | ---: |
| mssql | 624 | 3299 | 8043 |
| pg | 698 | 4760 | 16963 |
| sqlite | 1174 | 3647 | 4001 |

Peak on mssql: 8043 jobs/s (32 executors).
Peak on pg: 16963 jobs/s (32 executors).
Peak on sqlite: 4001 jobs/s (32 executors).

## Drain: sustained jobs/s (higher is better)

A preloaded backlog drained by N worker processes, 64 executors each, several workers spread over 2 namespaces. Cells are median jobs/s.

| provider | w=1 | w=16 |
| --- | ---: | ---: |
| mssql | 7011 | 25288 |
| pg | 12586 | 66405 |
| sqlite | 5643 | - |

Peak on mssql: 25288 jobs/s (16 workers).
Peak on pg: 66405 jobs/s (16 workers).
Peak on sqlite: 5643 jobs/s (1 worker).

## Latency: per-job round-trip ms (lower is better)

One executor, one job at a time.

| provider | p50 | p95 | p99 |
| --- | ---: | ---: | ---: |
| mssql | 3.68 | 4.66 | 6.33 |
| pg | 4.15 | 4.94 | 5.65 |
| sqlite | 19.63 | 23.45 | 32.67 |

## Enqueue: single-call fleet insert jobs/s (higher is better)

N concurrent producers each enqueue one job at a time into the one namespace; worker idle (no draining).

| provider | producers | jobs/s | p95 ms/call |
| --- | ---: | ---: | ---: |
| mssql | 1 | 765 | 1.58 |
| mssql | 16 | 7476 | 2.66 |
| pg | 1 | 752 | 1.61 |
| pg | 16 | 7923 | 2.44 |
| sqlite | 1 | 1400 | 0.81 |
| sqlite | 16 | 1412 | 0.79 |

## Enqueue (batch): fleet insert jobs/s (higher is better)

N concurrent producers each batch-enqueue their share into the one namespace; worker idle (no draining).

| provider | producers | jobs/s |
| --- | ---: | ---: |
| mssql | 1 | 39786 |
| mssql | 16 | 18705 |
| pg | 1 | 32188 |
| pg | 16 | 238839 |
| sqlite | 1 | 27825 |
| sqlite | 16 | 27349 |

## Query: job-list read ms (lower is better)

Paged job-list read over a retained backlog.

| provider | retained rows | p50 | p95 | p99 |
| --- | ---: | ---: | ---: | ---: |
| mssql | 50000 | 7.73 | 30.42 | 109.23 |
| pg | 50000 | 85.27 | 90.78 | 91.97 |
| sqlite | 50000 | 0.48 | 1.28 | 8.16 |

## Rate limit: what the meter admitted

A backlog released at once against one meter. Admitted jobs/s is measured over the first-to-last admission span.
Excess is max(admissions in the window) - (R*(T+W) + burst), W being how long a booked turn stays valid: one second, or one interval when longer. At most 0 is inside the contract.
Re-arms/job counts the turns each job had to book before it ran.

| provider | rate | jobs | workers | side jobs | admitted/s | max 1s | excess 1s | excess 5s | excess 10s | re-arms/job |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| mssql | 600/m | 600 | 1 | 0 | 9.4 | 19 | -11 | -12 | -12 | 0.96 |
| mssql | 100/s | 3000 | 3 | 0 | 96.8 | 194 | -106 | -106 | -101 | 1.00 |
| pg | 600/m | 600 | 1 | 0 | 10.2 | 19 | -11 | -11 | -11 | 0.98 |
| pg | 100/s | 3000 | 3 | 0 | 69.3 | 180 | -120 | -120 | -121 | 1.41 |
| sqlite | 600/m | 600 | 1 | 0 | 9.5 | 19 | -11 | -11 | -11 | 1.05 |
| sqlite | 100/s | 3000 | 3 | 0 | 95.0 | 191 | -109 | -132 | -144 | 0.01 |

## Lanes: end-to-end jobs/s and pickup ms

Enqueue and drain overlapped at 8 executors: unlaned jobs, 1000 lanes round-robin, one hot concurrency key (limit 1), and one lane holding every job. Pickup is enqueue to handler entry.

| provider | cell | jobs | jobs/s | p50 | p99 | status |
| --- | --- | ---: | ---: | ---: | ---: | --- |
| mssql | unlaned | 5000 | 3947 | 593.4 | 1092.8 | ok |
| mssql | lane-wide | 5000 | 3215 | 705.9 | 989.8 | ok |
| mssql | key-hot | 1000 | 16 | 10402.0 | 52519.4 | ok |
| mssql | lane-deep | 1000 | 231 | 2226.5 | 4292.3 | ok |
| pg | unlaned | 5000 | 4261 | 562.3 | 1066.2 | ok |
| pg | lane-wide | 5000 | 4116 | 568.4 | 1023.2 | ok |
| pg | key-hot | 1000 | 17 | 6662.5 | 48754.7 | ok |
| pg | lane-deep | 1000 | 245 | 2105.9 | 4053.5 | ok |
| sqlite | unlaned | 5000 | 3427 | 751.6 | 1272.8 | ok |
| sqlite | lane-wide | 5000 | 3391 | 757.1 | 1268.8 | ok |
| sqlite | key-hot | 1000 | 168 | 1063.2 | 5737.2 | ok |
| sqlite | lane-deep | 1000 | 60 | 8431.7 | 16661.9 | ok |

## Claim skew: drain jobs/s and pickup ms

10000 due jobs drained at 16 executors alone (none), beside 50000 Suspended jobs parked on an unbounded wait in the due jobs' band (parked), or behind 200000 High jobs due tomorrow ahead of due Bulk jobs (delayed-high). Pickup is due instant to handler entry.

| provider | cell | jobs | jobs/s | p50 | p99 | status |
| --- | --- | ---: | ---: | ---: | ---: | --- |
| mssql | none | 10000 | 6186 | 771.0 | 1611.0 | ok |
| mssql | parked | 10000 | 5732 | 885.7 | 1739.3 | ok |
| mssql | delayed-high | 10000 | 5088 | 1097.5 | 1951.9 | ok |
| pg | none | 10000 | 8750 | 633.3 | 1134.3 | ok |
| pg | parked | 10000 | 9518 | 556.1 | 1040.5 | ok |
| pg | delayed-high | 10000 | 9937 | 516.9 | 1004.5 | ok |
| sqlite | none | 10000 | 4804 | 1043.4 | 2085.7 | ok |
| sqlite | parked | 10000 | 4376 | 1175.2 | 2279.8 | ok |
| sqlite | delayed-high | 10000 | 4568 | 1128.1 | 2197.2 | ok |

