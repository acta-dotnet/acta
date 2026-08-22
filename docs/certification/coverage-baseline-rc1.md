# Coverage baseline and blind-spot list — v1.0.0-rc.1

**Captured** 2026-08-22 · **Commit** `8b9a636` · **Command** `tools/coverage.ps1`

Line and branch coverage for the two suites that need no container: `tests/Acta.Tests` (unit) and
`tests/Acta.Tests.Conformance.Sqlite` (conformance against a real ledger). CI runs the same script
and uploads the merged report; the same numbers come out on a laptop.

**There is no threshold and nothing here goes red.** A coverage target invites tests written to
colour lines rather than to falsify behaviour, and the number would then certify the gaming. The
percentages below exist so a change to them is visible. The deliverable is the second half of this
page: for each of ten failure areas, what the report says is *not* executed, and what that costs if
it is wrong.

## Baseline

| | Covered | Total | |
|---|---|---|---|
| **Line** | 32,003 | 36,261 | **88.2%** |
| **Branch** | 10,002 | 14,216 | **70.3%** |

Method coverage 82.0% (2,491 of 3,035); fully-covered methods 66.9%. 10 assemblies, 448 classes.

| Assembly | Line | | Branch | |
|---|---|---|---|---|
| Acta | 71.2% | 2,936/4,122 | 55.7% | 2,264/4,065 |
| Acta.AspNetCore | 97.8% | 3,135/3,206 | 79.4% | 313/394 |
| Acta.Generators | 86.7% | 2,703/3,116 | 67.8% | 1,389/2,049 |
| Acta.Postgres | 5.7% | 32/560 | 0.0% | 0/133 |
| Acta.Redis | 60.2% | 71/118 | 32.1% | 9/28 |
| Acta.Relational | 97.8% | 9,851/10,071 | 84.4% | 1,059/1,254 |
| Acta.Runtime | 91.2% | 11,933/13,074 | 82.3% | 4,591/5,578 |
| Acta.Sqlite | 93.3% | 458/491 | 80.8% | 97/120 |
| Acta.SqlServer | 21.6% | 113/524 | 19.5% | 31/159 |
| Acta.Testing | 78.7% | 770/979 | 57.1% | 249/436 |

### What these numbers are not

Five things have to be read alongside the table, or it says something it does not mean.

- **Acta.Postgres at 5.7% and Acta.SqlServer at 21.6% are the leg, not a gap.** This run starts no
  container. Provider code those two ship is exercised by the PostgreSQL and SQL Server conformance
  legs of the same CI job, which are not instrumented (instrumenting them would double the slowest
  part of the pipeline). Acta.Redis at 60.2% is the same story.
- **Ten SQLite conformance tests skip**, all of them for `CompleteExecutionsBatch`, which SQLite has
  no routine for (Bulk degrades to Direct there). `CompleteExecutionsBatchSpec` and
  `CompletionSinkBulkFallbackSpec` exist and run against PostgreSQL and SQL Server. A Bulk finding
  reached only by those specs is therefore "unmeasured on this leg", not "untested". The sink's own
  degraded behaviour no longer depends on them: `CompletionSinkDegradedFlushTests` drives it over a
  scripted store, so it runs on the leg most people run locally too (area 9).
- **Generated code is measured**, at 89.3% of 9,501 lines against 86.9% of 26,760 handwritten ones.
  It is why the `Acta` assembly reads 71.2%: its generated code-enum extensions sit at 62.5% while
  its handwritten code is at 85.8%. Generated code is left in because it ships; it is called out
  because an assembly-level number that is mostly generator output does not describe the library.
- **coverlet does not count an exception filter as a branch.** A `catch (X) when (predicate)` never
  shows up as a half-covered branch, so "100% branch" on a file full of filters is weaker evidence
  than it looks. Where a filter matters below, the finding cites the line hit count instead.
- **Run-to-run variance is about 8 lines** (~0.02%), from the timing-sensitive chaos specs. Treat a
  move under a tenth of a percent as noise.

## Blind spots

Ten areas, each read off the merged Cobertura report. "Dead" means zero hits. References are
`file:lines` at the commit above, and every source path is relative to `src/`.

