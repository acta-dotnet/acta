# Learn Acta: the how-tos, for coding agents

> Each how-to teaches one task with a small compiled program from the Acta repository: the handler, the call that starts it, the command that runs it, and the lines it must print. Every link below is plain Markdown.

Read /llms.txt first. It is the contract: the rules every handler follows. This index answers "show me the smallest working example of the task".

## How to use a how-to

- The numbered steps are what you write. The code is lifted from the program named on the page, which compiles; an excerpt can use a name declared outside it, and the whole-program file declares every one.
- "Its output must contain" lists the lines the program prints when the task works. The release check reads the same lines.
- `UseLocalDatabase` and `LearnLab` appear in the programs' host code. They are helpers of the repository, not of the packages: in an application write `UseSqlite`, `UsePostgres` or `UseSqlServer`.
- Rows printed by a lab come from SQL views, the operator's contract. Application code reads the same facts through `IJobs` and `IActaOperations`.
- A how-to is short on purpose. Its whole program, and the notes kept beside it, are in a second file named at its end, `<name>.program.md`; open that only when the steps are not enough.
- The how-tos describe the release /llms.txt names at its top.
- Addresses are relative to this file's site. The page for people is the same address without `.md`.

## The short tour

1. [How to run your first job](/learn/001-hello-acta.md)
2. [How to keep finished work from repeating on a retry](/learn/202-durable-step.md)
3. [How to wait for an approval without holding a worker](/learn/204-wait-signal.md)
4. [How to keep another system in sync with a permanent job](/learn/109-continuous-synchronization.md)
5. [How to recover the work of a killed worker](/learn/705-worker-crash-recovery.md)
6. [How to see your jobs in the dashboard](/learn/022-dashboard.md)
7. [How to test a job that waits for a signal](/learn/802-testing-durable-jobs.md)

## Which one when

- Something has to happen later:
  - a whole job that starts later: [How to run a job once, later](/learn/011-delayed-job.md)
  - a wait inside a job that has already begun: [How to wait for time to pass without a running process](/learn/205-durable-sleep.md)
  - an exact instant instead of a delay: [How to schedule, sleep, and reschedule to an exact instant](/learn/217-absolute-time-controls.md)
- Work must not happen twice:
  - work that finished must not repeat on a retry: [How to keep finished work from repeating on a retry](/learn/202-durable-step.md)
  - a side effect where a second attempt is worse than not knowing, such as capturing a payment: [How to refuse to repeat a side effect after a crash](/learn/220-at-most-once-step.md)
  - a repeated request must not create a second job: [How to keep a repeated request from creating a second job](/learn/010-deduplication-key.md)
- A job has to wait:
  - for a person or another system that tells you: [How to wait for an approval without holding a worker](/learn/204-wait-signal.md)
  - for time to pass: [How to wait for time to pass without a running process](/learn/205-durable-sleep.md)
  - for an outside system you have to ask again: [How to check again later without spending a retry](/learn/306-reschedule.md)
- Work repeats on a schedule:
  - one job the schedule fires again and again: [How to run a job on a repeating schedule](/learn/101-recurring-job.md)
  - the same local hour every day, in a time zone: [How to run a job every day at a set hour in your time zone](/learn/104-timezone-schedule.md)
  - every occurrence a job of its own, with its own retries and result: [How to make every occurrence its own job](/learn/110-per-occurrence-delivery.md)
  - one job that keeps its place between firings: [How to keep another system in sync with a permanent job](/learn/109-continuous-synchronization.md)
- One job becomes many:
  - children from a list, their results joined: [How to fan out over a list and merge the results](/learn/212-fan-out-join.md)
  - a few different children whose results the parent reads: [How to start child jobs and wait for them](/learn/211-child-jobs.md)
  - enqueue now and wait later: [How to fan out to child jobs with Map or WaitChildren](/learn/215-map-wait-children.md)
  - independent jobs with nothing to join: [How to enqueue many independent jobs from a handler](/learn/601-fan-out.md)

## Take the first steps

Define a job, start it, and read back what it did. Start at the top.

