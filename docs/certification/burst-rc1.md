# Alert burst certification — v1.0.0-rc.1

The plan's C6 gate, run 2026-08-22 on the near-final rc.1 commit `e016156` with
`anvil/Anvil.Burst`: seed a backlog of failed jobs, age their events past the projection horizon,
then let the production `sys.alerts` path drain it while the harness measures. The stated pass
condition: **a 10,000-event backlog projects within one bounded invocation and drains within two
minutes**; a 100,000 backlog proves bounded memory and continued forward progress. Five runs, five
passes — and by the plan's own decision rule, the batched-SQL fallback (C7) stays unbuilt.

The headline numbers: 10K projects in **one invocation** and drains in **12 seconds** against the
120-second budget on both server providers (2.3 s on SQLite); 100K drains in ten invocations at a
**110–122 MB peak working set**, every invocation moving a full batch. The dashboard's alert list
answers in tens of milliseconds with 100,000 alert rows in the table.

Three things to know before reading the blocks:

- **The reminder interval is 1 second in the harness**, not the shipped 24 hours — that is what
  makes `resolved-not-delivered` non-vacuous inside a run, and it is why `delivery-cap` still
  reports full 256-attempt invocations long after projection finished: those slots are reminders
  re-sending, exactly as a 24-hour cadence would at scale.
- **The one-second pagination ceiling is the harness's chosen threshold.** The plan says only
  "stays responsive"; the measured pages came in two orders of magnitude under the ceiling.
- **100K on SQLite is deliberately absent.** One writer means the seeding phase would dominate the
  run for no additional signal; the 10K SQLite run carries that provider's evidence.

## 10,000 events

```
  ACTA ALERT BURST CERTIFICATION  |  postgres  |  anvil_burst_b20260822_051830_3eabad  |  10,000 events

  [note] backlog-seeded               10,000 failed jobs over 4 definitions in 10,1s
  [ok  ] backlog-unprojected          alerts=0 cursor=0 before the first invocation (both schedules parked through seeding)
  [ok  ] backlog-projected            projected=10,000 of 10,000 seeded events in 1 invocation(s)
  [ok  ] incidents-materialized       alert rows=10,000 expected=10,000 (one open incident per failed job)
  [ok  ] projected-one-invocation     invocations that projected work=1 (a 10,000 backlog fits the 10,240-event bound)
  [ok  ] drain-wall-clock             12,2s (budget 120s, includes the harness poll between invocations)
  [ok  ] forward-progress             every invocation projected a full batch (min 10,000, max 10,000 events)
  [note] batches-per-invocation       min 40, max 40 of the 40 allowed
  [note] peak-working-set             116 MB peak over the drain, 131 MB allocated, 35 peak threads
  [ok  ] self-healed-zero-open        healed jobs=200 unresolved alerts left=0
  [ok  ] resolved-not-delivered       200 incident(s) were being re-sent before the heal; after it 0 were sent across two invocations that made 512 attempts
  [note] alerts-page-latency          p1=5ms p2=6ms p5=4ms p10=4ms p25=4ms p50=3ms over 100 page(s) of 100; slowest p2=6ms (page 1 includes the filter-wide count of 10,000)
  [ok  ] alerts-page-under-1s         slowest page 6ms at page 2
  [ok  ] retention-eligible           aged 200 open incident(s) past the cap (200 of them never delivered); 200 purged by one sys.retention pass, 0 left
  [ok  ] delivery-cap                 max 256 external attempts in one invocation (#1) of 7, cap 256

  PASS - every asserted burst property held.
```

```
  ACTA ALERT BURST CERTIFICATION  |  sqlserver  |  anvil_burst_b20260822_051912_9d5559  |  10,000 events

  [note] backlog-seeded               10,000 failed jobs over 4 definitions in 5,1s
  [ok  ] backlog-unprojected          alerts=0 cursor=0 before the first invocation (both schedules parked through seeding)
  [ok  ] backlog-projected            projected=10,000 of 10,000 seeded events in 1 invocation(s)
  [ok  ] incidents-materialized       alert rows=10,000 expected=10,000 (one open incident per failed job)
  [ok  ] projected-one-invocation     invocations that projected work=1 (a 10,000 backlog fits the 10,240-event bound)
  [ok  ] drain-wall-clock             12,8s (budget 120s, includes the harness poll between invocations)
  [ok  ] forward-progress             every invocation projected a full batch (min 10,000, max 10,000 events)
  [note] batches-per-invocation       min 40, max 40 of the 40 allowed
  [note] peak-working-set             117 MB peak over the drain, 242 MB allocated, 32 peak threads
  [ok  ] self-healed-zero-open        healed jobs=200 unresolved alerts left=0
  [ok  ] resolved-not-delivered       200 incident(s) were being re-sent before the heal; after it 0 were sent across two invocations that made 512 attempts
  [note] alerts-page-latency          p1=7ms p2=20ms p5=4ms p10=4ms p25=5ms p50=5ms over 100 page(s) of 100; slowest p2=20ms (page 1 includes the filter-wide count of 10,000)
  [ok  ] alerts-page-under-1s         slowest page 20ms at page 2
  [ok  ] retention-eligible           aged 200 open incident(s) past the cap (200 of them never delivered); 200 purged by one sys.retention pass, 0 left
  [ok  ] delivery-cap                 max 256 external attempts in one invocation (#1) of 7, cap 256

  PASS - every asserted burst property held.
```

