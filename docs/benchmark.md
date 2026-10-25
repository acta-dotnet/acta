# Acta Bench: rc.6 release evidence

The rc.6 engine, `src/` at `599e28e8`, under the Acta Bench full profile on SQLite, PostgreSQL, and SQL Server. The evidence is seven bundles from four bench commits; the bench changes between them touch only `tools/Acta.Bench` and its tests. From `05e5c27c`, a case where no rate holds at 0.8 of its discovered rate is measured again at 0.7, 0.6, and 0.5 of it, in turn, until one holds. Each section below is a bundle's own report as `acta-bench publish` writes it, with its headings moved two levels down.

| Section | Bench commit | Run | Covers |
|---|---|---|---|
| 1 | `599e28e8` | `20261025T084709291Z-ccfc5a01088f4cbdb63e9ca59f6131e6` | SQLite and SQL Server. Superseded: its PostgreSQL run, stopped by the bench's memory guard at M04 (sections 2 and 7); its SQL Server F09 (section 3); its SQLite P01 and its SQL Server M02, M04, O02, O03, and P01 (sections 5 and 6). |
| 2 | `fb8f8414` | `20261025T171642830Z-10ff9a58c7ae475aab00670be4a4a073` | PostgreSQL, with 1,000,000 chaos jobs. Superseded: its F09 (section 4) and its L01, C01, P01, I05, M04, O03, E01, and E03 (section 7). |
| 3 | `988764e6` | `20261025T212820450Z-7717e7b27a80435c9b87b0884064657f` | F09 on SQL Server, 1,000,000 jobs. |
| 4 | `988764e6` | `20261025T215626759Z-e52c64f2055544aa81bc5754dda63c14` | F09 on PostgreSQL, 1,000,000 jobs. |
| 5 | `05e5c27c` | `20261025T224336926Z-4223d27b87664c29ba0a7158fcaeda67` | P01 on SQLite. |
| 6 | `05e5c27c` | `20261025T224812128Z-ad7e90b325424ee6acbd4810822c5bb8` | M02, M04, O02, O03, and P01 on SQL Server. |
| 7 | `05e5c27c` | `20261025T234949411Z-7714ef9c9e834ecfb24afe4a626a9637` | L01, C01, P01, I05, M04, O03, E01, and E03 on PostgreSQL, and M03, which E01 takes its rate from. |

- Correctness: 0 safety failures and 0 liveness failures in every bundle, and no ledger violation in any trial.
- The chaos certification passed at 1,000,000 jobs on PostgreSQL and SQL Server (sections 3 and 4) and at 10,000 jobs on SQLite (section 1).
- One PostgreSQL worker validated at 7,680 no-op jobs per second end to end (L01, section 7).
- Still INCONCLUSIVE, with correctness passed: O03 on PostgreSQL and SQL Server, and I05 and the E01 soak on PostgreSQL. Each report lists its own under Not passing, along with the runs later sections supersede.

## 1. SQLite and SQL Server, `599e28e8`

### Acta Bench: INCONCLUSIVE

full profile on sqlite, pg, mssql. 37 of 886 case results did not pass (0 safety failures, 0 liveness failures, 37 coverage gaps); they are listed under Not passing.

#### L01: No-op end-to-end

| Provider | Cell | Offered roots/s | Measurement | Rate, median (min-max) | Unit | End-to-end p50 ms | End-to-end p99 ms |
|---|---|---:|---|---:|---|---:|---:|
| mssql | canonical | 1,280 | 3 VALID | 1,267 (1,263-1,291) | roots/second | 524 | 1,037 |
| mssql | executor-1 | 1,280 | 5 UNSTABLE; 2 added by the instability rule | 330 (295-461) | roots/second | 76,423 | 122,414 |
| mssql | executor-32 | 1,280 | 3 VALID | 1,290 (1,250-1,303) | roots/second | 521 | 1,043 |
| mssql | workers-16 | 1,280 | 1 GENERATOR_LIMITED, 2 VALID | 1,286 (1,282-1,286) | roots/second | 116 | 566 |
| mssql | workers-16-single-namespace | 1,280 | 1 GENERATOR_LIMITED, 4 VALID; 2 added by the instability rule | 1,281 (1,279-1,281) | roots/second | 86.4 | 486 |
| mssql | workers-4 | 1,280 | 1 GENERATOR_LIMITED, 2 VALID | 1,272 (1,262-1,285) | roots/second | 427 | 1,010 |
| pg | canonical | 5,120 | 3 VALID | 5,120 (5,099-5,213) | roots/second | 530 | 1,032 |
| pg | executor-1 | 5,120 | 3 UNSTABLE | 245 (233-246) | roots/second | 451,453 | 711,481 |
| pg | executor-32 | 5,120 | 3 VALID | 5,079 (4,975-5,185) | roots/second | 533 | 1,032 |
| pg | workers-16 | 5,120 | 1 GENERATOR_LIMITED, 2 VALID | 5,200 (5,098-5,204) | roots/second | 413 | 951 |
| pg | workers-16-single-namespace | 5,120 | 5 VALID; 2 added by the instability rule | 5,104 (5,065-5,204) | roots/second | 85 | 641 |
| pg | workers-4 | 5,120 | 3 VALID | 5,128 (5,115-5,255) | roots/second | 494 | 1,015 |
| sqlite | canonical | 40 | 3 VALID | 40.8 (39.9-40.9) | roots/second | 597 | 1,101 |
| sqlite | executor-1 | 40 | 3 VALID | 39.7 (39.5-39.8) | roots/second | 691 | 1,188 |
| sqlite | executor-32 | 40 | 3 VALID | 40.3 (40.1-40.8) | roots/second | 597 | 1,107 |

#### L02: Single-call admission

| Provider | Cell | Offered roots/s | Measurement | Rate, median (min-max) | Unit | Admission p50 ms | Admission p99 ms |
|---|---|---:|---|---:|---|---:|---:|
| mssql | canonical | 5,120 | 5 VALID; 2 added by the instability rule | 5,120 (5,119-5,140) | admissions/second | 17.7 | 61.7 |
| pg | canonical | 10,240 | 5 VALID; 2 added by the instability rule | 10,239 (10,229-10,243) | admissions/second | 18.1 | 36.3 |
| sqlite | canonical | 40 | 3 VALID | 40 (40-40) | admissions/second | 11 | 19.6 |

#### L03: Batch admission

| Provider | Cell | Offered roots/s | Measurement | Rate, median (min-max) | Unit | Admission p50 ms | Admission p99 ms |
|---|---|---:|---|---:|---|---:|---:|
| mssql | batch-1 | 2,560 | 3 GENERATOR_LIMITED | 406 (368-446) | admissions/second | 2,467 | 2,998 |
| mssql | batch-1000 | 2,560 | 3 VALID | 2,547 (2,547-2,547) | admissions/second | 65.5 | 179 |
| mssql | canonical | 2,560 | 3 VALID | 2,560 (2,560-2,560) | admissions/second | 20.1 | 86.8 |
| pg | batch-1 | 2,560 | 3 GENERATOR_LIMITED | 367 (351-376) | admissions/second | 2,794 | 2,934 |
| pg | batch-1000 | 2,560 | 5 VALID; 2 added by the instability rule | 2,547 (2,547-2,547) | admissions/second | 63.4 | 89.7 |
| pg | canonical | 2,560 | 1 GENERATOR_LIMITED, 4 VALID; 2 added by the instability rule | 2,560 (2,560-2,560) | admissions/second | 27.6 | 41.4 |
| sqlite | batch-1 | 10,240 | 3 GENERATOR_LIMITED | 919 (910-1,137) | admissions/second | 800 | 2,562 |
| sqlite | batch-1000 | 10,240 | 3 VALID | 10,253 (10,253-10,253) | admissions/second | 61.1 | 84.1 |
| sqlite | canonical | 10,240 | 5 VALID; 2 added by the instability rule | 10,240 (10,237-10,240) | admissions/second | 17.9 | 30.2 |

#### L04: Preloaded backlog drain

| Provider | Cell | Workers | Roots | Measurement | Database steady roots/s | Steady roots/s | Drain roots/s, median (min-max) | First completion s |
|---|---|---:|---:|---|---:|---:|---:|---:|
| mssql | canonical | 1 | 200,000 | 5 VALID; 2 added by the instability rule | 11,517 | 11,524 | 10,711 (8,983-14,149) | 0.677 |
| mssql | drain-workers-16 | 16 | 200,000 | 3 VALID | 41,783 | 41,054 | 34,033 (32,051-34,124) | 0.865 |
| mssql | drain-workers-4 | 4 | 200,000 | 3 VALID | 34,159 | 34,168 | 30,334 (29,241-30,707) | 0.678 |
| mssql | drain-workers-8 | 8 | 200,000 | 5 VALID; 2 added by the instability rule | 43,526 | 43,384 | 35,836 (32,611-39,912) | 0.761 |
| pg | canonical | 1 | 200,000 | 3 VALID | 20,743 | 20,739 | 19,299 (18,905-19,380) | 0.569 |
| pg | drain-workers-16 | 16 | 200,000 | 3 VALID | 60,328 | 52,190 | 42,605 (40,582-47,404) | 0.934 |
| pg | drain-workers-4 | 4 | 200,000 | 3 VALID | 54,397 | 54,401 | 45,972 (45,961-48,913) | 0.582 |
| pg | drain-workers-8 | 8 | 200,000 | 3 VALID | 74,908 | 74,411 | 59,508 (51,863-59,545) | 0.597 |
| sqlite | canonical | 1 | 50,000 | 3 VALID | 5,837 | 5,786 | 5,458 (5,420-5,604) | 0.515 |

#### C01: Three persisted steps

| Provider | Cell | Offered roots/s | Measurement | Rate, median (min-max) | Unit | End-to-end p50 ms | End-to-end p99 ms |
|---|---|---:|---|---:|---|---:|---:|
| mssql | canonical | 1,280 | 3 VALID | 1,254 (1,244-1,278) | roots/second | 569 | 1,119 |
| pg | canonical | 1,280 | 3 VALID | 1,278 (1,253-1,288) | roots/second | 569 | 1,089 |
| sqlite | canonical | 20 | 3 VALID | 19.9 (19.9-19.9) | roots/second | 901 | 2,238 |

#### P01: Ten children and join

| Provider | Cell | Offered roots/s | Measurement | Rate, median (min-max) | Unit | End-to-end p50 ms | End-to-end p99 ms |
|---|---|---:|---|---:|---|---:|---:|
| mssql | canonical | 320 | 5 UNSTABLE; 2 added by the instability rule | 248 (247-280) | roots/second | 9,148 | 11,238 |
| pg | canonical | 320 | 4 UNSTABLE, 1 VALID; 2 added by the instability rule | 276 (268-330) | roots/second | 6,574 | 8,209 |
| sqlite | canonical | 20 | 1 UNSTABLE, 2 VALID | 19.9 (19.7-21.2) | roots/second | 2,350 | 8,209 |

