# Acta Engineering Labs

A curated index of runnable proof. An Engineering Lab is a small project in
[`concepts/`](../concepts/) whose README carries the full loop: the decision, the alternatives,
what the design costs, the exact rows that prove the behavior, what happens when a worker dies at
the worst moment, and the source files behind it. A few entries (`001-hello-acta`, `802-testing-durable-jobs`,
`901-native-aot-json`, `903-redis-wakeup`) are focused runnable proofs rather than full labs. The explanations live beside the runnable code, where they cannot drift from it.

Everything below runs on embedded SQLite with no server, except `705-worker-crash-recovery`
(PostgreSQL or SQL Server) and `903-redis-wakeup` (Redis on `localhost:6379`). From a fresh
clone: `dotnet run --project concepts/<category>/<lab>`.

## The 45-minute tour

These labs, in order, cover the model end to end:

1. [`001-hello-acta`](../concepts/000-fundamentals/001-hello-acta/): enqueue a job and watch a worker
   run it.
2. [`202-durable-step`](../concepts/200-durable-execution/202-durable-step/): a named step records
   its outcome; re-entry returns the stored result instead of repeating the work.
3. [`204-wait-signal`](../concepts/200-durable-execution/204-wait-signal/): a job suspends on a
   named signal without holding a worker, then resumes when the signal arrives.
4. [`109-continuous-synchronization`](../concepts/100-scheduling/109-continuous-synchronization/):
   one permanent job keeps a web shop's orders synchronized from a watermark it holds as a durable
   variable; kill the worker after a batch and watch the restarted one reclaim the same firing and
   continue at the watermark.
5. [`705-worker-crash-recovery`](../concepts/700-topology-and-deployment/705-worker-crash-recovery/):
   kill a real worker process mid-job; watch the lease lapse and a peer reclaim the work
   (PostgreSQL/SQL Server).
6. [`022-dashboard`](../concepts/000-fundamentals/022-dashboard/): the embedded operator dashboard
   reading the same rows you can query yourself.
7. [`802-testing-durable-jobs`](../concepts/800-testing/802-testing-durable-jobs/): the
   deterministic test host drives the real runtime one tick at a time; real-database tests in tens
   of milliseconds.

## All labs

| Lab | What it proves |
| --- | --- |
| [`201-durable-checkout`](../concepts/200-durable-execution/201-durable-checkout/) | The flagship: a checkout that waits on a review and a timer and re-enters from the top, each completed step replayed from its row. |
| [`202-durable-step`](../concepts/200-durable-execution/202-durable-step/), [`220-at-most-once-step`](../concepts/200-durable-execution/220-at-most-once-step/) | Recorded step outcomes; refusing unsafe replay with `AtMostOnce()`. |
| [`109-continuous-synchronization`](../concepts/100-scheduling/109-continuous-synchronization/) | One permanent job keeps an external system synchronized: a firing every five seconds applies one bounded batch and returns the job to `Idle`; the watermark is a durable variable on the job, and it and Acta's schedule cursor move apart; a worker killed after a batch is reclaimed and the next execution continues at the watermark. |
| [`103-multiple-schedules`](../concepts/100-scheduling/103-multiple-schedules/), [`106-schedule-misfire`](../concepts/100-scheduling/106-schedule-misfire/) | Recurring work on one stable row; explicit misfire policy. |
| [`705-worker-crash-recovery`](../concepts/700-topology-and-deployment/705-worker-crash-recovery/) | Leaderless recovery after a real process kill. |
| [`204-wait-signal`](../concepts/200-durable-execution/204-wait-signal/), [`205-durable-sleep`](../concepts/200-durable-execution/205-durable-sleep/) | Waiting for signals and timers without occupying workers. |
| [`021-jobs-cli`](../concepts/000-fundamentals/021-jobs-cli/) | The worker binary as admin tool, including Explain and `jobs debug` under a breakpoint. |
| [`501-payload-formats`](../concepts/500-payloads/501-payload-formats/) | JSON, gzip JSON, and custom scalar payload codecs per job. |
| [`211-child-jobs`](../concepts/200-durable-execution/211-child-jobs/) | Fan-out as ordinary jobs with recorded lineage. |
| [`022-dashboard`](../concepts/000-fundamentals/022-dashboard/) | The embedded dashboard and opt-in operator controls. |
| [`209-concurrency-key`](../concepts/200-durable-execution/209-concurrency-key/) | Admitting hot keys one or N at a time, and metering starts, without blocking unrelated workers. |
| [`412-tenant-scope`](../concepts/400-observability-and-alerts/412-tenant-scope/) | A tenant as an audit boundary without becoming a queue. |
| [`310-operator-restart`](../concepts/300-failure-and-recovery/310-operator-restart/) | Restart that preserves evidence without pretending to be exactly-once. |
| [`903-redis-wakeup`](../concepts/900-runtime-and-tuning/903-redis-wakeup/) | Redis as a wakeup bell only, never a source of truth. |
| [`901-native-aot-json`](../concepts/900-runtime-and-tuning/901-native-aot-json/) | Jobs under Native AOT with source-generated JSON. |

