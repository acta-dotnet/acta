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

Six labs, in order, that cover the model end to end:

1. [`001-hello-acta`](../concepts/000-fundamentals/001-hello-acta/): enqueue a job and watch a worker
   run it.
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
| [`201-durable-checkout`](../concepts/200-durable-execution/201-durable-checkout/) | The flagship: a checkout that waits on a review and a timer and re-enters from the top, each completed step replayed from its row. |
| [`202-durable-step`](../concepts/200-durable-execution/202-durable-step/), [`220-at-most-once-step`](../concepts/200-durable-execution/220-at-most-once-step/) | Recorded step outcomes; refusing unsafe replay with `AtMostOnce()`. |
| [`103-multiple-schedules`](../concepts/100-scheduling/103-multiple-schedules/), [`106-schedule-misfire`](../concepts/100-scheduling/106-schedule-misfire/) | Recurring work on one stable row; explicit misfire policy. |
| [`705-worker-crash-recovery`](../concepts/700-topology-and-deployment/705-worker-crash-recovery/) | Leaderless recovery after a real process kill. |
| [`204-wait-signal`](../concepts/200-durable-execution/204-wait-signal/), [`205-durable-sleep`](../concepts/200-durable-execution/205-durable-sleep/) | Waiting for signals and timers without occupying workers. |
| [`021-jobs-cli`](../concepts/000-fundamentals/021-jobs-cli/) | The worker binary as admin tool, including Explain and `jobs debug` under a breakpoint. |
| [`501-payload-formats`](../concepts/500-payloads/501-payload-formats/) | JSON, MessagePack, and gzip payload codecs per job. |
| [`211-child-jobs`](../concepts/200-durable-execution/211-child-jobs/) | Fan-out as ordinary jobs with recorded lineage. |
| [`022-dashboard`](../concepts/000-fundamentals/022-dashboard/) | The embedded dashboard and opt-in operator controls. |
| [`209-concurrency-key`](../concepts/200-durable-execution/209-concurrency-key/) | Admitting hot keys one or N at a time, and metering starts, without blocking unrelated workers. |
| [`412-tenant-scope`](../concepts/400-observability-and-alerts/412-tenant-scope/) | A tenant as an audit boundary without becoming a queue. |
| [`310-operator-restart`](../concepts/300-failure-and-recovery/310-operator-restart/) | Restart that preserves evidence without pretending to be exactly-once. |
| [`903-redis-wakeup`](../concepts/900-runtime-and-tuning/903-redis-wakeup/) | Redis as a wakeup bell only, never a source of truth. |
| [`901-native-aot-json`](../concepts/900-runtime-and-tuning/901-native-aot-json/) | Jobs under Native AOT with source-generated JSON. |