#### R01: Transient failure and retry

| Provider | Cell | Offered roots/s | Measurement | Rate, median (min-max) | Unit | End-to-end p50 ms | End-to-end p99 ms |
|---|---|---:|---|---:|---|---:|---:|
| mssql | canonical | 2,560 | 3 VALID | 2,559 (2,556-2,560) | roots/second | 53.9 | 2,214 |
| pg | canonical | 5,120 | 3 VALID | 5,121 (5,120-5,126) | roots/second | 36.7 | 2,166 |
| sqlite | canonical | 40 | 3 VALID | 40.4 (40.1-40.5) | roots/second | 146 | 2,549 |

#### O01: Many independent lanes

| Provider | Cell | Offered roots/s | Measurement | Rate, median (min-max) | Unit | End-to-end p50 ms | End-to-end p99 ms |
|---|---|---:|---|---:|---|---:|---:|
| mssql | canonical | 1,280 | 3 VALID | 1,301 (1,275-1,305) | roots/second | 494 | 1,043 |
| pg | canonical | 5,120 | 3 VALID | 5,145 (5,139-5,210) | roots/second | 536 | 1,032 |
| sqlite | canonical | 40 | 3 VALID | 41 (40.9-41) | roots/second | 597 | 1,101 |

#### O02: One hot lane

| Provider | Cell | Offered roots/s | Measurement | Rate, median (min-max) | Unit | End-to-end p50 ms | End-to-end p99 ms |
|---|---|---:|---|---:|---|---:|---:|
| mssql | canonical | 160 | 5 UNSTABLE; 2 added by the instability rule | 132 (129-154) | roots/second | 6,828 | 8,522 |
| pg | canonical | 80 | 3 VALID | 79.5 (78.6-80.8) | roots/second | 508 | 1,021 |
| sqlite | canonical | 20 | 3 VALID | 20.2 (20.2-20.3) | roots/second | 581 | 1,101 |

#### O03: Hot concurrency key

| Provider | Cell | Offered roots/s | Measurement | Rate, median (min-max) | Unit | End-to-end p50 ms | End-to-end p99 ms |
|---|---|---:|---|---:|---|---:|---:|
| mssql | canonical | - | 1 NOT_MEASURED | - |  | - | - |
| pg | canonical | - | 1 NOT_MEASURED | - |  | - | - |
| sqlite | canonical | 1 | 5 VALID; 2 added by the instability rule | 1 (1-1) | roots/second | 346 | 768 |

#### S02: Parked-work skew

| Provider | Cell | Offered roots/s | Measurement | Rate, median (min-max) | Unit | End-to-end p50 ms | End-to-end p99 ms |
|---|---|---:|---|---:|---|---:|---:|
| mssql | canonical | 640 | 3 VALID | 640 (635-641) | roots/second | 508 | 1,032 |
| pg | canonical | 5,120 | 3 VALID | 5,073 (5,062-5,151) | roots/second | 536 | 1,032 |
| sqlite | canonical | 40 | 3 VALID | 39.6 (39.2-41) | roots/second | 597 | 1,101 |

#### S03: Future high-priority skew

| Provider | Cell | Offered roots/s | Measurement | Rate, median (min-max) | Unit | End-to-end p50 ms | End-to-end p99 ms |
|---|---|---:|---|---:|---|---:|---:|
| mssql | canonical | 320 | 3 VALID | 325 (325-327) | roots/second | 513 | 1,021 |
| pg | canonical | 5,120 | 3 VALID | 5,070 (5,006-5,128) | roots/second | 542 | 1,032 |
| sqlite | canonical | 40 | 3 VALID | 39.5 (39.5-40.8) | roots/second | 594 | 1,101 |

#### I05: Idempotent external sink

| Provider | Cell | Offered roots/s | Measurement | Rate, median (min-max) | Unit | End-to-end p50 ms | End-to-end p99 ms |
|---|---|---:|---|---:|---|---:|---:|
| mssql | canonical | 320 | 3 VALID | 320 (319-321) | roots/second | 655 | 1,137 |
| pg | canonical | 320 | 5 UNSTABLE; 2 added by the instability rule | 313 (306-316) | roots/second | 1,591 | 2,008 |
| sqlite | canonical | 9 | 3 VALID | 9 (8.9-9.13) | roots/second | 762 | 1,338 |

#### M01: Fixed CPU work

| Provider | Cell | Offered roots/s | Measurement | Rate, median (min-max) | Unit | End-to-end p50 ms | End-to-end p99 ms |
|---|---|---:|---|---:|---|---:|---:|
| mssql | canonical | 2,560 | 3 VALID | 2,558 (2,532-2,627) | roots/second | 116 | 1,054 |
| pg | canonical | 5,120 | 3 VALID | 5,043 (4,970-5,094) | roots/second | 406 | 1,043 |
| sqlite | canonical | 40 | 3 VALID | 40.9 (40.9-41) | roots/second | 597 | 1,101 |

#### M02: Controlled I/O work

| Provider | Cell | Offered roots/s | Measurement | Rate, median (min-max) | Unit | End-to-end p50 ms | End-to-end p99 ms |
|---|---|---:|---|---:|---|---:|---:|
| mssql | canonical | 640 | 5 UNSTABLE; 2 added by the instability rule | 440 (435-607) | roots/second | 11,177 | 14,574 |
| pg | canonical | 640 | 5 UNSTABLE; 2 added by the instability rule | 310 (305-456) | roots/second | 27,912 | 39,473 |
| sqlite | canonical | 9 | 3 VALID | 8.93 (8.93-9.03) | roots/second | 745 | 1,295 |

#### M03: Short/long mixed workload

| Provider | Cell | Offered roots/s | Measurement | Rate, median (min-max) | Unit | End-to-end p50 ms | End-to-end p99 ms |
|---|---|---:|---|---:|---|---:|---:|
| mssql | canonical | 640 | 3 VALID | 640 (639-641) | roots/second | 106 | 1,181 |
| pg | canonical | 640 | 3 VALID | 640 (638-640) | roots/second | 104 | 1,220 |
| sqlite | canonical | 20 | 3 VALID | 19.9 (19.9-20.1) | roots/second | 250 | 1,549 |

#### M04: Payload sweep

| Provider | Cell | Offered roots/s | Measurement | Rate, median (min-max) | Unit | End-to-end p50 ms | End-to-end p99 ms |
|---|---|---:|---|---:|---|---:|---:|
| mssql | canonical | 1,280 | 3 GENERATOR_LIMITED | 1,265 (1,248-1,282) | roots/second | 420 | 1,077 |
| mssql | payload-256 | 1,280 | 1 GENERATOR_LIMITED, 2 VALID | 1,276 (1,267-1,299) | roots/second | 460 | 1,049 |
| mssql | payload-4096 | 1,280 | 1 GENERATOR_LIMITED, 2 VALID | 1,283 (1,250-1,295) | roots/second | 510 | 1,037 |
| mssql | payload-65536 | 1,280 | 2 GENERATOR_LIMITED, 1 VALID | 1,285 (1,266-1,317) | roots/second | 402 | 1,113 |
| pg | canonical | 2,560 | 4 GENERATOR_LIMITED, 1 VALID; 2 added by the instability rule | 2,561 (2,452-2,607) | roots/second | 547 | 1,188 |
| pg | payload-256 | 2,560 | 3 VALID | 2,581 (2,541-2,601) | roots/second | 521 | 1,021 |
| pg | payload-4096 | 2,560 | 3 VALID | 2,525 (2,523-2,527) | roots/second | 524 | 1,021 |
| pg | payload-65536 | 2,560 | 1 GENERATOR_LIMITED | 1,357 (1,357-1,357) | roots/second | 14,574 | 17,316 |
| sqlite | canonical | 40 | 3 VALID | 40.9 (39.5-40.9) | roots/second | 600 | 1,101 |
| sqlite | payload-256 | 40 | 3 VALID | 40.6 (39.5-40.7) | roots/second | 597 | 1,101 |
| sqlite | payload-4096 | 40 | 3 VALID | 40.6 (39.6-40.9) | roots/second | 597 | 1,101 |
| sqlite | payload-65536 | 40 | 3 VALID | 40.9 (39.8-41) | roots/second | 600 | 1,107 |

#### E01: Mixed-workload soak

| Provider | Cell | Offered roots/s | Measurement | Rate, median (min-max) | Unit | End-to-end p50 ms | End-to-end p99 ms |
|---|---|---:|---|---:|---|---:|---:|
| mssql | canonical | 448 | 1 VALID | 448 (448-448) | roots/second | 140 | 1,125 |
| sqlite | canonical | 14 | 1 VALID | 14 (14-14) | roots/second | 326 | 1,882 |

#### E02: Queries during processing

| Provider | Cell | Offered roots/s | Measurement | Rate, median (min-max) | Unit | End-to-end p50 ms | End-to-end p99 ms |
|---|---|---:|---|---:|---|---:|---:|
| mssql | canonical | 2,560 | 3 VALID | 2,611 (2,553-2,632) | roots/second | 369 | 1,043 |
| sqlite | canonical | 40 | 3 VALID | 40.6 (40.1-40.8) | roots/second | 600 | 1,101 |

#### Overhead

| Measure | Value |
|---|---:|
| Run wall time | 28974 s |
| Evidence written | 82.2 MB of a 50 MB budget |
| Setup, median per trial | 0.722 s |
| Seeding, median per trial | 5E-07 s |
| Measured window and drain, median per trial | 17.4 s |
| Verification, median per trial | 0.0742 s |
| Ledger checks, median per trial | 0.0715 s |
| Evidence writing, median per trial | 0.00206 s |

| Disk, by phase and drive | Windows counters |
|---|---|
| before | 0 c: | write 2.52 ms mean, 7.73 ms p95, 10 writes/s, 99% idle, 14 samples |
| before | 1 d: | write 4.19 ms mean, 6.93 ms p95, 5 writes/s, 100% idle, 14 samples |
| mssql | 0 c: | write 3.47 ms mean, 7.83 ms p95, 9 writes/s, 99% idle, 5314 samples |
| mssql | 1 d: | write 0.37 ms mean, 2.33 ms p95, 1345 writes/s, 63% idle, 5314 samples |
| pg | 0 c: | write 3.47 ms mean, 7.86 ms p95, 8 writes/s, 99% idle, 5182 samples |
| pg | 1 d: | write 0.16 ms mean, 0.57 ms p95, 897 writes/s, 54% idle, 5182 samples |
| sqlite | 0 c: | write 3.21 ms mean, 7.85 ms p95, 8 writes/s, 99% idle, 3903 samples |
| sqlite | 1 d: | write 0.29 ms mean, 1.64 ms p95, 480 writes/s, 92% idle, 3903 samples |
| after | 0 c: | write 4.22 ms mean, 8.55 ms p95, 5 writes/s, 101% idle, 15 samples |
| after | 1 d: | write 1.68 ms mean, 7.31 ms p95, 7 writes/s, 100% idle, 15 samples |