```
  ACTA ALERT BURST CERTIFICATION  |  sqlite  |  anvil_burst_b20260822_051944_a9f8f1  |  10,000 events

  [note] backlog-seeded               10,000 failed jobs over 4 definitions in 15,0s
  [ok  ] backlog-unprojected          alerts=0 cursor=0 before the first invocation (both schedules parked through seeding)
  [ok  ] backlog-projected            projected=10,000 of 10,000 seeded events in 1 invocation(s)
  [ok  ] incidents-materialized       alert rows=10,000 expected=10,000 (one open incident per failed job)
  [ok  ] projected-one-invocation     invocations that projected work=1 (a 10,000 backlog fits the 10,240-event bound)
  [ok  ] drain-wall-clock             2,3s (budget 120s, includes the harness poll between invocations)
  [ok  ] forward-progress             every invocation projected a full batch (min 10,000, max 10,000 events)
  [note] batches-per-invocation       min 40, max 40 of the 40 allowed
  [note] peak-working-set             139 MB peak over the drain, 169 MB allocated, 19 peak threads
  [ok  ] self-healed-zero-open        healed jobs=200 unresolved alerts left=0
  [ok  ] resolved-not-delivered       200 incident(s) were being re-sent before the heal; after it 0 were sent across two invocations that made 512 attempts
  [note] alerts-page-latency          p1=10ms p2=10ms p5=8ms p10=8ms p25=7ms p50=5ms over 100 page(s) of 100; slowest p1=10ms (page 1 includes the filter-wide count of 10,000)
  [ok  ] alerts-page-under-1s         slowest page 10ms at page 1
  [ok  ] retention-eligible           aged 200 open incident(s) past the cap (200 of them never delivered); 200 purged by one sys.retention pass, 0 left
  [ok  ] delivery-cap                 max 256 external attempts in one invocation (#1) of 7, cap 256

  PASS - every asserted burst property held.
```

## 100,000 events

```
  ACTA ALERT BURST CERTIFICATION  |  postgres  |  anvil_burst_b20260822_052011_a77696  |  100,000 events

  [note] backlog-seeded               100,000 failed jobs over 4 definitions in 66,6s
  [ok  ] backlog-unprojected          alerts=0 cursor=0 before the first invocation (both schedules parked through seeding)
  [ok  ] backlog-projected            projected=100,000 of 100,000 seeded events in 10 invocation(s)
  [ok  ] incidents-materialized       alert rows=100,000 expected=100,000 (one open incident per failed job)
  [n/a ] projected-one-invocation     a 100,000 backlog exceeds the 10,240-event invocation bound by design
  [n/a ] drain-wall-clock             stated for the 10K backlog only; this run drained in 117,4s
  [ok  ] forward-progress             every invocation projected a full batch (min 7,840, max 10,240 events)
  [note] batches-per-invocation       min 31, max 40 of the 40 allowed
  [note] peak-working-set             110 MB peak over the drain, 1288 MB allocated, 31 peak threads
  [ok  ] self-healed-zero-open        healed jobs=200 unresolved alerts left=0
  [ok  ] resolved-not-delivered       200 incident(s) were being re-sent before the heal; after it 0 were sent across two invocations that made 512 attempts
  [note] alerts-page-latency          p1=33ms p2=22ms p5=15ms p10=14ms p25=14ms p50=15ms over 200 page(s) of 100; slowest p1=33ms (page 1 includes the filter-wide count of 100,000)
  [ok  ] alerts-page-under-1s         slowest page 33ms at page 1
  [ok  ] retention-eligible           aged 200 open incident(s) past the cap (200 of them never delivered); 200 purged by one sys.retention pass, 0 left
  [ok  ] delivery-cap                 max 256 external attempts in one invocation (#1) of 16, cap 256

  PASS - every asserted burst property held.
```

```
  ACTA ALERT BURST CERTIFICATION  |  sqlserver  |  anvil_burst_b20260822_052342_2f26af  |  100,000 events

  [note] backlog-seeded               100,000 failed jobs over 4 definitions in 36,0s
  [ok  ] backlog-unprojected          alerts=0 cursor=0 before the first invocation (both schedules parked through seeding)
  [ok  ] backlog-projected            projected=100,000 of 100,000 seeded events in 10 invocation(s)
  [ok  ] incidents-materialized       alert rows=100,000 expected=100,000 (one open incident per failed job)
  [n/a ] projected-one-invocation     a 100,000 backlog exceeds the 10,240-event invocation bound by design
  [n/a ] drain-wall-clock             stated for the 10K backlog only; this run drained in 126,5s
  [ok  ] forward-progress             every invocation projected a full batch (min 7,840, max 10,240 events)
  [note] batches-per-invocation       min 31, max 40 of the 40 allowed
  [note] peak-working-set             122 MB peak over the drain, 2358 MB allocated, 23 peak threads
  [ok  ] self-healed-zero-open        healed jobs=200 unresolved alerts left=0
  [ok  ] resolved-not-delivered       200 incident(s) were being re-sent before the heal; after it 0 were sent across two invocations that made 512 attempts
  [note] alerts-page-latency          p1=18ms p2=6ms p5=4ms p10=4ms p25=4ms p50=5ms over 200 page(s) of 100; slowest p1=18ms (page 1 includes the filter-wide count of 100,000)
  [ok  ] alerts-page-under-1s         slowest page 18ms at page 1
  [ok  ] retention-eligible           aged 200 open incident(s) past the cap (200 of them never delivered); 200 purged by one sys.retention pass, 0 left
  [ok  ] delivery-cap                 max 256 external attempts in one invocation (#1) of 16, cap 256

  PASS - every asserted burst property held.
```

## Reproducing

`anvil/Anvil.Burst` mints a fresh `anvil_burst_*` schema per run and leaves it behind for
inspection. The exact command lines, flag meanings, and the harness's measurement seams are
documented in the runner's own source; the SQL Server 10K run exists in part to exercise the
`DateTime2` branch of the raw-SQL instant binding that neither other provider touches.