Read the list beside the seals, not instead of them. Coverage instruments only the unit and SQLite
suites; the certification gates run uninstrumented Anvil processes that kill workers on a five-second
cadence, so crash recovery, worker death, and lease loss are proven end-to-end there even where this
report shows zero hits — `RecoveryJob` and `WorkerRuntimeHost` below are the clearest cases. A zero
in this file means no *deterministic in-process* test walks the path: harder to debug when it breaks,
not unproven under chaos. The entries that are blind in both layers are the ones to worry about
first.

### 1. Lease loss — closed, bar a failed lock release

The renewal loops' failure arms are driven by `RenewalLoopFailureArmTests`, and the step-ownership
abort by `JobExecutionOwnershipTests`. All three loops are awaited by the runtime host, so what these
facts pin is not the log line but the loop: a tick failure that escapes `RunAsync` faults the host's
`WhenAll` and takes the worker down at exactly the moment the renewers exist to survive.

- `Acta.Runtime/Modules/Execution/Workers/LockLeaseHeartbeat.cs` — **98% line, 94% branch.** Every
  failure arm of the lock-lease loop now runs: the initial tick's cancel and error handlers, and the
  periodic loop's. Both renewers absorb a store fault *inside* their own tick while the token is live,
  so the state in which a fault reaches these arms is the shutdown window — a connection torn down
  under a running query — and that is the shape the tests inject, the same one
  `LoopTickCancellationFilterTests` already uses. The only dead line left is the enqueue-only return.
- `.../WorkerHeartbeat.cs` — **97% line, 88% branch.** The periodic loop's two arms and the initial
  tick's cancel arm. Dead: the enqueue-only return and `StampDrainingAsync`'s registration guard.
- `.../AttemptWatchdog.cs` — **94% line.** The tick-failure arm, injected at the watchdog's clock seam
  because it does no I/O at all. It is the one loop whose carry-on is directly observable, and the
  fact asserts that rather than the log: the tick after the failure still cancels an attempt whose
  lease has lapsed.
- `.../RuntimeJobContext.cs:404` — covered. A step body that failed and a `complete_step` that
  answered `StaleVersion` raises `StepOwnershipLostException`, and the attempt aborts retryably rather
  than reporting a business failure the ledger never accepted.
- `.../RuntimeJobContext.cs:524,571` — **still dead.** A failed lock release is never recorded, so
  neither the warning nor the `lock_release_failure` metric has ever been produced.

**Not a blind spot:** `WorkerHeartbeat.TickAsync`'s store-fault path (`:158-167`, leaving the live
set non-authoritative and deferring to the watchdog) is exercised, as is `AttemptWatchdog.TickAsync`
in full, and lease-loss-mid-handler end to end (`WorkerCrashRecoveryChaosSpec`).

*Risk: what is left is a lock release that fails silently. The lease paths themselves are now walked
deterministically in process, on top of the chaos gates.*

### 2. Worker death — blind spot: the recovery job and the host that runs it

- `Acta.Runtime/Modules/Execution/RecoveryJob.cs:31,51-84` — **0 of 15 lines, 0 of 12 branches.**
  The `sys.recovery` handler is never executed. Each routine it calls has its own spec
  (`ReclaimStuckJobsSpec`, `MarkDeadWorkersSpec`) and
  `tests/Acta.Tests.Conformance/Testing/RecoverySweep.cs` is an explicit test-side *model* of the
  sweep — so what is untested is the composition: the sweep order, the
  `FailedChildren` latch loop, the stale-latch backstop loop, and both wakeup publishes.
- `.../Workers/WorkerRuntimeHost.cs:24-148` — **0 of 51 lines, 0 of 12 branches.** The process-level
  `BackgroundService`: bootstrap-once, per-worker initialize, run-all, and the shutdown lifecycle
  stamp. `Acta.Testing/Hosting/ActaTestHost.cs` reproduces its startup order in a comment rather than
  calling it, so the two can drift silently.
- `.../Workers/WorkerShutdownPhase.cs:20,50,53,60,63` — the empty-batch shortcut, the per-item
  cancellation arm, and the arm where the failure callback itself throws. The phase deadline is
  never reached in a test.

*Risk: the two production entry points for worker lifecycle and crash recovery are modelled by the
harness instead of executed by it, so a change to either is caught by no test on this leg.*

### 3. Claim/control races — closed, bar the per-job executor-fault swallow