| Slowest cases | Provider | Wall time s | Results |
|---|---|---:|---:|
| L01 | pg | 3,159 | 30 |
| L01 | mssql | 1,675 | 30 |
| L03 | sqlite | 1,382 | 22 |
| M04 | pg | 909 | 21 |
| S02 | sqlite | 901 | 7 |
| O03 | pg | 811 | 9 |
| M04 | mssql | 804 | 20 |
| F09 | sqlite | 766 | 1 |
| O03 | mssql | 765 | 9 |
| L03 | pg | 742 | 22 |

#### Not passing

| Case | Provider | Phase | Trial | Status | Safety | Liveness | Coverage | Measurement | Reason | Evidence |
|---|---|---|---:|---|---|---|---|---|---|---|
| P01 | sqlite | reconcile | 0 | INCONCLUSIVE | UNVERIFIED | UNVERIFIED | INCOMPLETE | NOT_MEASURED | No arrival rate passed all required independent validation trials; correctness results remain separate. |  |
| O03 | pg | measure | 0 | INCONCLUSIVE | UNVERIFIED | UNVERIFIED | INCOMPLETE | NOT_MEASURED | Discovery did not establish a valid rate; no capacity score is available. |  |
| F09 | pg | verify | 0 | INCONCLUSIVE | UNVERIFIED | UNVERIFIED | INCOMPLETE | NOT_MEASURED | Case did not finish (InvalidOperationException: Owned worker exited before readiness.); partial histories retained. | histories/pg-F09-chaos-ledger.json |
| D01 | pg | verify | 0 | INCONCLUSIVE | UNVERIFIED | UNVERIFIED | INCOMPLETE | NOT_MEASURED | The resource budget stopped this provider: Declared memory/disk resource budget exceeded; evidence retained. |  |
| D02 | pg | verify | 0 | INCONCLUSIVE | UNVERIFIED | UNVERIFIED | INCOMPLETE | NOT_MEASURED | The resource budget stopped this provider: Declared memory/disk resource budget exceeded; evidence retained. |  |
| D03 | pg | verify | 0 | INCONCLUSIVE | UNVERIFIED | UNVERIFIED | INCOMPLETE | NOT_MEASURED | The resource budget stopped this provider: Declared memory/disk resource budget exceeded; evidence retained. |  |
| D04 | pg | verify | 0 | INCONCLUSIVE | UNVERIFIED | UNVERIFIED | INCOMPLETE | NOT_MEASURED | The resource budget stopped this provider: Declared memory/disk resource budget exceeded; evidence retained. |  |
| E01 | pg | verify | 0 | INCONCLUSIVE | UNVERIFIED | UNVERIFIED | INCOMPLETE | NOT_MEASURED | The resource budget stopped this provider: Declared memory/disk resource budget exceeded; evidence retained. |  |
| E02 | pg | verify | 0 | INCONCLUSIVE | UNVERIFIED | UNVERIFIED | INCOMPLETE | NOT_MEASURED | The resource budget stopped this provider: Declared memory/disk resource budget exceeded; evidence retained. |  |
| E03 | pg | verify | 0 | INCONCLUSIVE | UNVERIFIED | UNVERIFIED | INCOMPLETE | NOT_MEASURED | The resource budget stopped this provider: Declared memory/disk resource budget exceeded; evidence retained. |  |
| O03 | mssql | measure | 0 | INCONCLUSIVE | UNVERIFIED | UNVERIFIED | INCOMPLETE | NOT_MEASURED | Discovery did not establish a valid rate; no capacity score is available. |  |
| F09 | mssql | fault | 0 | INCONCLUSIVE | PASS | PASS | INCOMPLETE | NOT_MEASURED | Required busy-worker kills with observed reclaim were not all established. | histories/mssql-F09-chaos-checks.json histories/mssql-F09-chaos-model.json histories/mssql-F09-chaos-expected.json histories/mssql-F09-chaos-observed.json histories/mssql-F09-chaos-ledger.json |
| E03 | mssql | fault | 1 | INCONCLUSIVE | PASS | PASS | INCOMPLETE | VALID | The offered overload of 3200 roots/s did not grow the backlog during the overload half. | histories/mssql-E03-fault-1-1280-checks.json histories/mssql-E03-fault-1-1280-backlog.csv histories/mssql-E03-fault-1-1280-window.json histories/mssql-E03-fault-1-1280-admissions.json histories/mssql-E03-fault-1-1280-observations.json histories/mssql-E03-fault-1-1280-expected.json histories/mssql-E03-fault-1-1280-observed.json histories/mssql-E03-fault-1-1280-ledger.json |
| E03 | mssql | fault | 2 | INCONCLUSIVE | PASS | PASS | INCOMPLETE | VALID | The offered overload of 3200 roots/s did not grow the backlog during the overload half. | histories/mssql-E03-fault-2-1280-checks.json histories/mssql-E03-fault-2-1280-backlog.csv histories/mssql-E03-fault-2-1280-window.json histories/mssql-E03-fault-2-1280-admissions.json histories/mssql-E03-fault-2-1280-observations.json histories/mssql-E03-fault-2-1280-expected.json histories/mssql-E03-fault-2-1280-observed.json histories/mssql-E03-fault-2-1280-ledger.json |
| P01 | mssql | reconcile | 0 | INCONCLUSIVE | UNVERIFIED | UNVERIFIED | INCOMPLETE | NOT_MEASURED | No arrival rate passed all required independent validation trials; correctness results remain separate. |  |
| O02 | mssql | reconcile | 0 | INCONCLUSIVE | UNVERIFIED | UNVERIFIED | INCOMPLETE | NOT_MEASURED | No arrival rate passed all required independent validation trials; correctness results remain separate. |  |
| O03 | mssql | reconcile | 0 | INCONCLUSIVE | UNVERIFIED | UNVERIFIED | INCOMPLETE | NOT_MEASURED | Capacity discovery did not find a valid arrival rate. |  |
| F09 | mssql | reconcile | 0 | INCONCLUSIVE | UNVERIFIED | UNVERIFIED | INCOMPLETE | NOT_MEASURED | Required seeded busy kills, reclaim evidence, or acknowledged cohort accounting is incomplete. |  |
| M02 | mssql | reconcile | 0 | INCONCLUSIVE | UNVERIFIED | UNVERIFIED | INCOMPLETE | NOT_MEASURED | No arrival rate passed all required independent validation trials; correctness results remain separate. |  |
| M04 | mssql | reconcile | 0 | INCONCLUSIVE | UNVERIFIED | UNVERIFIED | INCOMPLETE | NOT_MEASURED | No arrival rate passed all required independent validation trials; correctness results remain separate. |  |
| L03 | pg | reconcile | 0 | INCONCLUSIVE | UNVERIFIED | UNVERIFIED | INCOMPLETE | NOT_MEASURED | No arrival rate passed all required independent validation trials; correctness results remain separate. |  |
| P01 | pg | reconcile | 0 | INCONCLUSIVE | UNVERIFIED | UNVERIFIED | INCOMPLETE | NOT_MEASURED | No arrival rate passed all required independent validation trials; correctness results remain separate. |  |
| O03 | pg | reconcile | 0 | INCONCLUSIVE | UNVERIFIED | UNVERIFIED | INCOMPLETE | NOT_MEASURED | Capacity discovery did not find a valid arrival rate. |  |
| F09 | pg | reconcile | 0 | INCONCLUSIVE | UNVERIFIED | UNVERIFIED | INCOMPLETE | NOT_MEASURED | Required seeded busy kills, reclaim evidence, or acknowledged cohort accounting is incomplete. |  |
| I05 | pg | reconcile | 0 | INCONCLUSIVE | UNVERIFIED | UNVERIFIED | INCOMPLETE | NOT_MEASURED | No arrival rate passed all required independent validation trials; correctness results remain separate. |  |
| M02 | pg | reconcile | 0 | INCONCLUSIVE | UNVERIFIED | UNVERIFIED | INCOMPLETE | NOT_MEASURED | No arrival rate passed all required independent validation trials; correctness results remain separate. |  |
| M04 | pg | reconcile | 0 | INCONCLUSIVE | UNVERIFIED | UNVERIFIED | INCOMPLETE | NOT_MEASURED | Missing diagnostic cell payload-65536, independent trial 2. |  |
| M04 | pg | reconcile | 0 | INCONCLUSIVE | UNVERIFIED | UNVERIFIED | INCOMPLETE | NOT_MEASURED | Missing diagnostic cell payload-65536, independent trial 3. |  |
| M04 | pg | reconcile | 0 | INCONCLUSIVE | UNVERIFIED | UNVERIFIED | INCOMPLETE | NOT_MEASURED | No arrival rate passed all required independent validation trials; correctness results remain separate. |  |
| D01 | pg | reconcile | 0 | INCONCLUSIVE | UNVERIFIED | UNVERIFIED | INCOMPLETE | NOT_MEASURED | Missing required boundary repetition 1. |  |
| D01 | pg | reconcile | 0 | INCONCLUSIVE | UNVERIFIED | UNVERIFIED | INCOMPLETE | NOT_MEASURED | Missing required boundary repetition 2. |  |
| D02 | pg | reconcile | 0 | INCONCLUSIVE | UNVERIFIED | UNVERIFIED | INCOMPLETE | NOT_MEASURED | Missing required boundary repetition 1. |  |
| D02 | pg | reconcile | 0 | INCONCLUSIVE | UNVERIFIED | UNVERIFIED | INCOMPLETE | NOT_MEASURED | Missing required boundary repetition 2. |  |
| E01 | pg | reconcile | 0 | INCONCLUSIVE | UNVERIFIED | UNVERIFIED | INCOMPLETE | NOT_MEASURED | Required sustained soak interval or measurement validity is incomplete. |  |
| E02 | pg | reconcile | 0 | INCONCLUSIVE | UNVERIFIED | UNVERIFIED | INCOMPLETE | NOT_MEASURED | Capacity discovery did not find a valid arrival rate. |  |
| E03 | pg | reconcile | 0 | INCONCLUSIVE | UNVERIFIED | UNVERIFIED | INCOMPLETE | NOT_MEASURED | Missing required boundary repetition 1. |  |
| E03 | pg | reconcile | 0 | INCONCLUSIVE | UNVERIFIED | UNVERIFIED | INCOMPLETE | NOT_MEASURED | Missing required boundary repetition 2. |  |