Beyond the labs, the [`concepts/`](../concepts/) ladder holds a runnable project for most concepts, from
fundamentals through runtime tuning (the outbox is covered by the guides, not yet by a rung);
[the concept ladder](#the-concept-ladder) below lists every rung.

## Engine-room proofs

Some claims are proven by suites rather than sample projects: the cross-provider conformance suite
(one contract that PostgreSQL, SQL Server, and SQLite all pass), the SQL policy guardrails over
handwritten SQL, the shared real-database test host, PostgreSQL batch enqueue staging, and the
latest benchmark round in [`docs/reference/benchmark.md`](./reference/benchmark.md).
Anvil (`anvil/Anvil`) is the load-and-failure laboratory: enqueue a million jobs, kill real worker
processes, and watch recovery, with the dashboard attached.

## The concept ladder

Every rung is a small project in `concepts/` that teaches one idea, numbered in bands so the ladder can
grow without renumbering. Run one with `dotnet run --project concepts/<band>/<rung>`. Most rungs
enqueue work and keep the worker running until Ctrl+C; the ones whose lesson is a value read back
(003, 012, 013, 015, 016) exit on their own, and the 800 band runs with `dotnet test`. Every rung but 705
defaults to SQLite; set `ACTA_LOCAL_PROVIDER=postgres` or `sqlserver` with `ACTA_TEST_PG` or
`ACTA_TEST_MSSQL` to run it on a server ([CONTRIBUTING.md](../CONTRIBUTING.md) has the setup).
Engineering Labs also accept `--brief` to skip row output and `--all-columns` to show complete
curated-view rows.

| Band | Topic |
| --- | --- |
| `001-099` | Fundamentals: define, enqueue, status, result, and operator surfaces |
| `100-199` | Scheduling |
| `200-299` | Durable execution (steps, variables, signals, sleep, locks, child jobs) |
| `300-399` | Failure and recovery |
| `400-499` | Observability and alerts |
| `500-599` | Payloads |
| `600-699` | Job composition through enqueue |
| `700-799` | Topology and deployment |
| `800-899` | Testing |
| `900-999` | Runtime and tuning (AOT, Redis wakeup, worker configuration) |

Every rung, with what it shows and the how-to written from it, is listed in
[the concept ladder](./reference/concept-ladder.md). That file is generated from the programs: a rung's
title and one-line summary are its program's opening comment, and the steps of its how-to are the lines
the program marks with `// learn:` ([CONTRIBUTING.md](../CONTRIBUTING.md), "Concept programs").

The production capstone is a demo, not a rung: [`demos/ApiWorkerSplit`](../demos/ApiWorkerSplit/) runs an
enqueue-only API and an independently scalable worker as separate processes. Open its focused solution
and press F5. Other multi-project apps live under [`demos/`](../demos/).