Beyond the labs, the [`concepts/`](../concepts/) ladder holds a runnable project for most concepts, from
fundamentals through runtime tuning (lanes and the outbox are covered by the guides, not yet by a
rung); [the concept ladder](#the-concept-ladder) below lists every rung.

## Engine-room proofs

Some claims are proven by suites rather than sample projects: the cross-provider conformance suite
(one contract certifying PostgreSQL, SQL Server, and SQLite), the SQL policy guardrails over
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

| # | Rung | Teaches |
| --- | --- | --- |
| 001 | [hello-acta](../concepts/000-fundamentals/001-hello-acta/) | One `[Job]`, fire-and-forget enqueue, a worker runs it |
| 002 | [job-input](../concepts/000-fundamentals/002-job-input/) | Typed input record, no result |
| 003 | [job-result](../concepts/000-fundamentals/003-job-result/) | A job returns a typed result; read it with `GetResultAsync<T>` |
| 004 | [no-input-job](../concepts/000-fundamentals/004-no-input-job/) | A job with no input (`NoInput`) |
| 005 | [scalar-input](../concepts/000-fundamentals/005-scalar-input/) | A bare scalar input, no request record |
| 006 | [many-jobs-one-class](../concepts/000-fundamentals/006-many-jobs-one-class/) | Several `[Job]` methods in one class |
| 007 | [dependency-injection](../concepts/000-fundamentals/007-dependency-injection/) | Constructor-injected services in a handler |
| 008 | [cancellation-token](../concepts/000-fundamentals/008-cancellation-token/) | Async handler honoring `CancellationToken` |
| 009 | [typed-enqueue](../concepts/000-fundamentals/009-typed-enqueue/) | `EnqueueAsync` routes by the input type (no name string) |
| 010 | [deduplication-key](../concepts/000-fundamentals/010-deduplication-key/) | Idempotent enqueue (dedupe by deduplication key) |
| 011 | [delayed-job](../concepts/000-fundamentals/011-delayed-job/) | Delay the earliest run with `Delayed` |
| 012 | [read-status](../concepts/000-fundamentals/012-read-status/) | Read a `GetJobResponse` with `GetAsync` |
| 013 | [read-result](../concepts/000-fundamentals/013-read-result/) | `GetResultAsync<T>` is a point-in-time read (null before Succeeded) |
| 014 | [read-by-deduplication-key](../concepts/000-fundamentals/014-read-by-deduplication-key/) | Look a job up by `JobLookup.ByDeduplicationKey` |
| 015 | [execute-vs-enqueue](../concepts/000-fundamentals/015-execute-vs-enqueue/) | Fire-and-forget vs enqueue-and-wait |
| 016 | [max-attempts](../concepts/000-fundamentals/016-max-attempts/) | Retry budget; terminal `Failed` after the cap |
| 017 | [priority](../concepts/000-fundamentals/017-priority/) | Claim-order priority on enqueue |
| 018 | [tags](../concepts/000-fundamentals/018-tags/) | Annotate a job with tags |
| 019 | [batch-enqueue](../concepts/000-fundamentals/019-batch-enqueue/) | Enqueue many jobs in one round-trip |
| 020 | [raw-enqueue](../concepts/000-fundamentals/020-raw-enqueue/) | Enqueue by explicit (namespace, job-name) + payload |
| 021 | [jobs-cli](../concepts/000-fundamentals/021-jobs-cli/) | **Engineering Lab.** Same-binary `info`/`events`/`explain`/`debug` over one signal-waiting identity |
| 022 | [dashboard](../concepts/000-fundamentals/022-dashboard/) | **Engineering Lab.** Embedded dashboard seeded with varied states; experience controls-disabled safety first |
| 023 | [job-contract](../concepts/000-fundamentals/023-job-contract/) | Enqueue by a generated, compile-checked `JobContract` token (no input-type inference) |
| 024 | [aspnet-enqueue-api](../concepts/000-fundamentals/024-aspnet-enqueue-api/) | Minimal HTTP API: POST returns 202 + JobRef; GET polls status/result; deduplication key, correlation id, tags |
| 025 | [large-payload-reference](../concepts/000-fundamentals/025-large-payload-reference/) | Store big files in blob/object storage; enqueue a verified reference (URI + checksum + size), not the bytes |
| 028 | [deduplication-time-buckets](../concepts/000-fundamentals/028-deduplication-time-buckets/) | Time-bucketed deduplication keys (`PerHour`/`PerDay`/`PerTimeBucket`) dedupe within a bucket, insert across it |
| 101 | [recurring-job](../concepts/100-scheduling/101-recurring-job/) | `[JobSchedule]` cron recurring slot - fires itself, no enqueue |
| 102 | [interval-schedule](../concepts/100-scheduling/102-interval-schedule/) | Replace a simple `PeriodicTimer` cleanup with an interval schedule (`10s`) |
| 103 | [multiple-schedules](../concepts/100-scheduling/103-multiple-schedules/) | **Engineering Lab.** One recurring job row, several moving schedule cursors, event-backed occurrence history |
| 104 | [timezone-schedule](../concepts/100-scheduling/104-timezone-schedule/) | A cron schedule pinned to a wall clock (`TimeZoneId = "Europe/Ljubljana"`), DST-safe |
| 105 | [schedule-control](../concepts/100-scheduling/105-schedule-control/) | Pause (indefinite + timed) and resume a recurring schedule with `operations.Schedules` |
| 106 | [schedule-misfire](../concepts/100-scheduling/106-schedule-misfire/) | **Engineering Lab.** Watch `Skip` and `CatchUpOnce` move overdue cursor rows differently |
| 201 | [durable-checkout](../concepts/200-durable-execution/201-durable-checkout/) | **Engineering Lab flagship.** Durable orchestration as inspectable jobs, steps, checkpoints, events, and results |
| 202 | [durable-step](../concepts/200-durable-execution/202-durable-step/) | **Engineering Lab.** Completed step outcome replays while the job advances to execution two |
| 203 | [durable-variable](../concepts/200-durable-execution/203-durable-variable/) | A durable variable pins a value (a sync watermark) across retries |
| 204 | [wait-signal](../concepts/200-durable-execution/204-wait-signal/) | **Engineering Lab.** Prove a signal-waiting job is suspended, durable, and holds no worker lease |
| 205 | [durable-sleep](../concepts/200-durable-execution/205-durable-sleep/) | **Engineering Lab.** Compare process-local delay with a timer checkpoint and handler re-entry |
| 206 | [step-chain](../concepts/200-durable-execution/206-step-chain/) | Chained typed steps: each `RunStepAsync<T>` output feeds the next |
| 207 | [run-with-lock](../concepts/200-durable-execution/207-run-with-lock/) | A named lock serializes jobs touching the same resource |
| 208 | [progress](../concepts/200-durable-execution/208-progress/) | Report progress to durable state (`SetProgressAsync`) + a live bar |
| 209 | [concurrency-key](../concepts/200-durable-execution/209-concurrency-key/) | **Engineering Lab.** Whole-job admission: inspect the named lease and a budget-neutral competitor bounce |
| 210 | [step-retry](../concepts/200-durable-execution/210-step-retry/) | A step retries on its own curve, sparing the job's budget |
| 211 | [child-jobs](../concepts/200-durable-execution/211-child-jobs/) | **Engineering Lab.** Independent child rows plus parent-owned latches; suspended parent holds no worker |
| 212 | [fan-out-join](../concepts/200-durable-execution/212-fan-out-join/) | Map-reduce with `MapAsync`: chunk children compute partial sums in parallel, the parent merges their results |
| 213 | [execute-child](../concepts/200-durable-execution/213-execute-child/) | `ExecuteChildAsync` delegates to an existing job and waits for its result (vs an step) |
| 214 | [reset-state](../concepts/200-durable-execution/214-reset-state/) | A recurring monitor uses durable state, then `ctx.ResetStateAsync` so the next fire starts blank |
| 215 | [map-parallel-join](../concepts/200-durable-execution/215-map-parallel-join/) | `ParallelAsync`, `MapAsync`, and `JoinAsync` fan out child jobs and wait, all over the same latches |
| 216 | [variable-lifecycle](../concepts/200-durable-execution/216-variable-lifecycle/) | Durable-variable lifecycle: `GetOrSet` (compute-once), `Exists`, `Delete`, defaults, raw `JobPayload` |
| 217 | [absolute-time-controls](../concepts/200-durable-execution/217-absolute-time-controls/) | Absolute-instant timing: `NextRunAt`, `SleepUntilAsync`, `RescheduleUntilAsync` |
| 218 | [global-lock](../concepts/200-durable-execution/218-global-lock/) | A `LockScope.Global` lock serializes jobs across namespaces (vs 207's namespace scope) |
| 219 | [child-failure-outcomes](../concepts/200-durable-execution/219-child-failure-outcomes/) | Child-group failure handling: `MapOutcome`/`ParallelOutcome`/`JoinOutcome` and `ThrowIfAnyFailed` |
| 220 | [at-most-once-step](../concepts/200-durable-execution/220-at-most-once-step/) | **Engineering Lab.** Real process loss after a side effect: refuse body replay, surface ambiguity, reconcile |
| 301 | [fail-job](../concepts/300-failure-and-recovery/301-fail-job/) | `ctx.FailAsync` ends the job permanently - no retries (unlike throwing) |
| 302 | [cancel-job](../concepts/300-failure-and-recovery/302-cancel-job/) | `ctx.CancelAsync` ends the job as Cancelled |
| 303 | [pause-job](../concepts/300-failure-and-recovery/303-pause-job/) | `ctx.PauseAsync` parks the job as Paused, awaiting resume |
| 304 | [execution-timeout](../concepts/300-failure-and-recovery/304-execution-timeout/) | `ExecutionTimeout` trips the CancellationToken on a too-slow attempt |
| 305 | [operator-cancel](../concepts/300-failure-and-recovery/305-operator-cancel/) | `IJobs.CancelAsync` cancels a running job from outside |
| 306 | [reschedule](../concepts/300-failure-and-recovery/306-reschedule/) | Self-reschedule to poll-and-wait, without burning the retry budget |
| 307 | [deadline-strict](../concepts/300-failure-and-recovery/307-deadline-strict/) | A whole-job `Deadline` (Strict) abandons an overdue job at admission; the handler never runs |
| 308 | [deadline-advisory](../concepts/300-failure-and-recovery/308-deadline-advisory/) | An Advisory `Deadline` still runs the handler; it reads `ctx.IsOverdue` and degrades gracefully |
| 309 | [operator-resume](../concepts/300-failure-and-recovery/309-operator-resume/) | Pause and resume a job from outside via `IJobs.PauseAsync`/`ResumeAsync` (Applied vs Rejected) |
| 310 | [operator-restart](../concepts/300-failure-and-recovery/310-operator-restart/) | **Engineering Lab.** Same identity, reset budget, appended history, retained step, repeated bare handler code |
| 311 | [execute-outcome-timeout](../concepts/300-failure-and-recovery/311-execute-outcome-timeout/) | Read a `JobOutcome`: `WaitTimeout`, `ThrowIfFailed`, `TryGetValue`, `ValueOrThrow` |
| 312 | [backoff-curve](../concepts/300-failure-and-recovery/312-backoff-curve/) | Typed retry backoff curve via the `Backoff` DSL string (e.g. `1s..8s x2 exact`) |
| 401 | [pipeline-behavior](../concepts/400-observability-and-alerts/401-pipeline-behavior/) | `IJobPipelineBehavior` wraps every handler, like middleware |
| 402 | [alerts](../concepts/400-observability-and-alerts/402-alerts/) | Raise an operator alert from a handler (`ctx.AlertAsync`), then read it back via `operations.Alerts` |
| 403 | [alert-channel](../concepts/400-observability-and-alerts/403-alert-channel/) | Declare a custom alert channel (`AddAlertChannel`) and route an alert to it by name |
| 404 | [read-event-timeline](../concepts/400-observability-and-alerts/404-read-event-timeline/) | Read the `JobEvent` timeline ("why did this happen?") across a job lineage |
| 405 | [real-alert-routing](../concepts/400-observability-and-alerts/405-real-alert-routing/) | A Slack-webhook channel from configuration, logging when none is set, with a severity floor; a deduplication key collapses repeats of one incident |
| 406 | [automatic-failure-alerts](../concepts/400-observability-and-alerts/406-automatic-failure-alerts/) | An automatic failure alert via `[Job(AlertProfile=...)]`; same-job repeats dedupe and count up |
| 407 | [audit-level](../concepts/400-observability-and-alerts/407-audit-level/) | `AuditLevel` (Off/Failures/Audit) changes how many `JobEvent`s a job emits |
| 408 | [correlation-id](../concepts/400-observability-and-alerts/408-correlation-id/) | Propagate a `CorrelationKey` parent->child (read via log scope / raw SQL) |
| 409 | [operator-queries](../concepts/400-observability-and-alerts/409-operator-queries/) | `ILedger` reads: filters, `NextCursor` paging, `IncludeTotal`, `GetOverviewAsync` |
| 410 | [http-api-controls](../concepts/400-observability-and-alerts/410-http-api-controls/) | HTTP control API: `MapActaApi` + `EnableControls`, the `X-Acta-Control` header, `LocalOnly` |
| 411 | [alert-escalation](../concepts/400-observability-and-alerts/411-alert-escalation/) | Alert escalation stages: FirstFailure -> ThresholdReached -> FinalFailure |
| 412 | [tenant-scope](../concepts/400-observability-and-alerts/412-tenant-scope/) | **Engineering Lab.** Tenant catalog resolution, child inheritance, filtering, and unknown/suspended rejection |
| 501 | [payload-formats](../concepts/500-payloads/501-payload-formats/) | **Engineering Lab.** Custom formats compared by serialization time, enqueue time, readability, and stored bytes |
| 601 | [fan-out](../concepts/600-job-composition/601-fan-out/) | A handler injects `IJobs` and enqueues many independent jobs: no lineage and no join (compare 212's `MapAsync`) |
| 602 | [chained-jobs](../concepts/600-job-composition/602-chained-jobs/) | A linear pipeline: each stage enqueues the next |
| 701 | [enqueue-only-reference](../concepts/700-topology-and-deployment/701-enqueue-only-reference/) | `Reference<TManifest>()` enqueues without a worker; `Run<TManifest>()` drains (one process, two roles) |
| 703 | [multi-worker-process](../concepts/700-topology-and-deployment/703-multi-worker-process/) | Two workers (two namespaces) in one process |
| 704 | [cross-namespace-child](../concepts/700-topology-and-deployment/704-cross-namespace-child/) | A parent starts a child in another worker's namespace and waits across the boundary |
| 705 | [worker-crash-recovery](../concepts/700-topology-and-deployment/705-worker-crash-recovery/) | **Engineering Lab.** Real peer-process loss/recovery through SQL leases and leaderless crash recovery on the heartbeat (PostgreSQL/SQL Server) |
| 706 | [API/worker split](../demos/ApiWorkerSplit/) | **Production capstone.** Open the focused solution and press F5 to run an enqueue-only API and independently scalable worker as separate processes |
| 801 | [testing-jobs](../concepts/800-testing/801-testing-jobs/) | Test `[Job]` handlers with `Acta.Testing`: a class-fixture-shared `ActaTestHost`, deterministic `RunOnceAsync` ticks, `Rearmed`-vs-`Failed` retry semantics (`dotnet test`, not `dotnet run`) |
| 802 | [testing-durable-jobs](../concepts/800-testing/802-testing-durable-jobs/) | Test a durable, multi-step job (step + approval signal) deterministically: drive suspend, raise the typed signal, drive resume; assert run-once across the replay |
| 901 | [native-aot-json](../concepts/900-runtime-and-tuning/901-native-aot-json/) | AOT-safe JSON payloads: `UseJsonPayloads(JsonSerializerContext)` + `JobPayload.Json(value, typeInfo)` |
| 903 | [redis-wakeup](../concepts/900-runtime-and-tuning/903-redis-wakeup/) | Redis pub/sub wakes workers across processes (run worker + enqueue roles) |
| 904 | [worker-tuning](../concepts/900-runtime-and-tuning/904-worker-tuning/) | Worker tuning: `MaxConcurrentExecutors` and `SafetyPollInterval` |

Multi-project apps live under [`demos/`](../demos/); the ladder links one directly as the 706
capstone.