#### Notes

Measurement counts each trial's verdict: VALID qualifies it as capacity evidence, UNSTABLE means its latency target or backlog trend did not qualify, and GENERATOR_LIMITED or OBSERVER_LIMITED means the harness could not offer or observe the load.

A selected-case or quick PASS establishes only its declared scope. Capacity is qualified per workload and repetition, and missing evidence never establishes a guarantee. Process-crash tests do not certify physical power loss.

#### Environment

Protocol: `acta-bench.full/1`  
Build: `599e28e882e3cb43a565ebbe7198104a00285ed7` (source dirty: False)  
Run: `20261025T084709291Z-ccfc5a01088f4cbdb63e9ca59f6131e6`  
architecture: X64  
artifact_disk_free_at_start_bytes: 243836198912  
build_configuration: Release  
compilation_mode: managed_jit  
cpu_model: Intel(R) Core(TM) i9-14900K  
database_backing_disk: Unavailable for Docker volumes; artifact-drive free space is guarded across the complete run.  
docker_vm_cpus: 32  
docker_vm_memory_bytes: 50379538432  
filesystem: ReFS  
gc_server: False  
hybrid_cpu: True  
machine_name: DESKTOP-MVKQH5M  
memory_available_to_runtime_bytes: 102818476032  
missing_metrics: Physical disk, per-database CPU/waits, live/allocation storage and network metrics require collectors. Whole-container CPU/memory are sampled where cgroup v2 is available; unavailable values are not zero.  
monotonic_clock_frequency: 10000000  
mssql_container_image_digest: sha256:4402d880dd4c34bfa7d8705e56a86cd6c88da80a1f6bbbe741f999e76264a090  
mssql_container_memory_mb: 32031  
mssql_cost_threshold_for_parallelism: 50  
mssql_cpu_count: 32  
mssql_database_data_files: 1 x 4096 MB, growth 512 MB  
mssql_database_log_files: 1 x 2048 MB, growth 512 MB  
mssql_max_degree_of_parallelism: 8  
mssql_max_server_memory_mb: 27226  
mssql_memory_optimized_tempdb_metadata: 1  
mssql_optimize_for_ad_hoc_workloads: 1  
mssql_physical_memory_mb: 32031  
mssql_server_mode: owned_container  
mssql_tempdb_data_files: 8 x 1024 MB, growth 256 MB  
mssql_tempdb_log_files: 1 x 1024 MB, growth 256 MB  
mssql_trace_flag_3979: on  
observer: Aggregate SQL counts during a trial and one paged read after it; completion times are database completion stamps on the controller stopwatch.  
os: Microsoft Windows 10.0.26300  
performance_logical_processors: 0,1,2,3,4,5,6,7,8,9,10,11,12,13,14,15  
pg_autovacuum: on  
pg_container_image_digest: sha256:74935e72241653ca55e0414067e6d8763aceb8a810eb51b452253ec3dcfc4336  
pg_container_memory_mb: 32031  
pg_container_shm_mb: 8007  
pg_effective_cache_size: 24023MB  
pg_maintenance_work_mem: 1024MB  
pg_max_connections: 500  
pg_max_wal_size: 8192MB  
pg_server_mode: owned_container  
pg_shared_buffers: 8007MB  
physical_cores: 24  
preloaded_backlog_roots: sqlite=50000,pg=200000,mssql=200000  
processor_count: 32  
repetitions: 3  
run_wall_seconds: 28974  
runtime: .NET 10.0.12  
worker_compilation_mode: managed_jit  
workers_pinned_to_performance_cores: True  

## 2. PostgreSQL, `fb8f8414`

### Acta Bench: INCONCLUSIVE

full profile on pg. 12 of 354 case results did not pass (0 safety failures, 0 liveness failures, 12 coverage gaps); they are listed under Not passing.

#### L01: No-op end-to-end

| Provider | Cell | Offered roots/s | Measurement | Rate, median (min-max) | Unit | End-to-end p50 ms | End-to-end p99 ms |
|---|---|---:|---|---:|---|---:|---:|
| pg | canonical | 10,240 | 2 GENERATOR_LIMITED, 3 UNSTABLE; 2 added by the instability rule | 9,647 (9,404-10,493) | roots/second | 2,633 | 3,414 |
| pg | executor-1 | 10,240 | 3 UNSTABLE | 391 (363-444) | roots/second | 912,738 | 1,462,017 |
| pg | executor-32 | 10,240 | 5 UNSTABLE; 2 added by the instability rule | 5,692 (5,495-9,109) | roots/second | 14,185 | 15,893 |
| pg | workers-16 | 10,240 | 3 GENERATOR_LIMITED | 10,139 (9,844-10,215) | roots/second | 162 | 931 |
| pg | workers-16-single-namespace | 10,240 | 3 GENERATOR_LIMITED, 2 VALID; 2 added by the instability rule | 10,227 (10,110-10,232) | roots/second | 81.8 | 766 |
| pg | workers-4 | 10,240 | 2 GENERATOR_LIMITED, 1 VALID | 10,231 (10,089-10,246) | roots/second | 74.2 | 972 |

#### L02: Single-call admission

| Provider | Cell | Offered roots/s | Measurement | Rate, median (min-max) | Unit | Admission p50 ms | Admission p99 ms |
|---|---|---:|---|---:|---|---:|---:|
| pg | canonical | 10,240 | 5 VALID; 2 added by the instability rule | 10,240 (10,236-10,252) | admissions/second | 17.1 | 28 |

#### L03: Batch admission

| Provider | Cell | Offered roots/s | Measurement | Rate, median (min-max) | Unit | Admission p50 ms | Admission p99 ms |
|---|---|---:|---|---:|---|---:|---:|
| pg | batch-1 | 2,560 | 3 GENERATOR_LIMITED | 430 (369-527) | admissions/second | 2,720 | 2,966 |
| pg | batch-1000 | 2,560 | 5 VALID; 2 added by the instability rule | 2,547 (2,547-2,547) | admissions/second | 59.4 | 84.7 |
| pg | canonical | 2,560 | 3 VALID | 2,560 (2,560-2,560) | admissions/second | 27.9 | 41.8 |

#### L04: Preloaded backlog drain

| Provider | Cell | Workers | Roots | Measurement | Database steady roots/s | Steady roots/s | Drain roots/s, median (min-max) | First completion s |
|---|---|---:|---:|---|---:|---:|---:|---:|
| pg | canonical | 1 | 200,000 | 3 VALID | 20,881 | 20,862 | 19,317 (18,913-19,335) | 0.571 |
| pg | drain-workers-16 | 16 | 200,000 | 3 VALID | 59,368 | 59,055 | 47,254 (44,384-47,422) | 0.91 |
| pg | drain-workers-4 | 4 | 200,000 | 3 VALID | 57,169 | 57,213 | 48,903 (48,456-49,193) | 0.579 |
| pg | drain-workers-8 | 8 | 200,000 | 3 VALID | 77,662 | 77,125 | 59,368 (58,952-59,533) | 0.592 |

#### C01: Three persisted steps

| Provider | Cell | Offered roots/s | Measurement | Rate, median (min-max) | Unit | End-to-end p50 ms | End-to-end p99 ms |
|---|---|---:|---|---:|---|---:|---:|
| pg | canonical | 2,560 | 5 UNSTABLE; 2 added by the instability rule | 1,834 (1,808-2,345) | roots/second | 11,672 | 15,893 |

#### P01: Ten children and join

| Provider | Cell | Offered roots/s | Measurement | Rate, median (min-max) | Unit | End-to-end p50 ms | End-to-end p99 ms |
|---|---|---:|---|---:|---|---:|---:|
| pg | canonical | 320 | 4 UNSTABLE, 1 VALID; 2 added by the instability rule | 276 (263-331) | roots/second | 6,296 | 7,990 |

#### R01: Transient failure and retry

| Provider | Cell | Offered roots/s | Measurement | Rate, median (min-max) | Unit | End-to-end p50 ms | End-to-end p99 ms |
|---|---|---:|---|---:|---|---:|---:|
| pg | canonical | 5,120 | 3 VALID | 5,122 (5,118-5,124) | roots/second | 38.5 | 2,166 |

#### O01: Many independent lanes

| Provider | Cell | Offered roots/s | Measurement | Rate, median (min-max) | Unit | End-to-end p50 ms | End-to-end p99 ms |
|---|---|---:|---|---:|---|---:|---:|
| pg | canonical | 5,120 | 3 VALID | 5,139 (5,044-5,264) | roots/second | 539 | 1,032 |

#### O02: One hot lane

| Provider | Cell | Offered roots/s | Measurement | Rate, median (min-max) | Unit | End-to-end p50 ms | End-to-end p99 ms |
|---|---|---:|---|---:|---|---:|---:|
| pg | canonical | 80 | 3 VALID | 80.7 (80.4-81.3) | roots/second | 513 | 1,021 |

#### O03: Hot concurrency key

| Provider | Cell | Offered roots/s | Measurement | Rate, median (min-max) | Unit | End-to-end p50 ms | End-to-end p99 ms |
|---|---|---:|---|---:|---|---:|---:|
| pg | canonical | 1 | 2 UNSTABLE, 3 VALID; 2 added by the instability rule | 1 (1-1) | roots/second | 431 | 1,022 |

#### S02: Parked-work skew

| Provider | Cell | Offered roots/s | Measurement | Rate, median (min-max) | Unit | End-to-end p50 ms | End-to-end p99 ms |
|---|---|---:|---|---:|---|---:|---:|
| pg | canonical | 5,120 | 3 VALID | 5,134 (5,095-5,213) | roots/second | 527 | 1,026 |

#### S03: Future high-priority skew

| Provider | Cell | Offered roots/s | Measurement | Rate, median (min-max) | Unit | End-to-end p50 ms | End-to-end p99 ms |
|---|---|---:|---|---:|---|---:|---:|
| pg | canonical | 5,120 | 3 VALID | 5,066 (4,992-5,132) | roots/second | 539 | 1,032 |

#### I05: Idempotent external sink

| Provider | Cell | Offered roots/s | Measurement | Rate, median (min-max) | Unit | End-to-end p50 ms | End-to-end p99 ms |
|---|---|---:|---|---:|---|---:|---:|
| pg | canonical | 640 | 5 UNSTABLE; 2 added by the instability rule | 307 (304-319) | roots/second | 27,761 | 43,515 |