- `Acta.Runtime/Modules/Execution/JobExecution.cs:78-84` — covered. The branch taken when
  `StartExecutionAsync` does not return `Started`: the row was reclaimed, reassigned, or moved by an
  operator between claim and start. `JobExecutionLostClaimTests` walks all four non-`Started` answers
  and pins the skip as clean — no handler invocation, no completion command submitted against a row
  this worker no longer owns, `NothingClaimed` reported, and a log line naming which of the four ways
  the claim was lost. (The lines had picked up a single incidental hit since this page was first
  captured; nothing asserted the contract.) `WorkerCrashRecoveryChaosSpec` still covers only the
  *other* lost-claim window, during the handler, and accepts either outcome there.
- `.../Workers/WorkerLoop.cs:181,186-190,193-195,198` and `:312-326` — covered by
  `WorkerLoopClaimFailureTests`, in both loop shapes: a failed claim backs off a full safety interval
  and says how long it is waiting, the loop keeps claiming afterwards rather than tearing down, a stop
  landing inside that backoff breaks out instead of waiting it out, and a stop landing on the claim
  call itself ends the loop without calling a clean shutdown a failure — with the combined loop's
  executor permits released either way, or its drain would hang. `WorkerLoop.cs` overall: **90% line,
  86% branch.**
- `.../Workers/WorkerLoop.cs:252-259` and `:384-388` — **still dead.** The per-job executor-fault
  swallow that keeps one bad job from tearing down the coordinator.

**Not a blind spot:** the store-side races are well covered — `ClaimAndControlRaceChaosSpec`,
`AttemptOverlapChaosSpec`, `ClaimTypes.cs` at 97% line / 83% branch, `RelationalJobStore.cs` at 100%
line. And `JobExecution.cs:642-650` (an external control or a stolen lease landing while the handler
ran) has both arms covered.

*Risk: what remains is one bad job faulting its executor, which the coordinator swallows on a path no
test walks. Losing the claim itself, and a claim query that fails outright, are now both driven.*

### 4. Retry exhaustion — no blind spot found, bar the backoff clamps

Step exhaustion (`RuntimeJobContext.cs:407-418`), `StepExhaustedException`, `StepInterruptedException`
on an `AtMostOnce` re-entry, the one-shot job retry budget (`OneShotRetrySpec`,
`TimeoutRetryBudgetSpec`) and the deferred step retry (`StepDeferredRetrySpec`) are all exercised.

One gap, small and real: `Acta.Runtime/Kernel/BackoffSchedule.cs:19,34,38` — the three clamps
(attempt below 1, and the post-jitter floor and ceiling) are dead. Nothing proves a large jitter
fraction cannot produce a negative delay or one above the configured maximum.

*Risk: low. A miscomputed backoff delays a retry rather than losing it, but an unclamped negative
would turn a backoff into a hot loop.*

### 5. Transaction rollback — mostly covered; the teardown-failure arms are not

- **Covered, and worth recording as covered:** `Acta.Relational/Connections/DbSession.cs:269-285`
  (`DisposeFailedAttemptAsync`, including the arm where rolling the transaction back throws) is
  exercised, and `Acta.Relational/Commands/DeadlockRetry.cs` is genuinely driven — its backoff line ran 11 times and
  its cancellation-classification line once, so the transient-retry arm is not merely compiled.
- `DbSession.cs:292-299` — the arm where disposing the *connection* of a failed attempt throws. Dead.
  That is the path that leaks a connection out of the pool.
- `DbSession.cs:68-71` — a connection that fails to open. Dead.
- `DbSession.cs:101-122` and `:354-356` — the routine-provider command and result-set paths. Dead by
  construction on SQLite; PostgreSQL and SQL Server exercise them off this leg.
- `Modules/Execution/CompletionSink.cs:159,164-165` — the only place in the runtime that may claim a
  whole batch rolled back. Now covered (see area 9).

*Risk: pool exhaustion after a database blip is the one failure here that hides, because the symptom
appears far from the cause and the code that would log it has never run.*

### 6. Duplicate enqueue — no blind spot found

`EnqueueRejectionSpec` and `EnqueueSpec` drive the dedup-key rejection path;
`DuplicateDeduplicationKeyInBatchException` is at 100%; `RelationalJobStore.cs` is at 100% line;
`DeduplicationKey.cs` is at 93% line / 94% branch.

