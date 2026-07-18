# Acta Engineering Labs

A curated index of runnable proof. An Engineering Lab is a small project in
[`concepts/`](../concepts/) whose README carries the full loop: the decision, the alternatives,
what the design costs, the exact rows that prove the behavior, what happens when a worker dies at
the worst moment, and the source files behind it. A few tour stops (`001-hello-acta`,
`802-testing-durable-jobs`, `901-native-aot-json`, `903-redis-wakeup`) are focused runnable proofs
rather than full labs. The explanations live beside the runnable code, where they stay honest.

Everything below runs on embedded SQLite with no server unless a lab says otherwise. From a fresh
clone: `dotnet run --project concepts/<category>/<lab>`.

## The 45-minute tour

Six labs, in order, that cover the model end to end:

1. [`001-hello-acta`](../concepts/000-fundamentals/001-hello-acta/): enqueue a job, watch a worker
   run it, inspect the row it left behind.
2. [`202-durable-step`](../concepts/200-durable-execution/202-durable-step/): a named step records
   its outcome; re-entry returns the stored result instead of repeating the work.
3. [`204-wait-signal`](../concepts/200-durable-execution/204-wait-signal/): a job suspends on a
   named signal without holding a worker, then resumes when the signal arrives.
4. [`705-worker-crash-recovery`](../concepts/700-topology-and-deployment/705-worker-crash-recovery/):
   kill a real worker process mid-job; watch the lease lapse and a peer reclaim the work
   (PostgreSQL/SQL Server).
5. [`022-dashboard`](../concepts/000-fundamentals/022-dashboard/): the embedded operator dashboard
   reading the same rows you can query yourself.
6. [`802-testing-durable-jobs`](../concepts/800-testing/802-testing-durable-jobs/): the
   deterministic test host drives the real runtime one tick at a time; real-database tests in tens
   of milliseconds.

## All labs

| Lab | What it proves |
| --- | --- |
| [`201-durable-checkout`](../concepts/200-durable-execution/201-durable-checkout/) | The flagship: a multi-step checkout that survives crashes between steps. |
| [`202-durable-step`](../concepts/200-durable-execution/202-durable-step/), [`220-at-most-once-step`](../concepts/200-durable-execution/220-at-most-once-step/) | Recorded step outcomes; refusing unsafe replay with `AtMostOnce()`. |
| [`103-multiple-schedules`](../concepts/100-scheduling/103-multiple-schedules/), [`106-schedule-misfire`](../concepts/100-scheduling/106-schedule-misfire/) | Recurring work on one stable row; explicit misfire policy. |
| [`705-worker-crash-recovery`](../concepts/700-topology-and-deployment/705-worker-crash-recovery/) | Leaderless recovery after a real process kill. |
| [`204-wait-signal`](../concepts/200-durable-execution/204-wait-signal/), [`205-durable-sleep`](../concepts/200-durable-execution/205-durable-sleep/) | Waiting for signals and timers without occupying workers. |
| [`021-jobs-cli`](../concepts/000-fundamentals/021-jobs-cli/) | The worker binary as admin tool, including Explain and `jobs debug` under a breakpoint. |
| [`501-payload-formats`](../concepts/500-payloads/501-payload-formats/) | JSON, MessagePack, and gzip payload codecs per job. |
| [`211-child-jobs`](../concepts/200-durable-execution/211-child-jobs/) | Fan-out as ordinary jobs with recorded lineage. |
| [`022-dashboard`](../concepts/000-fundamentals/022-dashboard/) | The embedded dashboard and opt-in operator controls. |
| [`209-exclusive-key`](../concepts/200-durable-execution/209-exclusive-key/) | Serializing hot keys without blocking unrelated workers. |
| [`412-tenant-scope`](../concepts/400-observability-and-alerts/412-tenant-scope/) | A tenant as an audit boundary without becoming a queue. |
| [`310-operator-restart`](../concepts/300-failure-and-recovery/310-operator-restart/) | Restart that preserves evidence without pretending to be exactly-once. |
| [`903-redis-wakeup`](../concepts/900-runtime-and-tuning/903-redis-wakeup/) | Redis as a wakeup bell only, never a source of truth. |
| [`901-native-aot-json`](../concepts/900-runtime-and-tuning/901-native-aot-json/) | Jobs under Native AOT with source-generated JSON. |

Beyond the labs, the full [`concepts/`](../concepts/) ladder holds 88 runnable projects from
fundamentals through runtime tuning; [`docs/guide/tutorials.md`](./guide/tutorials.md) sequences
them.

## Engine-room proofs

Some claims are proven by suites rather than sample projects: the cross-provider conformance suite
(one contract certifying PostgreSQL, SQL Server, and SQLite), the SQL policy guardrails over
handwritten SQL, the shared real-database test host, batched Bulk completions, PostgreSQL batch
enqueue staging, and the benchmark baselines in [`docs/benchmarks/`](./benchmarks/stress-tests.md).
Anvil (`anvil/Anvil`) is the load-and-failure laboratory: enqueue a million jobs, kill real worker
processes, and watch recovery, with the dashboard attached.