#### M01: Fixed CPU work

| Provider | Cell | Offered roots/s | Measurement | Rate, median (min-max) | Unit | End-to-end p50 ms | End-to-end p99 ms |
|---|---|---:|---|---:|---|---:|---:|
| pg | canonical | 5,120 | 3 VALID | 5,150 (5,050-5,164) | roots/second | 536 | 1,043 |

#### M02: Controlled I/O work

| Provider | Cell | Offered roots/s | Measurement | Rate, median (min-max) | Unit | End-to-end p50 ms | End-to-end p99 ms |
|---|---|---:|---|---:|---|---:|---:|
| pg | canonical | 160 | 3 VALID | 160 (159-161) | roots/second | 630 | 1,143 |

#### M03: Short/long mixed workload

| Provider | Cell | Offered roots/s | Measurement | Rate, median (min-max) | Unit | End-to-end p50 ms | End-to-end p99 ms |
|---|---|---:|---|---:|---|---:|---:|
| pg | canonical | 640 | 3 VALID | 640 (640-640) | roots/second | 111 | 1,150 |

#### M04: Payload sweep

| Provider | Cell | Offered roots/s | Measurement | Rate, median (min-max) | Unit | End-to-end p50 ms | End-to-end p99 ms |
|---|---|---:|---|---:|---|---:|---:|
| pg | canonical | 2,560 | 2 GENERATOR_LIMITED, 1 VALID | 2,572 (2,567-2,602) | roots/second | 545 | 1,054 |
| pg | payload-256 | 2,560 | 1 GENERATOR_LIMITED, 2 VALID | 2,535 (2,494-2,555) | roots/second | 521 | 1,021 |
| pg | payload-4096 | 2,560 | 3 VALID | 2,541 (2,517-2,569) | roots/second | 519 | 1,026 |
| pg | payload-65536 | 2,560 | 5 GENERATOR_LIMITED; 2 added by the instability rule | 1,346 (1,218-1,454) | roots/second | 14,574 | 17,807 |

#### E01: Mixed-workload soak

| Provider | Cell | Offered roots/s | Measurement | Rate, median (min-max) | Unit | End-to-end p50 ms | End-to-end p99 ms |
|---|---|---:|---|---:|---|---:|---:|
| pg | canonical | 448 | 1 GENERATOR_LIMITED | 448 (448-448) | roots/second | 157 | 1,119 |

#### E02: Queries during processing

| Provider | Cell | Offered roots/s | Measurement | Rate, median (min-max) | Unit | End-to-end p50 ms | End-to-end p99 ms |
|---|---|---:|---|---:|---|---:|---:|
| pg | canonical | 5,120 | 3 VALID | 5,235 (5,187-5,235) | roots/second | 530 | 1,032 |

#### Overhead

| Measure | Value |
|---|---:|
| Run wall time | 14491 s |
| Evidence written | 28.7 MB of a 50 MB budget |
| Setup, median per trial | 0.66 s |
| Seeding, median per trial | 5E-07 s |
| Measured window and drain, median per trial | 17.2 s |
| Verification, median per trial | 0.17 s |
| Ledger checks, median per trial | 0.0406 s |
| Evidence writing, median per trial | 0.00211 s |

| Disk, by phase and drive | Windows counters |
|---|---|
| before | 0 c: | write 3.38 ms mean, 8.36 ms p95, 14 writes/s, 99% idle, 14 samples |
| before | 1 d: | write 5.67 ms mean, 6.95 ms p95, 3 writes/s, 100% idle, 14 samples |
| pg | 0 c: | write 3.58 ms mean, 7.95 ms p95, 10 writes/s, 99% idle, 7188 samples |
| pg | 1 d: | write 0.32 ms mean, 0.99 ms p95, 968 writes/s, 45% idle, 7188 samples |
| after | 0 c: | write 3.88 ms mean, 8.37 ms p95, 9 writes/s, 99% idle, 15 samples |
| after | 1 d: | write 0.12 ms mean, 0.21 ms p95, 21 writes/s, 99% idle, 15 samples |

| Slowest cases | Provider | Wall time s | Results |
|---|---|---:|---:|
| L01 | pg | 5,705 | 35 |
| M04 | pg | 1,241 | 23 |
| O03 | pg | 1,035 | 14 |
| L03 | pg | 651 | 20 |
| E01 | pg | 614 | 1 |
| L02 | pg | 581 | 16 |
| I05 | pg | 559 | 15 |
| C01 | pg | 451 | 14 |
| S02 | pg | 421 | 14 |
| M01 | pg | 407 | 13 |

#### Not passing

| Case | Provider | Phase | Trial | Status | Safety | Liveness | Coverage | Measurement | Reason | Evidence |
|---|---|---|---:|---|---|---|---|---|---|---|
| F09 | pg | verify | 0 | INCONCLUSIVE | UNVERIFIED | UNVERIFIED | INCOMPLETE | NOT_MEASURED | Case did not finish (InvalidOperationException: Owned worker exited before readiness: Acta Bench could not complete the command (SocketException: Only one usage of each socket address (protocol/network address/port) is normally permitted.).); partial histories retained. | histories/pg-F09-chaos-ledger.json |
| E03 | pg | fault | 0 | INCONCLUSIVE | UNVERIFIED | UNVERIFIED | INCOMPLETE | NOT_MEASURED | No independently validated L01 capacity is available for this declared workload. |  |
| L01 | pg | reconcile | 0 | INCONCLUSIVE | UNVERIFIED | UNVERIFIED | INCOMPLETE | NOT_MEASURED | No arrival rate passed all required independent validation trials; correctness results remain separate. |  |
| C01 | pg | reconcile | 0 | INCONCLUSIVE | UNVERIFIED | UNVERIFIED | INCOMPLETE | NOT_MEASURED | No arrival rate passed all required independent validation trials; correctness results remain separate. |  |
| P01 | pg | reconcile | 0 | INCONCLUSIVE | UNVERIFIED | UNVERIFIED | INCOMPLETE | NOT_MEASURED | No arrival rate passed all required independent validation trials; correctness results remain separate. |  |
| O03 | pg | reconcile | 0 | INCONCLUSIVE | UNVERIFIED | UNVERIFIED | INCOMPLETE | NOT_MEASURED | No arrival rate passed all required independent validation trials; correctness results remain separate. |  |
| F09 | pg | reconcile | 0 | INCONCLUSIVE | UNVERIFIED | UNVERIFIED | INCOMPLETE | NOT_MEASURED | Required seeded busy kills, reclaim evidence, or acknowledged cohort accounting is incomplete. |  |
| I05 | pg | reconcile | 0 | INCONCLUSIVE | UNVERIFIED | UNVERIFIED | INCOMPLETE | NOT_MEASURED | No arrival rate passed all required independent validation trials; correctness results remain separate. |  |
| M04 | pg | reconcile | 0 | INCONCLUSIVE | UNVERIFIED | UNVERIFIED | INCOMPLETE | NOT_MEASURED | No arrival rate passed all required independent validation trials; correctness results remain separate. |  |
| E01 | pg | reconcile | 0 | INCONCLUSIVE | UNVERIFIED | UNVERIFIED | INCOMPLETE | NOT_MEASURED | Required sustained soak interval or measurement validity is incomplete. |  |
| E03 | pg | reconcile | 0 | INCONCLUSIVE | UNVERIFIED | UNVERIFIED | INCOMPLETE | NOT_MEASURED | Missing required boundary repetition 1. |  |
| E03 | pg | reconcile | 0 | INCONCLUSIVE | UNVERIFIED | UNVERIFIED | INCOMPLETE | NOT_MEASURED | Missing required boundary repetition 2. |  |

#### Notes

Measurement counts each trial's verdict: VALID qualifies it as capacity evidence, UNSTABLE means its latency target or backlog trend did not qualify, and GENERATOR_LIMITED or OBSERVER_LIMITED means the harness could not offer or observe the load.

A selected-case or quick PASS establishes only its declared scope. Capacity is qualified per workload and repetition, and missing evidence never establishes a guarantee. Process-crash tests do not certify physical power loss.

#### Environment

Protocol: `acta-bench.full/1`  
Build: `fb8f841476c904354633e1b26432c74602fb3f07` (source dirty: False)  
Run: `20261025T171642830Z-10ff9a58c7ae475aab00670be4a4a073`  
architecture: X64  
artifact_disk_free_at_start_bytes: 236727373824  
build_configuration: Release  
compilation_mode: managed_jit  
cpu_model: Intel(R) Core(TM) i9-14900K  
database_backing_disk: Unavailable for Docker volumes; artifact-drive free space is guarded across the complete run.  
docker_vm_cpus: 32  
docker_vm_memory_bytes: 50379538432  
filesystem: ReFS  
gc_server: False  
hybrid_cpu: True  
machine_name: DESKTOP-MVKQH5M  
memory_available_to_runtime_bytes: 102818476032  
missing_metrics: Physical disk, per-database CPU/waits, live/allocation storage and network metrics require collectors. Whole-container CPU/memory are sampled where cgroup v2 is available; unavailable values are not zero.  
monotonic_clock_frequency: 10000000  
observer: Aggregate SQL counts during a trial and one paged read after it; completion times are database completion stamps on the controller stopwatch.  
os: Microsoft Windows 10.0.26300  
performance_logical_processors: 0,1,2,3,4,5,6,7,8,9,10,11,12,13,14,15  
pg_autovacuum: on  
pg_container_image_digest: sha256:74935e72241653ca55e0414067e6d8763aceb8a810eb51b452253ec3dcfc4336  
pg_container_memory_mb: 32031  
pg_container_shm_mb: 8007  
pg_effective_cache_size: 24023MB  
pg_maintenance_work_mem: 1024MB  
pg_max_connections: 1000  
pg_max_wal_size: 8192MB  
pg_server_mode: owned_container  
pg_shared_buffers: 8007MB  
physical_cores: 24  
preloaded_backlog_roots: sqlite=50000,pg=200000,mssql=200000  
processor_count: 32  
repetitions: 3  
run_wall_seconds: 14491  
runtime: .NET 10.0.12  
worker_compilation_mode: managed_jit  
workers_pinned_to_performance_cores: True  

## 3. F09 on SQL Server, `988764e6`

### Acta Bench: PASS

full profile on mssql. All 9 case results passed, with safety, liveness and coverage complete.

#### Overhead

| Measure | Value |
|---|---:|
| Run wall time | 1686 s |
| Evidence written | 0.1 MB of a 50 MB budget |
| Setup, median per trial | - s |
| Seeding, median per trial | - s |
| Measured window and drain, median per trial | - s |
| Verification, median per trial | - s |
| Ledger checks, median per trial | 10.6 s |
| Evidence writing, median per trial | - s |