Dead lines are ambient-clock convenience overloads (`Acta/Jobs/DeduplicationKey.cs:56,64,77`, the
`PerHour`/`PerDay`/`PerTimeBucket` forms that read `UtcNow` instead of taking an instant) and three
argument-validation throws (`Acta/Jobs/JobEnqueueRequestValidation.cs:57-60,70-73,112-115`: too many
tags, duplicate normalized tag name, `OverrideParentTenant` without a parent and tenant).

*Risk: negligible for the dedup contract itself. The uncovered overloads only forward to the covered
ones with `UtcNow`.*

### 7. Signal-before-wait — no blind spot found

Well covered, and by name: `SignalSpec.Signal_raised_before_wait_is_observed` (raise while the job is
still Ready: the slot is created Set and the job never suspends),
`SignalSpec.Wait_before_signal_suspends_job`, `Wait_signal_is_idempotent_while_pending`,
`Duplicate_raise_is_last_writer_wins`, plus `SignalSuspendHandoffRaceChaosSpec`, which drives the
narrow window between `wait_signal` answering `SuspendPending` and `complete_execution` writing that
suspend, and asserts the job lands Ready with a published wake rather than parked on a signal it
already holds. `SignalService.cs` is at 94% line / 93% branch; `RelationalSignalStore.cs` at 100%
line.

The one dead line is `SignalService.cs:40`, the not-found return — reached through `JobsService` by
`Raise_signal_returns_not_found_for_unknown_job` rather than through this facade.

### 8. Child completion/cancellation races — blind spot: the crash backstop

- `Acta.Runtime/Modules/Execution/RecoveryJob.cs:59-76` — both backstop loops, dead with the rest of
  the handler (area 2): the budget-exhausted child that landed Failed with no worker completion to
  raise its latch, and the re-raise of latches lost to a crash between a child's terminal landing and
  its follow-up raise. These are precisely the child-completion races recovery exists to close.
- `Acta.Runtime/Modules/Execution/ChildLatches/RaiseChildLatch.cs:38` — one arm only. Every test raise releases the parent;
  nothing exercises a raise against a parent that is terminal, missing, or not Suspended, so the
  "returns false" half of the contract is unpinned.
- `.../CompletionSink.cs:185-195,266-279` — the parent child-done latch on the Bulk fallback path,
  and the parent-release wakeup that follows it. Now measured on this leg too, over a scripted store
  (area 9); the ledger-level version stays SQLite-skipped.

**Not a blind spot:** `Acta.Runtime/Modules/Execution/Jobs/CancelDescendants.cs` is at 100% line and
branch, and `ChildJobSpec`,
`ChildTimeoutSpec`, `ChildGroupTimeoutSpec`, `ChildJobCrossNamespaceSpec` and
`CancelPropagatesToHandlerSpec` cover the in-transaction raise on the hot path.

*Risk: a parent that never wakes. The hot path is covered; the backstop that catches the raise lost
to a crash is not executed at all, and a parent stuck Suspended forever is the failure it prevents.*

### 9. Failure between paired transitions — closed, bar two arms that cannot fire

`Acta.Runtime/Modules/Execution/CompletionSink.cs` was the worst-covered non-provider file in the
runtime at 56% line / 42% branch; it is now at **94% line, 88% branch**.
`CompletionSinkDegradedFlushTests` drives the degraded paths over a scripted execution store rather
than a ledger, so they run on every leg, and each fact is one clause of Bulk's stated contract rather
than a line to colour. Now covered:

- `:84-88,91` — `RunFlushersAsync`: several flushers drain the shared multi-reader buffer and each
  buffered completion reaches the store exactly once. (`WorkerLoop.cs:79-90`, the Bulk branch of the
  loop that starts them in production, is still dead.)
- `:122-127` — the flusher's async read and its interval-window timeout: a lone completion is flushed
  on time rather than waiting for a batch that never fills.
- `:159,164-165` — the whole-batch failure: nothing landed, so no per-job fallback is attempted and no
  wakeup is published, and this is the only path that may claim a rollback.