- [How to run your first job](/learn/001-hello-acta.md): One `[Job]` method, one enqueue, and a worker that runs it
- [How to pass typed input to a job](/learn/002-job-input.md): The input is a record: Acta stores it with the job and hands it to the handler as its first parameter
- [How to return a result and wait for it](/learn/003-job-result.md): What the handler returns is stored with the job as its result. `WaitAsync<T>` follows the job to its end and hands the result back, typed
- [How to use your own services in a handler](/learn/007-dependency-injection.md): The class that holds the handler takes its services in its constructor, from the application's own container
- [How to read a job's status](/learn/012-read-status.md): `GetAsync` answers a snapshot of the job: its ref, its name and namespace, its status, and when it was created
- [How to read a job's result](/learn/013-read-result.md): `TryGetResultAsync<T>` finds nothing before Succeeded; `GetResultAsync<T>` returns the result you expect
- [How to start a job from an HTTP endpoint and poll it](/learn/024-aspnet-enqueue-api.md): A POST stores the job and answers 202 with its ref; a GET reads the status and, once the job has finished, the result
- [How to build the first-job guide's app](/learn/026-first-job.md): The first-job guide's app verbatim: typed result, a failure that is final and one that retries, explanation, cancel, and a wait that times out without stopping the job

## Start jobs

Every way to start work: now, later, once only, by name, or many at a time.

- [How to enqueue by the input's type](/learn/009-typed-enqueue.md): `EnqueueAsync` takes the input alone: its type names the job, so there is no name string to get wrong
- [How to enqueue by the job's generated call](/learn/023-job-call.md): The manifest's generated call names the job and takes its handler's parameters, several of them as one input
- [How to enqueue a job by its name](/learn/020-raw-enqueue.md): An `EnqueueJobRequest` names the namespace and the job as strings, for a caller that does not have the job's types
- [How to write a job that takes no input](/learn/004-no-input-job.md): A job with no input: a handler with no parameters, enqueued by its generated call
- [How to pass one plain value as the input](/learn/005-scalar-input.md): The input can be one plain value, such as a string or a number, with no record around it
- [How to keep several jobs in one class](/learn/006-many-jobs-one-class.md): A class can hold several `[Job]` methods. Each is a job of its own, with its own generated call
- [How to run a job once, later](/learn/011-delayed-job.md): `ScheduleAsync(input, delay)` stores the job now and makes it due after the delay. No process has to stay alive until then
- [How to schedule, sleep, and reschedule to an exact instant](/learn/217-absolute-time-controls.md): Pin work to an instant instead of a delay: `ScheduleAsync(input, runAt)` starts a job then, `SleepUntilAsync` pauses a handler until then, and `RescheduleUntilAsync` runs the handler again then
- [How to keep a repeated request from creating a second job](/learn/010-deduplication-key.md): A deduplication key makes an enqueue idempotent: a second enqueue with the same key answers the job that is already there
- [How to allow one job per hour or per day](/learn/028-deduplication-time-buckets.md): A deduplication key built from a time bucket lets one job in per bucket: `DeduplicationKey.PerTimeBucket` here, with `PerHour` and `PerDay` as its fixed sizes
- [How to save a record and start its job in one transaction](/learn/029-enqueue-in-transaction.md): `EnqueueAsync` takes your open transaction, so the order row and the job that fulfils it commit together, or neither exists
- [How to find a job by its deduplication key](/learn/014-read-by-deduplication-key.md): `JobLookup.ByDeduplicationKey` finds a job by the key it was enqueued with, when you did not keep its ref
- [How to choose between starting a job and waiting for it](/learn/015-call-vs-enqueue.md): `EnqueueAsync` returns as soon as the job is stored; `CallAsync` enqueues, waits, and returns the job's outcome
- [How to enqueue many jobs in one round trip](/learn/019-batch-enqueue.md): `EnqueueJobsAsync` writes a whole list of jobs in one round trip to the database and answers their outcomes in request order
- [How to run urgent jobs first](/learn/017-priority.md): A priority on the enqueue decides which of the jobs that are due at once a worker claims first
- [How to label jobs so you can find them](/learn/018-tags.md): Tags are name and value labels set on the enqueue, for filtering and grouping jobs in queries and in the dashboard
- [How to pass a large file by reference](/learn/025-large-payload-reference.md): Keep a big file in blob storage and enqueue a reference to it, with its checksum and size, instead of the bytes
- [How to store a job's payload in a custom format](/learn/501-payload-formats.md): A job names the format its payload is stored in: JSON by default, or a format of your own. This program adds two custom formats, `scalar-v1` and `json-gzip`, and runs the same echo job through each

## Run work on a schedule

Recurring work as one permanent job: by cron, by interval, in a time zone, or from a calendar.

- [How to run a job on a repeating schedule](/learn/101-recurring-job.md): `[Schedule]` with a cron makes one permanent job that fires on that cadence, with nothing to enqueue
- [How to run a job every day at a set hour in your time zone](/learn/104-timezone-schedule.md): A cron with `TimeZoneId` fires at the same local hour all year: `0 8 * * *` in `Europe/Berlin` is 08:00 in Berlin, in summer and in winter
- [How to replace a timer loop with an interval schedule](/learn/102-interval-schedule.md): An interval schedule, such as `10s`, runs a job at a fixed gap, where a timer loop in a hosted service would otherwise stand
- [How to give one job several schedules](/learn/103-multiple-schedules.md): Several `[Schedule]` attributes on one job share one permanent job, and schedules that come due together fire it once
- [How to decide what happens to runs a schedule missed](/learn/106-schedule-misfire.md): Missed occurrences are a business decision, and `MisfireStrategy` on the schedule makes it. Three schedules miss the same stretch while paused: `Skip`, the default, passes over them and fires only a latest one less than a minute overdue, `CatchUpOnce` runs one firing that covers them all, and `CatchUpAll` runs every one in order
- [How to pause and resume a schedule](/learn/105-schedule-control.md): `operations.Schedules` pauses a schedule from outside the handler, for good or until an instant, and resumes it
- [How to drive a schedule from a published calendar](/learn/108-calendar-catalog.md): A calendar is data that an operator publishes through `operations.Calendars`. A schedule binds it by name, and a later publication retimes every schedule bound to it in one transaction while workers run
- [How to make every occurrence its own job](/learn/110-per-occurrence-delivery.md): `ScheduledDelivery = PerOccurrence` makes each occurrence a root job of its own, with its own ref, retries, result, and retention. `MaxOutstanding` bounds how many may be unfinished at once
- [How to decide what a failed firing does to its schedule](/learn/111-firing-failure-policy.md): A firing that spends its retry budget returns the permanent job to `Idle`. `FiringFailurePolicy` says what happens to its schedules then: `Continue`, the default, fires the next occurrence as usual, and `Pause` pauses them, with the reason, until an operator resumes them
- [How to start each firing of a recurring job clean](/learn/214-reset-state.md): A permanent job keeps its variables and step results from one firing to the next. `ResetStateAsync` as the firing's last line clears them, so the next one starts blank

## Keep another system in sync

One job that keeps calling a service and remembers how far it got.

- [How to keep another system in sync with a permanent job](/learn/109-continuous-synchronization.md): One permanent job calls a service on a schedule and keeps a watermark of how far it got. Each firing syncs one bounded batch and returns; the watermark is the job's own durable variable, read at the top of every execution, so a recovered firing continues where the lost one stopped
- [How to keep a value across retries](/learn/203-durable-variable.md): A durable variable stores a value with the job the first time it is computed, and every later attempt reads the same value
- [How to check again later without spending a retry](/learn/306-reschedule.md): `RescheduleAsync` ends the execution and runs the handler again after a delay, without counting as a failure or spending a retry

## Make work survive a crash

Steps and variables record what a handler finished, so a retry or a restart carries on instead of starting over.

- [How to keep finished work from repeating on a retry](/learn/202-durable-step.md): A named step records its outcome, so a retry returns it instead of doing the work again
- [How to feed one step's result into the next](/learn/206-step-chain.md): A step can return a value. Each result is recorded once, so the next step takes it as its input and a replay rebuilds the chain from the records
- [How to check, delete, and default a job's variables](/learn/216-variable-lifecycle.md): Beyond `GetOrSetVariableAsync`, shown across a retry in 203, a handler can check for a variable, delete it, read it with a default, and store a raw `JobPayload`
- [How to compensate when the last attempt fails](/learn/210-step-retry.md): A failing step retries through its job, and a `catch` on `EndsJob` compensates before the failure ends the job
- [How to refuse to repeat a side effect after a crash](/learn/220-at-most-once-step.md): An at-most-once step refuses to run its body a second time after a crash. The handler is told that the outcome is unknown, and reconciles
- [How to build a checkout that waits for a review and a timer](/learn/201-durable-checkout.md): One handler reads top to bottom as a checkout: wait for a fraud review, reserve, charge, wait out a settlement window, send the receipt. Each stage is a signal, a step, a variable, or a timer that survives a restart
- [How to report how far along a job is](/learn/208-progress.md): `SetProgressAsync` stores how far the job has come as the checkpoint `sys.progress`, which the dashboard lists while the job runs

## Wait for people, events, and time

A waiting job is a row. No worker and no process stays busy while it waits.

- [How to wait for an approval without holding a worker](/learn/204-wait-signal.md): A job parks on a named signal, holds no worker, and carries on when the signal is raised
- [How to hold a sign-up until the email is confirmed](/learn/221-pending-signup.md): The job is the pending sign-up: Acta stores its input, a signal confirms it, and a deadline ends it with no account to clean up
- [How to wait for time to pass without a running process](/learn/205-durable-sleep.md): A named sleep stores the due time, frees the worker, and wakes the job when the time comes
- [How to read the outcome of a wait that can time out](/learn/311-call-outcome-timeout.md): `CallAsync` answers a `JobOutcome`: whether the wait timed out, whether the job failed, and the value when it succeeded

## Split work across child jobs

Fan out, join the results, and decide what a failed child means.

- [How to start child jobs and wait for them](/learn/211-child-jobs.md): A parent starts child jobs by name, suspends without holding a worker until every child has finished, and reads their results
- [How to call another job and use its result](/learn/213-call-child.md): `CallChildAsync` starts another job as a child, waits for it without holding a worker, and returns its outcome, whose `ValueOrThrow()` is the result
- [How to fan out over a list and merge the results](/learn/212-fan-out-join.md): `MapAsync` starts one child job per item of a list, waits for them all, and hands back each item's result for the parent to merge
- [How to fan out to child jobs with Map or WaitChildren](/learn/215-map-wait-children.md): Two ways to fan out: `EnqueueChildAsync` with `WaitChildrenAsync` for a few named children, and `MapAsync` for one child per item of a list
- [How to find the child job that handles one item](/learn/222-find-child-job.md): A child's name on the reads is its deduplication key, `{map}/{key}` for a child that a map started. List the parent's children and pick the one that carries the item's key
- [How to handle child jobs that fail](/learn/219-child-failure-outcomes.md): A group of children never throws by itself. A failed child shows in the `Failed` list of the wait or the map, and `ThrowIfAnyFailed` is how the parent chooses to fail with it
- [How to start a child job in another namespace](/learn/704-cross-namespace-child.md): A parent starts a child in another worker's namespace and waits across the boundary
- [How to enqueue many independent jobs from a handler](/learn/601-fan-out.md): A handler takes `IJobs` and enqueues many independent jobs, each with a stable deduplication key. Nothing links them to the handler's job and nothing waits for them (compare 212's `MapAsync`)
- [How to chain jobs into a pipeline](/learn/602-chained-jobs.md): Each stage is a job of its own that enqueues the next one, with a deduplication key so a replayed handoff starts no second pipeline

## Decide what happens when work fails

Retries, backoff, timeouts, deadlines, and ending a job on purpose.

- [How to limit how many times a job retries](/learn/016-max-attempts.md): `MaxAttempts` on the job is its retry budget. When the last attempt fails, the job ends as `Failed`
- [How to set how long a job waits between retries](/learn/312-backoff-curve.md): `Backoff` on the job sets how the wait between retries grows: `1s..8s x2 exact` starts at one second, doubles with each failure, and stops growing at eight
- [How to fail a job for good, with no retry](/learn/301-fail-job.md): `FailAsync` ends the job as `Failed` at once, whatever its retry budget. A thrown exception would be retried
- [How to cancel a job from inside its handler](/learn/302-cancel-job.md): `CancelAsync` in a handler ends the job as `Cancelled`: terminal, not a failure, so no retry and no alert follow
- [How to pause a job from inside its handler](/learn/303-pause-job.md): `PauseAsync` in a handler parks the job as `Paused` until someone resumes it
- [How to cut off an attempt that runs too long](/learn/304-execution-timeout.md): `ExecutionTimeout` caps one attempt: when the time is up, the handler's `CancellationToken` trips and the cut spends an attempt
- [How to stop cleanly when a job is cancelled](/learn/008-cancellation-token.md): A handler takes a `CancellationToken` and passes it to everything it awaits, so a cancel, a timeout, or a shutdown that outlasts the host's grace period ends it promptly
- [How to drop a job that is past its deadline](/learn/307-deadline-strict.md): A `Deadline` is the whole job's budget, measured from its creation. Under the default, strict behaviour an overdue job is cancelled before its handler runs
- [How to run an overdue job in a reduced mode](/learn/308-deadline-advisory.md): With `DeadlineBehavior = Advisory` an overdue job still runs. The handler reads `IsOverdue` and chooses a reduced path

## Control order and load

Keep jobs that touch the same thing from running over each other.

- [How to let one job at a time touch a resource](/learn/207-run-with-lock.md): `RunWithLockAsync` holds a named lock around a piece of work, so jobs that name the same lock take turns
- [How to limit how many jobs run at once for one key](/learn/209-concurrency-key.md): A `ConcurrencyKey` on the enqueue lets one job at a time run for that key. `ConcurrencyLimit` on the job gives a key several slots, and `RateLimit` meters how often attempts are admitted
- [How to apply each customer's commands in order while other customers keep moving](/learn/223-lane-per-customer.md): A lane runs its jobs one at a time, in enqueue order. A lane per customer keeps each customer's commands in order and lets the customers run side by side

## Watch and control running work

Read what happened, find jobs, and cancel, pause, resume, or restart them.

- [How to inspect a job from the command line](/learn/021-jobs-cli.md): The app's own binary answers `jobs info`, `events`, `explain`, and the control verbs for any job, read from the same database
- [How to see your jobs in the dashboard](/learn/022-dashboard.md): `MapActa` serves the dashboard and its JSON API from your own app. Its controls are off until you turn them on
- [How to show where each job is on your own page](/learn/415-own-status-page.md): `IJobs` reads one job for a page of your own: its status, a value the handler keeps for the screen, and a sentence that says why the job is where it is
- [How to read why a job did what it did](/learn/404-read-event-timeline.md): The append-only event ledger answers why a job did what it did. `ListEventsAsync` with `LineageRoot` reads the timeline of a job and of every child it started
- [How to list and page through jobs](/learn/409-operator-queries.md): `operations.Ledger` lists jobs with filters, pages through them by cursor, counts them, and answers the health counters an operator's screen opens with
- [How to cancel a running job from outside](/learn/305-operator-cancel.md): `CancelAsync` on a job's handle cancels it from outside its handler. Any process that holds an `IJobs` can do it, and a running handler's `CancellationToken` trips
- [How to pause and resume a job from outside](/learn/309-operator-resume.md): `IJobs.PauseAsync` and `ResumeAsync` pause and resume a job from outside its handler. Each answers whether it was applied or rejected, and its reason lands on the job's timeline
- [How to restart a failed job and keep its history](/learn/310-operator-restart.md): `RestartAsync` runs a finished job again under the same identity. The failure budget is reset, the history is appended to, steps that succeeded are not run again, and the bare lines of the handler are
- [How to control jobs over HTTP](/learn/410-http-api-controls.md): `MapActaApi` mounts the operator API without the dashboard. Its controls are off until `EnableControls` is set, answer local requests only by default, and a control request has to confirm itself
- [How to require permissions on the HTTP API](/learn/413-http-api-authorization.md): `RequirePermissions` has every route of the HTTP API check the host's policy named for its permission: read, enqueue, control, or admin. A caller with no credential gets 401, and one that lacks the permission gets 403 naming it
- [How to put the dashboard behind your sign-in](/learn/414-dashboard-sign-in.md): The dashboard and its API sit behind the host's own sign-in. The host maps its roles to Acta's four permissions; with no session a page goes to the sign-in, and an API call answers 401
- [How to record which tenant a job is for](/learn/412-tenant-scope.md): `TenantName` on the enqueue records which tenant a job is for. Its children inherit the tenant, the job list filters by it, and an enqueue for an unknown or a suspended tenant is refused
- [How to carry a correlation key from parent to child](/learn/408-correlation-id.md): A `CorrelationKey` set on the enqueue travels with the job and with every child it starts. It is on the job row, in each handler's log scope, and a filter of the job list
- [How to wrap every handler in your own middleware](/learn/401-pipeline-behavior.md): An `IJobPipelineBehavior` wraps every handler call, the way middleware wraps a request
- [How to choose how many events a job records](/learn/407-event-recording.md): `EventRecording` on the job decides which events it writes. `Audit`, the default, records every event. `Failures` records the failed and orphaned attempts, the success that recovers from a failure, what an enabled alert profile reads, a caller's controls of the job, and a schedule's pauses and manual triggers

## Get told when something breaks

Alerts raised by failures or by your own code, routed where your team looks.

- [How to get an alert when a job fails](/learn/406-automatic-failure-alerts.md): A failing job raises an alert by itself, with no `AlertAsync` in the handler. `AlertProfile` on the job says when, and repeats of the same job's failure count up on one alert rather than opening new ones
- [How to raise an alert from a handler](/learn/402-alerts.md): `AlertAsync` in a handler raises an alert for the people who operate the system, and `operations.Alerts` reads it back
- [How to send alerts to your own channel](/learn/403-alert-channel.md): `AddAlertChannel` declares where alerts go, under a name of your choice, and an alert names the channel it is for
- [How to route alerts to Slack from configuration](/learn/405-real-alert-routing.md): An alert channel posts to a Slack webhook whose URL comes from configuration, and logs when none is set. A deduplication key collapses repeats of one incident into one alert
- [How to escalate an alert as failures add up](/learn/411-alert-escalation.md): Automatic failure alerts escalate in stages: the first failure, the failure count reaching `AlertFailureThreshold`, and the final failure when the retry budget is spent

## Test without sleeps or polling

The real runtime on a throwaway database, driven one tick at a time.

- [How to test a handler with the test host](/learn/801-testing-jobs.md): `Acta.Testing` runs the real runtime on a real database with no background loop: `RunOnceAsync` decides when a job executes, so a test drives one attempt at a time. Run it with `dotnet test learn/800-testing/801-testing-jobs`
- [How to test a job that waits for a signal](/learn/802-testing-durable-jobs.md): Each `RunOnceAsync` drives exactly one attempt, so the suspend on a signal and the resume after its raise are two explicit ticks, and the test asserts that the step before the wait ran once across the replay. Run it with `dotnet test learn/800-testing/802-testing-durable-jobs`
- [How to test the first-job guide's app](/learn/803-first-job-tests.md): The test of the first-job app ([026-first-job](/learn/026-first-job.md)), as the first-job guide shows it: the real runtime on a throwaway SQLite file, driven one tick at a time, with a retry made due at once
- [How to test a recurring synchronization job](/learn/804-continuous-synchronization-tests.md): Tests for the permanent synchronization job of [109-continuous-synchronization](/learn/109-continuous-synchronization.md): a schedule forced due and one firing driven at a time, and a lost attempt reclaimed with the next execution continuing at the watermark. Run it with `dotnet test learn/800-testing/804-continuous-synchronization-tests`

## Deploy and scale

Split the API from the worker, add workers, and tune them.

- [How to enqueue from a process that runs no worker](/learn/701-enqueue-only-reference.md): `Reference<TManifest>()` lets a process enqueue a namespace's jobs by type without running a worker for it. `Run<TManifest>()` is the process that executes them
- [How to run two namespaces in one process](/learn/703-namespaces-in-one-process.md): Each `Run` starts a worker for one namespace. Two of them in one process share the process and the database, and never each other's jobs
- [How to restart a crashed app on SQLite and get its work back](/learn/706-sqlite-restart.md): On SQLite a starting worker releases the work of processes on the same machine that have ended, so a restarted app takes a lost job back at once. Nothing has to be written for it; the crash spends one attempt
- [How to recover the work of a killed worker](/learn/705-worker-crash-recovery.md): A worker process is killed in the middle of a job, and another worker reclaims the job once its lease has lapsed. The program runs on PostgreSQL or SQL Server
- [How to give Acta a JSON context for Native AOT](/learn/901-native-aot-json.md): A source-generated `JsonSerializerContext` given to `UseJsonPayloads` serializes payloads with no runtime reflection, and `JobPayload.Json(value, typeInfo)` builds a raw payload the same way. The program runs on the JIT: it shows the registration an AOT publish needs, and the publish itself is proven by the repository's own gate
- [How to wake workers across processes with Redis](/learn/903-redis-wakeup.md): `UseRedisWakeup` announces each enqueue over Redis, so a worker in another process starts the job within milliseconds instead of at its next poll
- [How to set how many jobs a worker runs at once](/learn/904-worker-tuning.md): `MaxConcurrentExecutors` caps how many jobs one worker runs at once, and `SafetyPollInterval` bounds how long an idle worker sleeps between looks at the database