| Disk, by phase and drive | Windows counters |
|---|---|
| before | 0 c: | write 2.66 ms mean, 7.51 ms p95, 7 writes/s, 99% idle, 14 samples |
| before | 1 d: | write 5.31 ms mean, 6.97 ms p95, 3 writes/s, 100% idle, 14 samples |
| mssql | 0 c: | write 3.58 ms mean, 8.09 ms p95, 6 writes/s, 101% idle, 810 samples |
| mssql | 1 d: | write 0.25 ms mean, 0.93 ms p95, 1053 writes/s, 65% idle, 810 samples |
| after | 0 c: | write 3.45 ms mean, 8.40 ms p95, 11 writes/s, 99% idle, 15 samples |
| after | 1 d: | write 0.29 ms mean, 1.06 ms p95, 14 writes/s, 99% idle, 15 samples |

| Slowest cases | Provider | Wall time s | Results |
|---|---|---:|---:|
| F09 | mssql | 1,450 | 1 |

#### Notes

Measurement counts each trial's verdict: VALID qualifies it as capacity evidence, UNSTABLE means its latency target or backlog trend did not qualify, and GENERATOR_LIMITED or OBSERVER_LIMITED means the harness could not offer or observe the load.

A selected-case or quick PASS establishes only its declared scope. Capacity is qualified per workload and repetition, and missing evidence never establishes a guarantee. Process-crash tests do not certify physical power loss.

#### Environment

Protocol: `acta-bench.full/1`  
Build: `988764e6b64d0017e79287eb158fe370ee8b4a3a` (source dirty: False)  
Run: `20261025T212820450Z-7717e7b27a80435c9b87b0884064657f`  
architecture: X64  
artifact_disk_free_at_start_bytes: 236609167360  
build_configuration: Release  
compilation_mode: managed_jit  
cpu_model: Intel(R) Core(TM) i9-14900K  
database_backing_disk: Unavailable for Docker volumes; artifact-drive free space is guarded across the complete run.  
docker_vm_cpus: 32  
docker_vm_memory_bytes: 50379538432  
filesystem: ReFS  
gc_server: False  
hybrid_cpu: True  
machine_name: DESKTOP-MVKQH5M  
memory_available_to_runtime_bytes: 102818476032  
missing_metrics: Physical disk, per-database CPU/waits, live/allocation storage and network metrics require collectors. Whole-container CPU/memory are sampled where cgroup v2 is available; unavailable values are not zero.  
monotonic_clock_frequency: 10000000  
mssql_container_image_digest: sha256:4402d880dd4c34bfa7d8705e56a86cd6c88da80a1f6bbbe741f999e76264a090  
mssql_container_memory_mb: 32031  
mssql_cost_threshold_for_parallelism: 50  
mssql_cpu_count: 32  
mssql_database_data_files: 1 x 4096 MB, growth 512 MB  
mssql_database_log_files: 1 x 2048 MB, growth 512 MB  
mssql_max_degree_of_parallelism: 8  
mssql_max_server_memory_mb: 27226  
mssql_memory_optimized_tempdb_metadata: 1  
mssql_optimize_for_ad_hoc_workloads: 1  
mssql_physical_memory_mb: 32031  
mssql_server_mode: owned_container  
mssql_tempdb_data_files: 8 x 1024 MB, growth 256 MB  
mssql_tempdb_log_files: 1 x 1024 MB, growth 256 MB  
mssql_trace_flag_3979: on  
observer: Aggregate SQL counts during a trial and one paged read after it; completion times are database completion stamps on the controller stopwatch.  
os: Microsoft Windows 10.0.26300  
performance_logical_processors: 0,1,2,3,4,5,6,7,8,9,10,11,12,13,14,15  
physical_cores: 24  
preloaded_backlog_roots: sqlite=50000,pg=200000,mssql=200000  
processor_count: 32  
repetitions: 3  
run_wall_seconds: 1686  
runtime: .NET 10.0.12  
worker_compilation_mode: managed_jit  
workers_pinned_to_performance_cores: True  

## 4. F09 on PostgreSQL, `988764e6`

### Acta Bench: PASS

full profile on pg. All 9 case results passed, with safety, liveness and coverage complete.

#### Overhead

| Measure | Value |
|---|---:|
| Run wall time | 1101 s |
| Evidence written | 0.1 MB of a 50 MB budget |
| Setup, median per trial | - s |
| Seeding, median per trial | - s |
| Measured window and drain, median per trial | - s |
| Verification, median per trial | - s |
| Ledger checks, median per trial | 9.73 s |
| Evidence writing, median per trial | - s |

| Disk, by phase and drive | Windows counters |
|---|---|
| before | 0 c: | write 3.55 ms mean, 6.59 ms p95, 9 writes/s, 99% idle, 14 samples |
| before | 1 d: | write 5.38 ms mean, 8.02 ms p95, 3 writes/s, 100% idle, 14 samples |
| pg | 0 c: | write 3.66 ms mean, 8.06 ms p95, 6 writes/s, 101% idle, 518 samples |
| pg | 1 d: | write 0.08 ms mean, 0.06 ms p95, 1014 writes/s, 43% idle, 518 samples |
| after | 0 c: | write 3.42 ms mean, 8.19 ms p95, 8 writes/s, 99% idle, 15 samples |
| after | 1 d: | write 1.64 ms mean, 6.92 ms p95, 19 writes/s, 100% idle, 15 samples |

| Slowest cases | Provider | Wall time s | Results |
|---|---|---:|---:|
| F09 | pg | 1,038 | 1 |

#### Notes

Measurement counts each trial's verdict: VALID qualifies it as capacity evidence, UNSTABLE means its latency target or backlog trend did not qualify, and GENERATOR_LIMITED or OBSERVER_LIMITED means the harness could not offer or observe the load.

A selected-case or quick PASS establishes only its declared scope. Capacity is qualified per workload and repetition, and missing evidence never establishes a guarantee. Process-crash tests do not certify physical power loss.

#### Environment

Protocol: `acta-bench.full/1`  
Build: `988764e6b64d0017e79287eb158fe370ee8b4a3a` (source dirty: False)  
Run: `20261025T215626759Z-e52c64f2055544aa81bc5754dda63c14`  
architecture: X64  
artifact_disk_free_at_start_bytes: 236602216448  
build_configuration: Release  
compilation_mode: managed_jit  
cpu_model: Intel(R) Core(TM) i9-14900K  
database_backing_disk: Unavailable for Docker volumes; artifact-drive free space is guarded across the complete run.  
docker_vm_cpus: 32  
docker_vm_memory_bytes: 50379538432  
filesystem: ReFS  
gc_server: False  
hybrid_cpu: True  
machine_name: DESKTOP-MVKQH5M  
memory_available_to_runtime_bytes: 102818476032  
missing_metrics: Physical disk, per-database CPU/waits, live/allocation storage and network metrics require collectors. Whole-container CPU/memory are sampled where cgroup v2 is available; unavailable values are not zero.  
monotonic_clock_frequency: 10000000  
observer: Aggregate SQL counts during a trial and one paged read after it; completion times are database completion stamps on the controller stopwatch.  
os: Microsoft Windows 10.0.26300  
performance_logical_processors: 0,1,2,3,4,5,6,7,8,9,10,11,12,13,14,15  
pg_autovacuum: on  
pg_container_image_digest: sha256:74935e72241653ca55e0414067e6d8763aceb8a810eb51b452253ec3dcfc4336  
pg_container_memory_mb: 32031  
pg_container_shm_mb: 8007  
pg_effective_cache_size: 24023MB  
pg_maintenance_work_mem: 1024MB  
pg_max_connections: 2500  
pg_max_wal_size: 8192MB  
pg_server_mode: owned_container  
pg_shared_buffers: 8007MB  
physical_cores: 24  
preloaded_backlog_roots: sqlite=50000,pg=200000,mssql=200000  
processor_count: 32  
repetitions: 3  
run_wall_seconds: 1101  
runtime: .NET 10.0.12  
worker_compilation_mode: managed_jit  
workers_pinned_to_performance_cores: True  

## 5. P01 on SQLite, `05e5c27c`

### Acta Bench: PASS

full profile on sqlite. All 16 case results passed, with safety, liveness and coverage complete.

#### P01: Ten children and join

| Provider | Cell | Offered roots/s | Measurement | Rate, median (min-max) | Unit | End-to-end p50 ms | End-to-end p99 ms |
|---|---|---:|---|---:|---|---:|---:|
| sqlite | canonical | 9 | 3 VALID | 9.03 (8.77-9.2) | roots/second | 213 | 1,842 |

#### Overhead

| Measure | Value |
|---|---:|
| Run wall time | 274 s |
| Evidence written | 0.6 MB of a 50 MB budget |
| Setup, median per trial | 0.358 s |
| Seeding, median per trial | 5E-07 s |
| Measured window and drain, median per trial | 26.5 s |
| Verification, median per trial | 0.0219 s |
| Ledger checks, median per trial | 0.0109 s |
| Evidence writing, median per trial | 0.00191 s |

| Disk, by phase and drive | Windows counters |
|---|---|
| before | 0 c: | write 3.83 ms mean, 8.45 ms p95, 5 writes/s, 99% idle, 14 samples |
| before | 1 d: | write 5.32 ms mean, 7.20 ms p95, 3 writes/s, 100% idle, 14 samples |
| sqlite | 0 c: | write 3.38 ms mean, 7.63 ms p95, 7 writes/s, 99% idle, 107 samples |
| sqlite | 1 d: | write 0.04 ms mean, 0.04 ms p95, 850 writes/s, 91% idle, 107 samples |
| after | 0 c: | write 3.56 ms mean, 8.05 ms p95, 7 writes/s, 102% idle, 15 samples |
| after | 1 d: | write 4.34 ms mean, 7.52 ms p95, 20 writes/s, 100% idle, 15 samples |

| Slowest cases | Provider | Wall time s | Results |
|---|---|---:|---:|
| P01 | sqlite | 214 | 8 |

#### Notes

Measurement counts each trial's verdict: VALID qualifies it as capacity evidence, UNSTABLE means its latency target or backlog trend did not qualify, and GENERATOR_LIMITED or OBSERVER_LIMITED means the harness could not offer or observe the load.

A selected-case or quick PASS establishes only its declared scope. Capacity is qualified per workload and repetition, and missing evidence never establishes a guarantee. Process-crash tests do not certify physical power loss.

#### Environment