- `:185-195,229-234` — the per-job fallback for rows the set call self-filtered (a parent, or a lost
  lease), and the `unresolved` bookkeeping: one failing fallback strands its own job only, the rows
  after it still complete, the row the set call committed still gets its wake, and the log names that
  one job out of the batch rather than the batch.
- `:253-263` — the fallback CAS matching nothing, i.e. a control or a reclaim moving the row while the
  completion sat buffered: warned by job id and outcome, and nobody is woken.
- `:266-279` — both wakeups a released parent depends on, and the non-terminal landing that publishes
  neither.

Still dead, and unreachable as written: `:221-224,239-243`, the wake-failure catch and its warning.
They cannot fire — the sink publishes through `WorkerWakeupPublisher.WakeAsync`, which already
catches every exception and logs its own warning, so no wake failure can propagate back into the
flush. Defensive depth, not a measurable path.

**Not a blind spot:** `JobExecution.cs:642-650` covers both arms of a completion CAS that matched
nothing after the handler ran, `JobExecution.cs:78-84` (area 3) is now driven, and
`RelationalExecutionStore.cs` is at 94% line. `RecoveryJob.cs` (area 2), which is what closes a
started execution that never finishes, remains untested on this leg.

*Risk: no longer the highest on this page. Bulk's "only the rest for recovery" is now asserted rather
than inferred, and asserted on the leg a contributor runs locally.*

### 10. Alert storms — no blind spot in the storm controls; the Slack transport is now covered

The three mechanisms that bound a storm are all exercised:

- The drain budget, `Acta.Runtime/Modules/Alerting/AlertsJob.cs:130-175` — every arm, including
  both bounds (`time-budget` and `batch-cap`) and their log sites.
- Incident collapse by dedup key (`:304`) and the occurrence threshold with its
  replay-safe mark check (`:278-288`), backed by `AlertThresholdReachedSpec` and
  `AlertRefDedupeStabilitySpec`.
- Delivery retry backoff (`:524`), `AlertDeliveryFailureSpec`, and the resolve-suppresses-pending
  path.

`AlertsJob.cs` overall: 98% line, 89% branch. `AlertRoutingCheck`, `AlertChannelDecision`,
`AlertChannelRegistry`, `AlertStoreSink`, `SlackAlertFormatter` and `RelationalAlertStore` are at
100% line.

Closed:

- `Acta.Runtime/Modules/Alerting/SlackAlertTransport.cs` — **100% line, 86% branch** (was 3 of 29
  lines, 2 of 14 branches). `SlackAlertTransportTests` drives the only transport that talks to a real
  endpoint over a stub `HttpMessageHandler`: what goes on the wire (one POST of the
  `SlackAlertFormatter` payload as UTF-8 JSON to the channel's endpoint), how a status maps to the
  retry semantics the projector then acts on (2xx delivered, 429 and 5xx retryable — the ones a storm
  hits first — other 4xx permanent), a channel with no endpoint, an unreachable Slack, and a send
  cancelled by shutdown propagating instead of being recorded as a delivery failure. The two
  uncovered branches are the constructor's null-coalescing defaults, taken only by a caller that
  supplies neither an `HttpClient` nor a logger.

Gaps:

- `AlertsJob.cs:330,334` — the `invalid-event` poison tag, thrown when a stored channel name or
  dedup key fails canonicalization. The sibling `unknown-job` tag (`:341-345`) *is* covered.
- `AlertsJob.cs:426,428` — cancellation during a transport send.
- `AlertsJob.cs:363` — the default title case of the render fallback.

*Risk: the projector holds under a storm and is measured doing so, and what happens when the storm
reaches Slack and Slack pushes back is now measured too. What is left is the projector's own poison
tag for a stored row that fails canonicalization.*

## Reproducing

```
tools/coverage.ps1            # builds, runs both legs, merges, prints the summary
tools/coverage.ps1 -NoBuild   # what CI runs, after the solution is already built
```

Output lands in `artifacts/coverage` (git-ignored): `Cobertura.xml` (merged, machine-readable),
`Summary.txt`, `SummaryGithub.md`, and an HTML report with per-file line and branch marks —
`index.htm` is the way in. The two legs take about 35 seconds of test time.

CI runs the same script in the `build-test` job and uploads `Cobertura.xml` and the two summaries as
the `coverage` artifact; the HTML render is ~90 MB and is left to whoever wants it locally.