Protocol: `acta-bench.full/1`  
Build: `05e5c27cdc1adad696ae116e75ea2bf6140318a5` (source dirty: False)  
Run: `20261025T224336926Z-4223d27b87664c29ba0a7158fcaeda67`  
architecture: X64  
artifact_disk_free_at_start_bytes: 236531445760  
build_configuration: Release  
compilation_mode: managed_jit  
cpu_model: Intel(R) Core(TM) i9-14900K  
database_backing_disk: Unavailable for Docker volumes; artifact-drive free space is guarded across the complete run.  
filesystem: ReFS  
gc_server: False  
hybrid_cpu: True  
machine_name: DESKTOP-MVKQH5M  
memory_available_to_runtime_bytes: 102818476032  
missing_metrics: Physical disk, per-database CPU/waits, live/allocation storage and network metrics require collectors. Whole-container CPU/memory are sampled where cgroup v2 is available; unavailable values are not zero.  
monotonic_clock_frequency: 10000000  
observer: Aggregate SQL counts during a trial and one paged read after it; completion times are database completion stamps on the controller stopwatch.  
os: Microsoft Windows 10.0.26300  
performance_logical_processors: 0,1,2,3,4,5,6,7,8,9,10,11,12,13,14,15  
physical_cores: 24  
preloaded_backlog_roots: sqlite=50000,pg=200000,mssql=200000  
processor_count: 32  
repetitions: 3  
run_wall_seconds: 274  
runtime: .NET 10.0.12  
worker_compilation_mode: managed_jit  
workers_pinned_to_performance_cores: True  

## 6. Capacity on SQL Server, `05e5c27c`

### Acta Bench: INCONCLUSIVE

full profile on mssql. 2 of 92 case results did not pass (0 safety failures, 0 liveness failures, 2 coverage gaps); they are listed under Not passing.

#### P01: Ten children and join

| Provider | Cell | Offered roots/s | Measurement | Rate, median (min-max) | Unit | End-to-end p50 ms | End-to-end p99 ms |
|---|---|---:|---|---:|---|---:|---:|
| mssql | canonical | 160 | 5 VALID; 2 added by the instability rule | 160 (160-161) | roots/second | 230 | 458 |

#### O02: One hot lane

| Provider | Cell | Offered roots/s | Measurement | Rate, median (min-max) | Unit | End-to-end p50 ms | End-to-end p99 ms |
|---|---|---:|---|---:|---|---:|---:|
| mssql | canonical | 120 | 3 VALID | 123 (122-123) | roots/second | 641 | 1,032 |
| mssql | canonical | 140 | 5 UNSTABLE; 2 added by the instability rule | 128 (122-136) | roots/second | 3,846 | 4,674 |
| mssql | canonical | 160 | 5 UNSTABLE; 2 added by the instability rule | 119 (116-169) | roots/second | 9,148 | 10,234 |

#### O03: Hot concurrency key

| Provider | Cell | Offered roots/s | Measurement | Rate, median (min-max) | Unit | End-to-end p50 ms | End-to-end p99 ms |
|---|---|---:|---|---:|---|---:|---:|
| mssql | canonical | - | 1 NOT_MEASURED | - |  | - | - |

#### M02: Controlled I/O work

| Provider | Cell | Offered roots/s | Measurement | Rate, median (min-max) | Unit | End-to-end p50 ms | End-to-end p99 ms |
|---|---|---:|---|---:|---|---:|---:|
| mssql | canonical | 400 | 3 VALID | 403 (396-404) | roots/second | 673 | 1,137 |
| mssql | canonical | 480 | 5 UNSTABLE; 2 added by the instability rule | 432 (427-438) | roots/second | 3,604 | 4,802 |
| mssql | canonical | 560 | 5 UNSTABLE; 2 added by the instability rule | 442 (439-575) | roots/second | 7,568 | 11,484 |
| mssql | canonical | 640 | 2 GENERATOR_LIMITED, 3 UNSTABLE; 2 added by the instability rule | 439 (425-635) | roots/second | 10,305 | 15,302 |

#### M04: Payload sweep

| Provider | Cell | Offered roots/s | Measurement | Rate, median (min-max) | Unit | End-to-end p50 ms | End-to-end p99 ms |
|---|---|---:|---|---:|---|---:|---:|
| mssql | canonical | 320 | 3 VALID | 318 (318-321) | roots/second | 542 | 1,060 |
| mssql | payload-256 | 320 | 3 VALID | 321 (312-327) | roots/second | 513 | 1,026 |
| mssql | payload-4096 | 320 | 3 VALID | 317 (316-324) | roots/second | 521 | 1,032 |
| mssql | payload-65536 | 320 | 4 GENERATOR_LIMITED, 1 VALID; 2 added by the instability rule | 321 (316-327) | roots/second | 533 | 1,101 |

#### Overhead

| Measure | Value |
|---|---:|
| Run wall time | 3696 s |
| Evidence written | 8.1 MB of a 50 MB budget |
| Setup, median per trial | 1.28 s |
| Seeding, median per trial | 5E-07 s |
| Measured window and drain, median per trial | 40.9 s |
| Verification, median per trial | 0.0599 s |
| Ledger checks, median per trial | 0.143 s |
| Evidence writing, median per trial | 0.00207 s |

| Disk, by phase and drive | Windows counters |
|---|---|
| before | 0 c: | write 4.18 ms mean, 8.14 ms p95, 6 writes/s, 99% idle, 14 samples |
| before | 1 d: | write 5.03 ms mean, 7.98 ms p95, 4 writes/s, 100% idle, 14 samples |
| mssql | 0 c: | write 3.24 ms mean, 7.80 ms p95, 10 writes/s, 99% idle, 1811 samples |
| mssql | 1 d: | write 0.29 ms mean, 1.39 ms p95, 1121 writes/s, 61% idle, 1811 samples |
| after | 0 c: | write 3.19 ms mean, 8.41 ms p95, 5 writes/s, 99% idle, 15 samples |
| after | 1 d: | write 4.46 ms mean, 7.68 ms p95, 12 writes/s, 100% idle, 15 samples |

| Slowest cases | Provider | Wall time s | Results |
|---|---|---:|---:|
| M02 | mssql | 1,029 | 25 |
| M04 | mssql | 736 | 20 |
| O02 | mssql | 715 | 19 |
| O03 | mssql | 683 | 9 |
| P01 | mssql | 296 | 10 |

#### Not passing

| Case | Provider | Phase | Trial | Status | Safety | Liveness | Coverage | Measurement | Reason | Evidence |
|---|---|---|---:|---|---|---|---|---|---|---|
| O03 | mssql | measure | 0 | INCONCLUSIVE | UNVERIFIED | UNVERIFIED | INCOMPLETE | NOT_MEASURED | Discovery did not establish a valid rate; no capacity score is available. |  |
| O03 | mssql | reconcile | 0 | INCONCLUSIVE | UNVERIFIED | UNVERIFIED | INCOMPLETE | NOT_MEASURED | Capacity discovery did not find a valid arrival rate. |  |

#### Notes

Measurement counts each trial's verdict: VALID qualifies it as capacity evidence, UNSTABLE means its latency target or backlog trend did not qualify, and GENERATOR_LIMITED or OBSERVER_LIMITED means the harness could not offer or observe the load.

A selected-case or quick PASS establishes only its declared scope. Capacity is qualified per workload and repetition, and missing evidence never establishes a guarantee. Process-crash tests do not certify physical power loss.

#### Environment

Protocol: `acta-bench.full/1`  
Build: `05e5c27cdc1adad696ae116e75ea2bf6140318a5` (source dirty: False)  
Run: `20261025T224812128Z-ad7e90b325424ee6acbd4810822c5bb8`  
architecture: X64  
artifact_disk_free_at_start_bytes: 236453855232  
build_configuration: Release  
compilation_mode: managed_jit  
cpu_model: Intel(R) Core(TM) i9-14900K  
database_backing_disk: Unavailable for Docker volumes; artifact-drive free space is guarded across the complete run.  
docker_vm_cpus: 32  
docker_vm_memory_bytes: 50379538432  
filesystem: ReFS  
gc_server: False  
hybrid_cpu: True  
machine_name: DESKTOP-MVKQH5M  
memory_available_to_runtime_bytes: 102818476032  
missing_metrics: Physical disk, per-database CPU/waits, live/allocation storage and network metrics require collectors. Whole-container CPU/memory are sampled where cgroup v2 is available; unavailable values are not zero.  
monotonic_clock_frequency: 10000000  
mssql_container_image_digest: sha256:4402d880dd4c34bfa7d8705e56a86cd6c88da80a1f6bbbe741f999e76264a090  
mssql_container_memory_mb: 32031  
mssql_cost_threshold_for_parallelism: 50  
mssql_cpu_count: 32  
mssql_database_data_files: 1 x 4096 MB, growth 512 MB  
mssql_database_log_files: 1 x 2048 MB, growth 512 MB  
mssql_max_degree_of_parallelism: 8  
mssql_max_server_memory_mb: 27226  
mssql_memory_optimized_tempdb_metadata: 1  
mssql_optimize_for_ad_hoc_workloads: 1  
mssql_physical_memory_mb: 32031  
mssql_server_mode: owned_container  
mssql_tempdb_data_files: 8 x 1024 MB, growth 256 MB  
mssql_tempdb_log_files: 1 x 1024 MB, growth 256 MB  
mssql_trace_flag_3979: on  
observer: Aggregate SQL counts during a trial and one paged read after it; completion times are database completion stamps on the controller stopwatch.  
os: Microsoft Windows 10.0.26300  
performance_logical_processors: 0,1,2,3,4,5,6,7,8,9,10,11,12,13,14,15  
physical_cores: 24  
preloaded_backlog_roots: sqlite=50000,pg=200000,mssql=200000  
processor_count: 32  
repetitions: 3  
run_wall_seconds: 3696  
runtime: .NET 10.0.12  
worker_compilation_mode: managed_jit  
workers_pinned_to_performance_cores: True  

## 7. Capacity on PostgreSQL, `05e5c27c`

### Acta Bench: INCONCLUSIVE

full profile on pg. 3 of 171 case results did not pass (0 safety failures, 0 liveness failures, 3 coverage gaps); they are listed under Not passing.

#### L01: No-op end-to-end

| Provider | Cell | Offered roots/s | Measurement | Rate, median (min-max) | Unit | End-to-end p50 ms | End-to-end p99 ms |
|---|---|---:|---|---:|---|---:|---:|
| pg | canonical | 7,680 | 3 VALID | 7,636 (7,502-7,830) | roots/second | 536 | 1,032 |
| pg | canonical | 8,960 | 1 GENERATOR_LIMITED, 4 VALID; 2 added by the instability rule | 9,026 (8,826-9,433) | roots/second | 547 | 1,037 |
| pg | canonical | 10,240 | 4 GENERATOR_LIMITED, 1 UNSTABLE; 2 added by the instability rule | 9,156 (9,122-10,420) | roots/second | 2,887 | 4,428 |
| pg | executor-1 | 10,240 | 3 UNSTABLE | 408 (301-413) | roots/second | 912,738 | 1,462,017 |
| pg | executor-32 | 10,240 | 5 UNSTABLE; 2 added by the instability rule | 7,223 (5,785-9,600) | roots/second | 11,863 | 14,974 |
| pg | workers-16 | 10,240 | 3 GENERATOR_LIMITED | 10,228 (10,146-10,249) | roots/second | 93.2 | 936 |
| pg | workers-16-single-namespace | 10,240 | 2 GENERATOR_LIMITED, 1 VALID | 10,229 (10,074-10,238) | roots/second | 83.2 | 840 |
| pg | workers-4 | 10,240 | 3 GENERATOR_LIMITED | 10,208 (9,977-10,342) | roots/second | 228 | 999 |

#### C01: Three persisted steps

| Provider | Cell | Offered roots/s | Measurement | Rate, median (min-max) | Unit | End-to-end p50 ms | End-to-end p99 ms |
|---|---|---:|---|---:|---|---:|---:|
| pg | canonical | 1,280 | 3 VALID | 1,259 (1,252-1,302) | roots/second | 591 | 1,101 |

#### P01: Ten children and join

| Provider | Cell | Offered roots/s | Measurement | Rate, median (min-max) | Unit | End-to-end p50 ms | End-to-end p99 ms |
|---|---|---:|---|---:|---|---:|---:|
| pg | canonical | 280 | 3 VALID | 291 (291-294) | roots/second | 1,540 | 2,075 |
| pg | canonical | 320 | 4 UNSTABLE, 1 VALID; 2 added by the instability rule | 276 (268-308) | roots/second | 6,646 | 8,298 |

#### O03: Hot concurrency key

| Provider | Cell | Offered roots/s | Measurement | Rate, median (min-max) | Unit | End-to-end p50 ms | End-to-end p99 ms |
|---|---|---:|---|---:|---|---:|---:|
| pg | canonical | 1 | 3 UNSTABLE, 2 VALID; 2 added by the instability rule | 1 (1-1) | roots/second | 617 | 6,474 |

#### I05: Idempotent external sink

| Provider | Cell | Offered roots/s | Measurement | Rate, median (min-max) | Unit | End-to-end p50 ms | End-to-end p99 ms |
|---|---|---:|---|---:|---|---:|---:|
| pg | canonical | 400 | 3 UNSTABLE | 306 (304-309) | roots/second | 8,389 | 13,078 |
| pg | canonical | 480 | 5 UNSTABLE; 2 added by the instability rule | 311 (306-322) | roots/second | 14,653 | 22,844 |
| pg | canonical | 560 | 5 UNSTABLE; 2 added by the instability rule | 309 (304-435) | roots/second | 21,062 | 33,373 |
| pg | canonical | 640 | 3 UNSTABLE | 431 (317-441) | roots/second | 17,615 | 31,105 |

#### M03: Short/long mixed workload

| Provider | Cell | Offered roots/s | Measurement | Rate, median (min-max) | Unit | End-to-end p50 ms | End-to-end p99 ms |
|---|---|---:|---|---:|---|---:|---:|
| pg | canonical | 800 | 3 VALID | 815 (795-826) | roots/second | 287 | 2,109 |
| pg | canonical | 960 | 3 UNSTABLE | 837 (835-843) | roots/second | 3,764 | 11,546 |
| pg | canonical | 1,120 | 5 UNSTABLE; 2 added by the instability rule | 878 (847-968) | roots/second | 7,248 | 19,524 |
| pg | canonical | 1,280 | 5 UNSTABLE; 2 added by the instability rule | 889 (872-1,433) | roots/second | 9,198 | 27,912 |

#### M04: Payload sweep

| Provider | Cell | Offered roots/s | Measurement | Rate, median (min-max) | Unit | End-to-end p50 ms | End-to-end p99 ms |
|---|---|---:|---|---:|---|---:|---:|
| pg | canonical | 1,920 | 3 VALID | 1,930 (1,907-1,933) | roots/second | 539 | 1,107 |
| pg | canonical | 2,240 | 1 GENERATOR_LIMITED, 2 VALID | 2,248 (2,204-2,268) | roots/second | 542 | 1,054 |
| pg | canonical | 2,560 | 1 GENERATOR_LIMITED, 2 VALID | 2,605 (2,592-2,605) | roots/second | 527 | 1,043 |
| pg | payload-256 | 2,560 | 3 VALID | 2,548 (2,527-2,576) | roots/second | 533 | 1,026 |
| pg | payload-4096 | 2,560 | 3 VALID | 2,530 (2,514-2,553) | roots/second | 527 | 1,026 |
| pg | payload-65536 | 2,560 | 3 GENERATOR_LIMITED | 1,434 (1,306-1,480) | roots/second | 13,510 | 17,145 |

#### E01: Mixed-workload soak

| Provider | Cell | Offered roots/s | Measurement | Rate, median (min-max) | Unit | End-to-end p50 ms | End-to-end p99 ms |
|---|---|---:|---|---:|---|---:|---:|
| pg | canonical | 560 | 1 GENERATOR_LIMITED | 560 (560-560) | roots/second | 165 | 1,214 |

#### Overhead

| Measure | Value |
|---|---:|
| Run wall time | 12001 s |
| Evidence written | 21.5 MB of a 50 MB budget |
| Setup, median per trial | 0.812 s |
| Seeding, median per trial | 5E-07 s |
| Measured window and drain, median per trial | 40.8 s |
| Verification, median per trial | 0.317 s |
| Ledger checks, median per trial | 0.132 s |
| Evidence writing, median per trial | 0.00217 s |

| Disk, by phase and drive | Windows counters |
|---|---|
| before | 0 c: | write 2.37 ms mean, 8.28 ms p95, 12 writes/s, 99% idle, 14 samples |
| before | 1 d: | write 5.06 ms mean, 7.22 ms p95, 5 writes/s, 100% idle, 14 samples |
| pg | 0 c: | write 3.64 ms mean, 7.86 ms p95, 8 writes/s, 99% idle, 5949 samples |
| pg | 1 d: | write 0.32 ms mean, 0.55 ms p95, 1158 writes/s, 32% idle, 5949 samples |
| after | 0 c: | write 3.68 ms mean, 8.35 ms p95, 5 writes/s, 99% idle, 15 samples |
| after | 1 d: | write 0.09 ms mean, 0.18 ms p95, 18 writes/s, 99% idle, 15 samples |

| Slowest cases | Provider | Wall time s | Results |
|---|---|---:|---:|
| L01 | pg | 5,958 | 41 |
| M04 | pg | 1,343 | 27 |
| I05 | pg | 1,175 | 26 |
| M03 | pg | 1,014 | 24 |
| O03 | pg | 939 | 14 |
| E01 | pg | 617 | 1 |
| P01 | pg | 483 | 14 |
| C01 | pg | 252 | 11 |
| E03 | pg | 153 | 2 |

#### Not passing

| Case | Provider | Phase | Trial | Status | Safety | Liveness | Coverage | Measurement | Reason | Evidence |
|---|---|---|---:|---|---|---|---|---|---|---|
| O03 | pg | reconcile | 0 | INCONCLUSIVE | UNVERIFIED | UNVERIFIED | INCOMPLETE | NOT_MEASURED | No arrival rate passed all required independent validation trials; correctness results remain separate. |  |
| I05 | pg | reconcile | 0 | INCONCLUSIVE | UNVERIFIED | UNVERIFIED | INCOMPLETE | NOT_MEASURED | No arrival rate passed all required independent validation trials; correctness results remain separate. |  |
| E01 | pg | reconcile | 0 | INCONCLUSIVE | UNVERIFIED | UNVERIFIED | INCOMPLETE | NOT_MEASURED | Required sustained soak interval or measurement validity is incomplete. |  |

#### Notes

Measurement counts each trial's verdict: VALID qualifies it as capacity evidence, UNSTABLE means its latency target or backlog trend did not qualify, and GENERATOR_LIMITED or OBSERVER_LIMITED means the harness could not offer or observe the load.

A selected-case or quick PASS establishes only its declared scope. Capacity is qualified per workload and repetition, and missing evidence never establishes a guarantee. Process-crash tests do not certify physical power loss.

#### Environment

Protocol: `acta-bench.full/1`  
Build: `05e5c27cdc1adad696ae116e75ea2bf6140318a5` (source dirty: False)  
Run: `20261025T234949411Z-7714ef9c9e834ecfb24afe4a626a9637`  
architecture: X64  
artifact_disk_free_at_start_bytes: 236369747968  
build_configuration: Release  
compilation_mode: managed_jit  
cpu_model: Intel(R) Core(TM) i9-14900K  
database_backing_disk: Unavailable for Docker volumes; artifact-drive free space is guarded across the complete run.  
docker_vm_cpus: 32  
docker_vm_memory_bytes: 50379538432  
filesystem: ReFS  
gc_server: False  
hybrid_cpu: True  
machine_name: DESKTOP-MVKQH5M  
memory_available_to_runtime_bytes: 102818476032  
missing_metrics: Physical disk, per-database CPU/waits, live/allocation storage and network metrics require collectors. Whole-container CPU/memory are sampled where cgroup v2 is available; unavailable values are not zero.  
monotonic_clock_frequency: 10000000  
observer: Aggregate SQL counts during a trial and one paged read after it; completion times are database completion stamps on the controller stopwatch.  
os: Microsoft Windows 10.0.26300  
performance_logical_processors: 0,1,2,3,4,5,6,7,8,9,10,11,12,13,14,15  
pg_autovacuum: on  
pg_container_image_digest: sha256:74935e72241653ca55e0414067e6d8763aceb8a810eb51b452253ec3dcfc4336  
pg_container_memory_mb: 32031  
pg_container_shm_mb: 8007  
pg_effective_cache_size: 24023MB  
pg_maintenance_work_mem: 1024MB  
pg_max_connections: 2500  
pg_max_wal_size: 8192MB  
pg_server_mode: owned_container  
pg_shared_buffers: 8007MB  
physical_cores: 24  
preloaded_backlog_roots: sqlite=50000,pg=200000,mssql=200000  
processor_count: 32  
repetitions: 3  
run_wall_seconds: 12001  
runtime: .NET 10.0.12  
worker_compilation_mode: managed_jit  
workers_pinned_to_performance_cores: True  
