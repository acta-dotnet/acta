# Release notes

## 1.0.0-rc.7

Not tagged yet. rc.7 redesigns scheduling (the sections below grow with each step), fixes what an external review of rc.6 found, renames Acta's log fields, and drops dependencies. The schema changes: reprovision, as every pre-1.0 release asks.

### What a consumer must change

**`[JobSchedule]` is now `[Schedule]`.** Only the name changes: `JobScheduleAttribute` is `ScheduleAttribute`, and its arguments, defaults, and diagnostics are the same. Rename the attribute on each scheduled handler.

**Schedule overrides are gone.** A schedule's expression and time zone are code-owned: change them in `[Schedule]` and deploy. `ISchedules.UpdateOverridesAsync`, `POST /schedules/{jobNamespace}/{jobName}/{scheduleName}/overrides`, the dashboard's override editor, the `schedule.overrides-updated` event, and the `expression_override`, `expression_effective`, `time_zone_id_override`, and `time_zone_id_effective` columns of `schedules` and `schedules_view` are deleted. Read `expression` and `time_zone_id`. The schedules row's `change_message` is `pause_message`: it holds only the reason for the current pause and is cleared when the schedule leaves Paused; events keep the history.

**An interval schedule is whole milliseconds.** `PT1.0000001S` is `ACTA0122` at compile time and an error at registration, since an occurrence is stored at millisecond precision on every provider and a finer interval would make two ticks one instant. `PT1.5S` is fine.

**A job's kind is stored.** `jobs.job_kind_code` says whether a row is `ordinary` or the `permanent-slot` of a scheduled definition, and `ux_jobs_slot` allows one slot per definition. Registration creates and finds the slot by its kind, and the worker reads the kind from its claim; neither guesses any more from a deduplication key that looks like a job name or from the schedules a row happens to own.

**A schedule's state row says when and why the engine next looks.** `schedules_state.last_advanced_occurrence_at_utc` is gone: no decision read it, and the ledger's `schedule.admitted` and `schedule.skipped` events carry every decision's range. The row gains `next_evaluation_at_utc`, `evaluation_status_code` (`scheduled`, `waiting-for-capacity`, `exhausted`, `evaluation-error`, `slot-unavailable`), and a `version` the admission engine will compare and set. `schedules` gains `interval_anchor_at_utc` (an interval steps from its first registration and never drifts), `misfire_grace_seconds` (60), and `timing_fingerprint`. `ListSchedulesItem`, `GET /schedules`, and `schedules_view` rename and gain the same fields. Jobs gain the status `idle`, which nothing writes until the admission engine lands.

**A handler reset keeps the scheduler's firing envelope.** `checkpoints` gains the kind `firing` (one `sys.firing` slot per scheduled job, Pending while a firing is outstanding and settled with `succeeded`, `failed`, or `cancelled`), which `ctx.ResetStateAsync` leaves in place and `GetCheckpointsAsync` omits: it is the scheduler's record of which schedules admitted the running firing, not the handler's state. Nothing writes it yet; the admission engine that follows does.

**A schedule is addressed by its path, and the slot job has no key.** `ScheduleLookup` is `(JobNamespace, JobName, ScheduleName)`, resolved through the definition to its permanent slot (`ux_jobs_slot`); the slot's `deduplication_key` is NULL, so `IJobs.GetJobIdAsync(JobLookup.ByDeduplicationKey(ns, jobName))` no longer finds it, and an ordinary root enqueued with the job's name as its key coexists with the slot instead of blocking the worker's start. Find a slot through `GET /schedules` (each item carries the slot's job ref) or the dashboard.

**A scheduled definition says what a failed firing does to its schedules.** `[Job(FiringFailurePolicy = ...)]` is stored as `definitions.firing_failure_policy_code` and read back on `GetDefinition` and `definitions_view`: `continue` (the default) leaves the schedules scheduled, and `pause-scheduling` pauses every active schedule of the definition, with the pause message naming the reason, on the completion or the reclaim that spends the firing's budget; the pause is audited as `schedule.paused` with reason `firing-failed`, and an operator resumes each schedule as usual. A handler's own fail or cancel is not a failed firing: it stops the slot.

**Two more catch-up strategies, and an occurrence can expire.** `MisfireStrategyCode` gains `CatchUpLatest` (30: the latest missed occurrence fires however late, the earlier ones coalesced into it) and `CatchUpAll` (40: every missed occurrence fires in order, one per settlement, within the schedule's capacity); `Skip` and `CatchUpOnce` keep their codes and meaning. `[Schedule(ExpireAfter = "1h", ExpireAction = ScheduleExpireActionCode.Discard)]`, stored as `schedules.expire_after_seconds` and `schedules.expire_action_code` (both or neither, `ck_schedules_expire_pair`), decides occurrences older than the age without work, audited as `schedule.skipped` with the reason `expired`; `Pause` also pauses the schedule with the reason. `EvaluationStatusCode` gains `resumed` (60), which a resume writes so the next evaluation expires the stale backlog once without pausing again. The generator reports an incomplete expiration as `ACTA0126` and an undefined misfire strategy as `ACTA0124`. `ListSchedulesItem`, `GET /schedules`, and `schedules_view` carry the expiration pair.

**A schedule can take its occurrences from a calendar, and any schedule can be limited to open hours.** `[Schedule("business-day", Calendar = "settlement-days")]` binds an occurrence calendar by name, `[Schedule(..., Availability = "office-hours")]` an availability calendar whose `[StartUtc, EndUtc)` windows filter any source: an occurrence outside them is omitted, never shifted. A name resolves afresh at every evaluation and preview, in order: a calendar declared in the process, then the namespace's catalog, then the shared catalog in `sys`. A code calendar is a class marked `[Calendar("settlement-days")]` implementing `IActaCalendar` (the first occurrence after an instant, UTC in and out, null when none remains) or `IActaAvailabilityCalendar` (the window at or after an instant), discovered by the generator into the manifest and created through DI at worker start; `IActaBuilder.AddCalendar(name, implementation)` and `AddAvailabilityCalendar` register one built from data at startup. Code calendars have no versions: a changed one takes effect at the next evaluation, where an occurrence the calendar no longer produces is skipped with the reason `source-changed` and the cursor moves on. `ScheduleExpressionKindCode` gains `calendar` (30; `expression` holds the calendar's name), and `schedules` gains `availability_calendar_name`, carried by `ListSchedulesItem`, `GET /schedules`, and `schedules_view`. A schedule stores the names it binds and nothing about where they resolved: `CalendarOriginCode` and the `calendar_origin_code`, `calendar_version`, `availability_calendar_origin_code`, and `availability_calendar_version` columns, which an earlier step of this release added, are gone with their checks. The timing fingerprint covers the names, not where they resolve, so a calendar that moves between code and the catalog keeps the cursor, and the `source-changed` skip heals an occurrence the new source no longer produces. Registration only checks that every name resolves: a name nothing resolves fails the worker start by schedule name, and at evaluation parks the schedule as `evaluation-error` with the reason logged. The generator reports an incomplete source as `ACTA0127` and an incomplete calendar declaration as `ACTA0128`. A preview resolves a code calendar in the host that answers, and on a host without it throws `CalendarUnavailableException`, a 409 problem over HTTP. An `ExpireAfter` that is no duration is reported against `[Schedule]`, not `[Job]`.

**A calendar catalog, per namespace and shared.** `IActaOperations.Calendars` keeps calendar families as data: `calendars` holds one immutable version per row (`calendar_role_code` occurrence or availability, fixed per family; a JSON `definition`; `content_hash`; `calendar_status_code` draft, published, superseded, or archived; `ux_calendars_namespace_name_version`; one published row per family under `ux_calendars_current`), read through `calendars_view`. A family lives in a namespace or in `sys`, the shared scope every namespace binds, and a name belongs to one scope. `SaveDraftAsync` saves the next version against the latest the caller saw (creating a namespace no worker has run in yet), `PreviewAsync` forecasts a version beside the current one with the bound schedules and their overdue flag, `PublishAsync` makes a draft current and retimes every schedule that names the family in its scope (none when the process declares the name in code, which resolves before the catalog) in one transaction under the affected namespaces' schedule locks, its cursor seeded from the new version, refusing while a schedule holds an undecided overdue occurrence unless the caller chooses `OverdueDecisionCode.Discard`, which audits the range as `schedule.skipped` with the reason `retimed`, and `ArchiveAsync` retires a family no live schedule names. Definitions are declarative JSON, validated by path: availability kinds `weekly-availability`, `union`, `intersection`, `subtraction`; occurrence kinds `explicit-instants`, `daily-local-time`, `business-day`, `at-opening`, `union`. Events: `calendar.draft-saved`, `calendar.published`, `calendar.archived`, and `schedule.retimed` per bound schedule. `ListAsync` lists one namespace's families with the shared ones, or every namespace's when `JobNamespace` is null. Over HTTP: `GET /calendars`, `GET /calendars/{jobNamespace}/{calendarName}` (`?version=`), `GET /calendars/{jobNamespace}/{calendarName}/preview?version=`, and the controls `POST .../drafts`, `.../publish`, and `.../archive` (authorizer verbs `calendars.drafts`, `calendars.publish`, `calendars.archive`), whose responses share `ICalendarControlResponse`.

**Scheduling reports itself on the `Acta` meter.** Six instruments join it, each tagged by namespace: `acta.schedule.decisions` counts what each admission pass applied, tagged by decision (`admitted`, `skipped`, `expired`, `blocked`, `exhausted`, `error`); `acta.schedule.lag` records how far past its nominal instant an occurrence stood when admitted; `acta.schedule.due.age` records how long past its evaluation instant each due schedule stood when a pass read it, so its maximum is the overdue frontier; `acta.schedule.pass` records a pass's wall time and `acta.schedule.stale` counts the plan groups another host moved first; `acta.firing.start.delay`, tagged by job name too, records the time from a firing's admission to the attempt that first ran it, by the worker's clock.

**A reschedule runs a recurring job now.** `IJobs.RescheduleAsync` takes a nullable instant, null meaning now, and `POST /jobs/{jobRef}/reschedule` accepts an empty body; the dashboard's job page gains a Run now control beside Change run time. On an `Idle` permanent slot, or a Paused one with no firing outstanding, the reschedule is a manual firing at the instant (a `sys.firing` envelope of kind `manual`, a fresh failure budget), and the slot returns to `Idle` when it settles; a slot mid-firing keeps its firing and moves its instant. At rc.6 the slot sat Ready at its next occurrence, so a reschedule to now ran it; the admission engine's `Idle` slot had lost that until now.

**A scheduled definition can deliver each occurrence as a fresh root.** `[Job(ScheduledDelivery = ScheduledDeliveryCode.PerOccurrence)]`, stored as `definitions.scheduled_delivery_code` and immutable once registered, replaces the permanent slot with one ordinary root job per admitted occurrence: `jobs.schedule_id` names the admitting schedule (`jobs_view` carries it, and the operator guide's capacity-drift recipe reads it), the root's input is the definition's default input, which registration now stores as `definitions.default_input_format_id` and `definitions.default_input` so any host admits it, and its deduplication key is `sys.occurrence.{schedule_id}.{ticks}`. `[Schedule(MaxOutstanding = 1, FullBehavior = ScheduleFullBehaviorCode.Wait)]` (`schedules.max_outstanding`, `schedules.full_behavior_code`) bounds a schedule's unsettled roots, counted in `schedules_state.outstanding_count`: every terminalization releases one, a restart of a finished root re-charges or is Rejected, a manual trigger admits a charged root, and a `Permanent` definition's schedules allow exactly 1 (`ACTA0124`, `ACTA0125`). A per-occurrence schedule's skip and ended pause are audited against the definition (`entity_code = definition`). `schedules.job_id` is NULL for such a schedule; `GetDueSchedules`, `ListSchedules`, `schedules_view`, `GET /schedules`, and the dashboard carry the delivery and capacity fields; the claim projection carries `schedule_id`; and `complete_executions` gains `p_has_schedule` on PostgreSQL.

**Scheduling is admission-driven, and a scheduled job's slot is `idle` between firings.** The admission engine, one loop per worker namespace, evaluates each schedule when it is due, admits one firing per slot per pass with every due schedule coalesced into it, moves the cursor at admission, and writes the firing's envelope to `sys.firing`; the claim then runs the slot like any job, and completion settles the envelope and wakes the schedules that waited for it. The slot carries no cursor of its own: it is `idle` with no `next_run_at_utc` until a firing is admitted, `ready` while one is, and `idle` again when it settles. A firing retries under `MaxAttempts` like a one-shot, with the count zeroed at each admission; a spent budget returns the slot to `idle` with the envelope `failed` and the attempt's `job.execution-failed` event, never `job.failed`, and a reclaim does the same for a lost attempt. `JobContext.TriggeringSchedules` (name, occurrence, time zone, covered-through) and `JobContext.FiringId` replace `TriggeringScheduleNames`. `job.recurring-rolled-over` is gone; the decisions are `schedule.admitted`, `schedule.skipped`, and `schedule.retimed`. Registration never moves a slot: a new slot is `idle`, a redeploy with an unchanged schedule changes nothing, and a changed expression, time zone, or interval anchor retimes the schedule, discarding the old timing's undecided occurrences. Pausing a schedule no longer pauses or re-arms the slot; resuming one puts it in front of the admitter at once with its cursor kept, and the misfire policy applies at the next pass. `TriggerNowAsync` admits a manual firing on an `idle` slot and is rejected otherwise. A job pause, cancel, restart, and resume accept an `idle` slot; a restart is rejected while a firing is outstanding, and a restart or resume with none returns the slot to `idle`. A permanent slot is purged only by deliberate decommission: `idle`, `failed`, or `cancelled`, no firing outstanding, and no live schedule of a live definition pinning it. A retire cancels an `idle` slot with retention and settles its envelope.

**A schedule has its own identity.** `schedules` is keyed by a new `schedule_id`, unique on `(definition_id, schedule_name)`, and `schedules_state` is keyed by it alone, so a cursor row no longer carries the slot job and the name. `schedules_view` leads with `schedule_id`. Registration keeps a schedule's identity across deploys; a renamed schedule is a new one. The HTTP and .NET surfaces are unchanged (a schedule is still addressed by `namespace/job/schedule`), but a `GET /schedules` continuation cursor from an earlier build is invalid: start the page again.

**A typed wait refuses a job whose definition returns another type.** `JobHandle<T>.WaitAsync`, `WaitAsync<T>`, and `RunAndWaitAsync<T>` throw `InvalidOperationException` when the job's registered definition produces a result other than `T`, or none. Before, a deduplication key shared across definitions handed back the other definition's job, and its wait decoded the foreign result leniently: success, with defaults in the missing members. A job this process does not register decodes as before.

**`JobOutcome.TerminalStatus` is `Status`, and a failed wait names the job by its ref.** An outcome's status is the job's last observed one, which is `Ready` or `Running` when `IsTimedOut` is set, so the member no longer claims it is terminal; `IsSuccess`, `IsFailed`, `TryGetValue`, and `ValueOrThrow` behave as before, and there is no alias. `JobFailedException` gains `JobRef`, its constructor is `(long jobId, JobRef jobRef, JobStatusCode terminalStatus)`, and its message, the wait-timeout `TimeoutException`, and `ValueOrThrow` identify the job by that ref, the one the dashboard and the HTTP API address, instead of the numeric id. Rename the member at every read.

**Every log field Acta writes is renamed, and saved log queries must follow.** Each field carries the `Acta` prefix so none can clash with an application's own: the job scope on every handler line is `ActaJobId`, `ActaJobName`, `ActaNamespace`, `ActaExecutionNumber`, `ActaWorkerId`, and `ActaCorrelationKey`. Numbers are `long`. `Operation`, `Outcome`, `Reason`, `Count`, and `SubjectRef` are gone, because each meant something different on different lines; their values now read in the message or in `ActaDetail`. The `ACTA ALERT` line carries `ActaAlertRef`, `ActaJobRef`, and `ActaAlertKind`. Acta no longer logs a line per completed job, nor any Debug line. The operator guide's "Logs" section shows how to quiet a failure storm.

**Native AOT with a multi-parameter handler:** declare the input record yourself. The developer guide used to say to add the generated `Args` record to your JSON context, which cannot work. System.Text.Json's generator never sees a generator-emitted type.

### Additions

- `IActaTestHost` gains `ForceScheduleDueAsync(ScheduleLookup)` and `AdmitOnceAsync(namespace)`, so a test drives a scheduled job the way it drives a one-shot: force the schedule due, admit once, run once, with no admitter loop and no sleep. `ActaRunStatus.Completed` now says what a slot's success does: the slot returns to `Idle`.
- The OpenAPI reference gives every operation an id derived from its route, `GetJobsByJobRef` or `PostJobsByJobRefPause`, the same under any mount; every control lists the mount's confirmation header with its one value; `POST /jobs` documents its `Location` header on 201 and 200 and carries a request example, as does a tag upsert; and the document's description states the cursor paging. The generator writes one document per mount, a read-only mount's without the controls. ASP.NET endpoint names stay unset.
- `POST /jobs` takes `lane`, `concurrencyKey`, and `tags` (`[{ "name": "source", "value": "dashboard" }]`, the value optional), the metadata `IJobs.EnqueueAsync` already took, under the same rules and defaults; an omitted member behaves as before. The dashboard's enqueue form shows the three fields and checks them before sending.

### Fixes

- A parent whose bounded child wait expired cancels the child and its subtree only while its attempt still holds the parent. Before, an attempt that lost the parent after its wait expired could cancel a child that a newer attempt had restarted and was waiting on.
- A new outbox operator command never replaces a pending one that a relay pass has planned. Before, a relay that died between a discard's delete and its audit event, followed by a park past `WorkerDeadAfter`, erased that deletion's only evidence.
- A wake's metric listener or logger that throws no longer fails the operation that published it. Before, a throwing listener made `CancelAsync` throw after the parent's cancel committed, and the children kept running.
- `jobs.EnqueueAsync(new EnqueueJobRequest(...))` and its transactional twin compile with the token left out. Before, they were ambiguous with the generic overload (CS0121).
- `JobContext.ExecutionNumber` no longer claims to be an idempotency key. It changes on every retry.

### Dependencies

- The payload-formats concept no longer uses MessagePack; its two custom formats need no package.
- The explicit SourceLink package is gone, because the .NET SDK ships it. The Microsoft.OpenApi pin is gone, because AspNetCore.OpenApi now requires a safe version itself.
- Meziantou.Analyzer is gone. src logs through source-generated `[LoggerMessage]` classes, and `LogVocabularyTests` holds their fields.

### Known limitations

- A requeue command replayed by a relay pass that stalled across another pass finishing it can requeue a row quarantined again since, once, with no event.

## 1.0.0-rc.6

Tagged 2026-10-26. Upgrading from rc.5 takes the changes below.

### What a consumer must change

**Drop and reprovision every database an rc.5 build provisioned**: the baseline is re-cut, and nothing translates it.

**The whole model now speaks one semantic vocabulary.** Tables: `runtimes` is `jobs_state`, and `schedules_state` is new (the schedule cursor split below). Columns carry qualified names: `<entity>_id` identities, domain-qualified codes (`job_status_code`, `schedule_status_code`, `worker_status_code`, ...), `event_recording_code` replaces `audit_level_code`, `max_concurrent_executors` replaces `max_concurrency`, `host_name` replaces `host`, tags attach by `entity_code`/`entity_id` with `tag_name`/`tag_value`/`tag_value_search`, and steps store `failure_code`/`failure_message` with their own `StepFailureCode` family. Every index, check, routine, table type, and view follows; SQL that reads base tables or views needs the new names.

- View changes operators will hit first: `jobs_view` raw code columns are `job_status_code`/`job_priority_code` (decoded `status`/`priority` stay); `schedules_view` exposes `schedule_name`, `next_occurrence_at_utc`, `last_advanced_occurrence_at_utc`, and `change_message`; `workers_view` has `max_concurrent_executors` and `host_name`; `tags_view` has `entity_kind`/`entity_code`/`entity_id`; `definitions_view` drops the effective-as-bare convention — the bare policy column is the declared default, with explicit `_override` and `_effective` variants; `definitions_view.definition_name` replaces `job_name`.
- `events` is a compact generic ledger: fifteen typed columns on the `(entity_code, entity_id)` subject pair. One outcome event per attempt replaces `job.execution-finished` plus its status triple: `job.execution-succeeded`, `job.execution-failed`, `job.execution-orphaned`, `job.execution-yielded`, and terminal `job.failed`. The actor, from/to status, execution status, and reason columns are gone — causes live in the event code and the JSON `details` payload (`details_format_id`/`details` pair, same convention as input/result/value); human text is `event_message`. Events no longer store `job_ref`: joins supply refs while the row lives, and an explicit purge leaves one `job.purged` tombstone whose details snapshot the ref and correlation key once.
- `events_view` has the new shape (`entity_kind`, `entity_id`, `event`, `execution_number`, `duration_ms`, `event_message`, `details_text`); `GET /events` items expose `eventId`, `entityKind`, and `entityId` (the one deliberate numeric identity on the wire — the subject survives row deletion), and the `actorCode`/`reasonCode` filters are gone.
- Schedules split: `schedules` keeps the declaration and operator lifecycle (status, pause, `change_message`, `version`); `schedules_state` holds only the cursor. A fire advances the cursor without bumping the config `version`, so an operator's `expectedVersion` is no longer invalidated by a running schedule. Orphaned and retired schedules carry a NULL cursor; `GET /schedules` with `liveOnly=false` lists them, the default live list does not.
- Wire renames: `maxConcurrency` → `maxConcurrentExecutors`, `host` → `hostName` (workers); `nextRunAtUtc` → `nextOccurrenceAtUtc` and `lastRunAtUtc` → `lastAdvancedOccurrenceAtUtc` on schedule items and control responses; `nextRunsUtc` → `nextOccurrencesUtc` (preview); `auditLevel*` → `eventRecording*` (definitions and overrides); the CLI explanation's `steps[].state` is `steps[].status`. Job-level `nextRunAtUtc` keeps its name: a run is a scheduled instant.
- .NET: `[Job(EventRecording = ...)]` replaces `AuditLevel`; `TagScopeCode` is replaced by the shared `EntityCode` (all entities, numbered alphabetically by tens); `EventReasonCode`, `ActorCode`, and `ExecutionStatusCode` are gone; schedule records speak occurrence vocabulary.
- SQL Server table types drop `_batch` (`enqueue_jobs`, `enqueue_tags`, `complete_executions`, `register_definitions`, `register_slots`, `register_schedules`) and their columns take full names; the `enqueue_job`/`enqueue_jobs` routine tag parameters are `@p_tags` / `p_tag_name`/`p_tag_value`/`p_tag_value_search`, with `p_job_priority_code_override`, `p_parent_job_id`, and `p_lane_name`.
- `PurgeAsync` can now answer a blocked condition (`JobPurgeBlockedException`, HTTP 409): a job whose alert-required events the alerts projector has not consumed cannot be purged until it runs; automatic retention defers those jobs the same way.

- `JobDescriptor` loses `InvocationKind`, `RequiresJobContextParameter`, and `RequiresCancellationToken`, and `JobInvocationKind` is gone: the generated invoker already carries those facts. Only code that builds descriptors by hand changes.
- `JobStatusCode.IsClaimable` is gone: compare the status with `JobStatusCode.Ready`.
- `JobStatusCode.IsActiveExecution` is gone: compare the status with `JobStatusCode.Executing`.
- `StepStatusCode.IsTerminal` is gone: a step is terminal when it is `Succeeded`, `Exhausted`, or `Interrupted`.
- `JobOutcome.IsCancelled` is gone: check `TerminalStatus == JobStatusCode.Cancelled` on an outcome that is not `IsTimedOut`.
- `ParallelOutcome.GroupName` and `MapOutcome<TKey>.GroupName` are gone, with their constructor parameters: the handler already holds the name it passed.
- The `ParallelOutcome` indexer is gone: read a branch as `outcome.Branches["branch-name"]`.
- `IAlertTransportRegistry` is internal: register an `IAlertTransport` and the alert job resolves it by its `TransportKind`.
- `ITags.ReplaceAsync`, `UpsertAsync`, and `RemoveAsync` lose their `reasonMessage` and `actor` parameters, which no tag write ever recorded.
- `INamespaces.ListNamesAsync`, `ListNamespaceNamesQuery`, and `ListNamespaceNamesResponse` are gone: call `ListAsync` and read each item's `JobNamespace`.
- `POST /jobs/{jobRef}/purge` takes an empty body: a `reasonMessage` member now answers 400, since purge never recorded it.
- `GET /jobs/{jobRef}/detail` drops the top-level `tenantName`: read `job.tenantName`.
- `CodeManifests.MarkdownReport` is gone: the code families are documented in `docs/reference/code-families.md`, and `CodeManifests.All` stays.
- A job's input and output type names must be printable ASCII and at most 512 characters: registration refuses others, and the generator reports ACTA0103 at compile time.
- `TagMutationAction` is gone: tag writes answer `ControlAction` (`Applied` or `NotFound`), with the same wire values.
- `JobAttribute.Tags` is gone; it never had an effect.
- `ActaEndpointOptions.ControlConfirmationHeaderValue` is gone: the confirmation header's value is always `true`, and only its name is configurable.
- `ExecutionStatusCode.ChargesFailureBudget` is gone: a `Failed` attempt charges the budget only when it retries, which the status alone cannot say.
- `JobPayload.FromBytes` and `CopyBytes` throw `ArgumentException` for json bytes that are not valid UTF-8 or hold a NUL byte; binary and custom formats are unaffected.
- `JobAttribute.RecurringResultCap` and `JobDescriptor.RecurringResultCap` are gone: a job stores one result, and a handler that wants more history keeps it itself.
- `ActaJobs.SysOutbox()` returns `JobCall<NoInput>`: the outbox relay returns no result and keeps its last tick's counts in the slot's `outbox-status` variable.
- `jobs_view` replaces `last_result_execution_number`, `last_result_format`, `last_result_text`, and `last_result_created_at_utc` with `result_format` (`none` when there is no result) and `result_text`.
- `tags_view` has no namespace columns: join `jobs_view` for a job tag's namespace.
- The job explanation's `view-result` action is gone.
- `events_view.worker_id` is 0, not NULL, for an event no worker wrote; `worker_ref` stays NULL for it.
- A ref prints as its prefix and the Guid's 32 lowercase hex digits, such as `job_01a14f4e954e7bf68ac609f3fcad8e3d`, and parses only in that form, with the hex in any casing. The Crockford form, a dashed or bare Guid, and an uppercase prefix no longer parse, and the HTTP routes answer 400 for them. `job.purged` names the job the same way.

**Typed enqueue answers a handle, and payloads are JSON or bytes.**

- A typed enqueue or a call answers `JobHandle`, a record deriving from `EnqueueJobResponse` that adds `GetStatusAsync`, `WaitAsync`, `GetResultAsync`, `CancelAsync`, and `ExplainAsync`; a result job's call answers `JobHandle<TResult>`, and its result type is checked before the enqueue. A `ValueTask<EnqueueJobResponse>` variable or wrapper becomes `ValueTask<JobHandle>`. A test double of `IJobs` returns `new JobHandle(jobs, jobId, jobRef, action)`.
- `IJobs.WaitAsync(job, timeout)` and `WaitAsync<TResult>` wait on any job; `IJobs.GetHandleAsync(jobRef)` rebuilds a handle; `JobOutcome` carries `JobRef`. `RunAndWaitAsync` throws when its job is purged while it waits, instead of timing out as `Ready`.
- `ScheduleAsync(input, TimeSpan)` and `ScheduleAsync(input, DateTimeOffset)` enqueue one job for later, with call and transactional forms.
- A manifest's job members are factories instead of `JobContract` properties: `ShippingJobs.ShipOrder(order)` returns the job's `JobCall`, which `EnqueueAsync` and `ScheduleAsync` take with a `configure` callback for options and `RunAndWaitAsync` with its `JobExecutionOptions`, so `jobs.EnqueueAsync(ShippingJobs.ShipOrder, order)` becomes `jobs.EnqueueAsync(ShippingJobs.ShipOrder(order))` and a no-input `ShippingJobs.Sweep` becomes `ShippingJobs.Sweep()`. The contract overloads of `EnqueueAsync`, `RunAndWaitAsync`, and `ScheduleAsync` are gone, with the conversion from `JobContract<TInput, TResult>` to `JobContract<TInput>`.
- A handler may take several input parameters before `JobContext` and `CancellationToken`, which used to be ACTA0103: they travel as one generated record, `ShippingJobs.{Job}Args`, stored as JSON keyed by the parameter names. Such a handler cannot carry `[JobSchedule]` (ACTA0123), an interface parameter other than a collection is ACTA0103, and a Native AOT app registers the record in its `UseJsonPayloads` context.
- The text payload format is gone: a typed value with no explicit format is JSON unless it is `byte[]` or `ReadOnlyMemory<byte>`, scalars included, so a scalar handler's stored input reads `"order-42"` or `42`. `JobPayload.Text`, `JobRequestBuilder.Text`, `JobPayloadFormat.Text`, and `TextJobPayloadSerializer` are gone; `JobPayload.Json(string)` and `JobRequestBuilder.Json(string)` replace them without reflection. `[Job(Format = "text")]` no longer compiles (ACTA0132).
- Under reflection-free Native AOT, Acta supplies JSON metadata for the standard scalars; an enum input or result goes in the app's `UseJsonPayloads` context like any other type. JSON has no NaN or Infinity, so serializing one throws at enqueue.
- A json payload is exactly one strict JSON value: `JobPayload.FromBytes` and `CopyBytes` reject comments, trailing commas, a second value, and an empty body, the JSON serializer reads strict JSON, an HTTP or CLI signal like that answers 400 or a usage error, and an outbox row like that quarantines.
- HTTP: `POST /jobs` and `POST /jobs/{jobRef}/input` drop the `text` field; a payload's `json` field is the stored JSON text as a string, never a nested object, and a stored row within the inline size cap that is not strict JSON reads as `base64` with `malformed: true`.
- The operator views decode only json payloads into the `*_text` columns.
### Behaviour changes

- Automatic alerts are independent of the recording level: an enabled alert profile captures the failed, orphaned, terminal, and recovery transitions durably even at `EventRecording = Off`, retention never sweeps an alert-required event the projector has not consumed, and a poison event stays recoverable until replayed or dispositioned. One documented gap: a recurring job restarted after a terminal failure does not auto-resolve its incident at `Failures`/`Off` (see known-limitations).
- `Failures` recording now records every failed attempt, the orphaned attempt a reclaim writes, and the success that recovers from a failure — not only terminal failures.
- `failure_count` saturates at 32,767 instead of growing without bound; it still resets on success, and `MaxAttempts` registration caps at the same ceiling. Alert escalation's `occurrence_count` is unaffected.
- A schedule fire no longer bumps the schedule's `version` or `modified_at_utc`; only operator edits do.
- A retired definition's schedules are inert everywhere. Registration, a completion planned before the retire, and the schedule verbs (pause, resume, overrides, trigger) can no longer revive their cursors or wake the slot. Each schedule verb answers `NotFound`. Resuming or restarting a retired slot is no longer rejected: the worker claims it and lands it `Cancelled` without running the handler.
- Each namespace has one schedule lock. `RetireDefinition` and a worker's schedule registration take it exclusive; a completion that fires a recurring slot and the schedule verbs take it shared. A retire or registration therefore waits for the namespace's in-flight fires and verbs to commit, and they wait for it; fires never wait for each other.
- A worker start skips its schedule registration, and that lock, when the namespace holds the start's catalog hash and no stored cursor or slot would move. `namespaces.catalog_hash` now hashes the whole registered catalog and is written by that registration, not by the worker start.
- On SQL Server, `correlation_key` and the tag search column compare bytewise, as they already did on PostgreSQL and SQLite. A correlation filter no longer matches a key that differs only by case, and a tag filter no longer matches full-width letters, a different emoji, or a sharp-s spelling.
- A correlation filter is trimmed and checked like a key, and a tag value filter ignores surrounding whitespace, on every provider.
- A job ref resolves only while its job row exists. Once retention purges the job, by-ref reads return null and the by-ref HTTP routes answer 404; its events stay queryable in `events_view`, and by job id.
- Retiring a definition also stops the jobs running when it lands: the attempt finishes, then the job lands Cancelled with `JobDefinitionRetired` instead of retrying, rescheduling, or rolling a recurring slot over. Workers learn of a retire on their next heartbeat.
- Worker events carry a null actor and name their worker by `worker_id`. Retention keeps a worker row as long as events can name it: the longer of `WorkerRetention` and `JobEventsRetention`, counted from the newest event about the worker, recovery's included, and never while it still leases an Executing job.
- Automatic retention also deletes expired tenant events, which live in the `sys` namespace.
- An instance handler registered in DI is the instance invoked, so a typed `HttpClient` keeps its configuration; a Singleton registration is shared by every concurrent attempt. An unregistered handler is constructed per attempt and disposed when the attempt ends (`DisposeAsync` first), and a dispose that throws fails the attempt.
- A stale `expectedVersion` on definition overrides, retire, or schedule overrides answers `VersionConflict` (409 `versionConflict`) instead of `Rejected`, as every other versioned verb already did. `SetDefinitionOverridesResponse` and `RetireDefinitionResponse` carry the definition's `Version`.
- The alert, definition, outbox, and tag response interfaces extend `IControlResponse`; the versionless families report a null `Version` and keep their JSON shape.
- A failed attempt that retries within budget records execution status `failed` (200), not `rescheduled` (150), which now means only a handler-requested reschedule. A query that counted retries by 150 counts 200 with to-status `ready`.
- A signal body that is not UTF-8 or holds a NUL byte answers 400; an outbox row like that quarantines.
- The descriptions of `job.rescheduled`, the `Failures` audit level, the `OnFailure` alert profile, the `job` and `worker` actors, and the `failed` execution status now say what Acta records; behaviour is unchanged.
- A job stores at most one result, on its `jobs_state` row: a one-shot job's terminal success, or a recurring job's latest successful occurrence. Every claim and every restart clears it, so a running attempt or a failed occurrence leaves the job without one. The `results` table and its per-execution history are gone, and a successful completion writes one row instead of two.
- The dashboard's job view shows the input and the result beside the status; the Input and Result tabs are gone.
- On SQL Server, events are clustered by worker, then id, so each worker appends to its own end of the table instead of every worker contending for one last page; PostgreSQL keys events the same way. A purge deletes events by that pair.
- A generic input or output type is stored without assembly versions (``System.Collections.Generic.List`1[MyApp.Line]``), so an application version bump no longer reports contract drift for it.
- Startup and migration apply refuse a database whose migration history names a baseline this build does not ship, and the error names it. Applied over an earlier release candidate's database, the re-cut baseline changed nothing and left that cut's schema in place.
- Three metrics join the `Acta` meter: `acta.executions.fenced` counts attempts stopped because a write found their job held by another execution, `acta.lease.headroom` records how long an attempt had left on its lease when a renewal confirmed it, and `acta.alerts.lag` records how far the alert projector's cursor stood behind the database clock when a pass began.
- The SQLite floor is 3.51.0, up from the documented 3.42: batch completion uses `jsonb_each`, which SQLite added in 3.51.0, so an older engine started but could not complete a job. Startup now reads `sqlite_version()` and refuses an older engine with an error naming both versions; the engine `Microsoft.Data.Sqlite` bundles clears the floor.

### Fixes

- A handler write from an attempt that no longer holds its job changes nothing. Checkpoints of every kind, waits, timers, steps, notes, a state reset, and a child start each check the job is still executing that attempt on that worker, under a lock on its `jobs_state` row held to commit. Before, an attempt cancelled, restarted, or reclaimed under a running handler could overwrite the next execution's variables or mark its at-most-once step Interrupted until the next heartbeat stopped it. A refused attempt now stops as a lost lease does, with no completion and no retry charged, and a note from it is no longer recorded.
- On SQLite, a step whose retry instant came due while `start_step` ran is answered Suspend. Each of the script's statements read the clock afresh, so the attempt was invoked without being counted, and a budget of two attempts could run the body three times. On every provider a step's next attempt now clears the retry instant it spends.
- A bounded group wait that runs out of time resumes its parent in one pass, however many members it holds. Each member's latch is armed with the group's stored deadline itself, and a latch first armed past it times out at once. Before, each member first reached after the deadline armed a fresh one-second bound and suspended the parent again, so a group's overshoot grew with its size.
- Typed `RunAndWaitAsync` reads the result of the execution it saw succeed. After a restart whose second result was too large to store, it returned the first execution's result; now it throws.
- Typed `ExecuteChildAsync` reads the result of the child execution that released the parent, and throws when that execution stored none. It returned an older execution's result after a restart, and a value-type default such as 0 for a child that stored no result.
- On SQLite, the recovery pass's lane repair seeks the lanes primary key instead of scanning and sorting every lane inside the write transaction.
- The checkpoint and tag shape checks accept code values this build does not name, so a future checkpoint kind or status, or tag scope, needs no table change.
- A malformed name or key in a query string or route segment answers 400 instead of a sanitized 500.
- A payload type whose state is get-only properties set through a constructor travels as JSON. It was classified as carrying no payload, so the handler received default values and a result of that shape was dropped.
- Two job names that differ only in `.` and `-`, such as `send.mail` and `send-mail`, compile and warn ACTA0106, as do names matching a member the manifest class declares or a type it uses; they failed the build with duplicate members.
- The dashboard's schedule editor saves with the version its draft was opened at, so another operator's change made while the editor was open is refused instead of silently reverted.
- An outbox row whose tags hold a null entry, or that fails reconstruction any other way, is quarantined alone; it stalled its whole claimed batch on every relay tick.
- A stale relay pass no longer deletes an outbox requeue or discard it never applied. A command parked after another pass consumed the previous one starts at the same version, and the stale pass's consume matched it by version; a pass now consumes only the command it read.
- An outbox requeue or discard replayed after a relay crash between its source change and its evidence records one event naming the rows it changed, and changes no other row. The relay now stores the rows a command targets in the command before it changes them, and records the evidence and consumes the command in one statement; a replay named no rows, a crash after the evidence recorded the command twice, and a stale pass of a command that targets every quarantined row swept rows quarantined since.
- On SQL Server, the outbox relay's finalizes and the operator requeue and discard seek the producer's table by key, so a producer transaction still open on the table no longer holds them up. On a small table they scanned it and waited for that transaction to end, and a long one failed the relay tick.
- A job of a retired definition that recovery returns to Ready is claimed and landed Cancelled, even by a worker that had stopped claiming the definition for want of its handler; it stayed Ready forever.
- On SQLite, a job due before 1970 is claimed; the claim never reached it, and the worker retried every poll floor.
- A Redis wake whose jitter draw was zero no longer disables remote wakes for its channel until the process restarts.
- A metric listener that throws no longer stops a worker's recovery passes for good.
- Application types in the root namespace named like the types generated code uses, such as `JobContext` or `ValueTask`, no longer break the build: the generated manifest names every type with `global::`.
- On SQL Server, rate-limited jobs no longer deadlock in `reserve_rate`. A call on a meter whose bucket was not yet stored took a range lock over the gap that holds the meter's reservations and the next meters, and a chaos run with three meters recorded 150 to 190 deadlocks, each retried after the deadlock monitor picked a victim. Calls on one meter now serialize on the meter's own lock.
- On SQL Server, a completion batch no longer deadlocks with a wait or a raise on the same job's slots. The batch probed an awaited slot under `HOLDLOCK`, locking the gap after it, and only later asked for a parent latch's lock that a waiting raise held. It now takes every slot lock in one name-ordered pass before it touches a slot row.
- A note written with `ctx.NoteAsync` records the worker whose attempt wrote it. It stored worker 0, so on SQL Server every worker's notes appended at one point of the events key.
- On SQL Server, a binary parameter bound without a size is declared `varbinary(max)`, so a statement keeps one cached plan instead of one per payload length.
- Under reflection-off Native AOT, `sys.alerts` writes and reads its poison-event records when the application's JSON resolver has no `string` metadata: Acta's own context now covers every value Acta persists, `string` and `long` included. The write failed, and since it lands before the cursor moves, the projector could not get past that event.
- A `[Job]` handler named with a C# keyword, declared as `@event` or named `await`, compiles: the generated invoker writes the name with its `@`, as it now does a keyword segment of the root namespace. It emitted `Handlers.event()` and failed the build.
- A graceful drain waits for the descendant cancel that a completion repeating apart starts when it lands. It read which cancels to wait for before those completions landed, so the stop that followed could cut a handler-cancelled job's walk short and leave its descendants running.
- A cancel walks the job's descendants to the end when its caller stops waiting. The dashboard's cancel walked on the request's token, so a closed tab or a proxy timeout on a large tree left children running.
- When a handler cancels its job, a descendant walk that fails partway runs again from the root until it finishes or the worker stops. A database error ended the walk and left the rest running.
- A recurring job whose default input encodes to zero bytes, such as a handler that takes `ReadOnlyMemory<byte>`, registers at worker start. Its slot was written with NULL bytes beside a payload format, which the input pair check refuses, so the start failed; on SQLite a batch enqueue of an empty text or byte payload failed the same way.

### Evidence

The engine was certified on `599e28e8` with the Acta Bench full profile on SQLite, PostgreSQL, and SQL Server, in seven bundles reported in [`docs/benchmark.md`](./benchmark.md). The bench changes between the bundles touch only `tools/Acta.Bench` and its tests; `src/` is the same in all seven.
- **Result.** No case failed safety or liveness in any bundle, and every trial's ledger passed the certification checks.
- **Chaos certification.** F09 passed at 1,000,000 jobs on PostgreSQL and on SQL Server: 24 workers of 64 executors, a worker killed every 5 s for 7 minutes, 84 and 83 kills, every one of a busy worker whose work was reclaimed, and 16 ledger checks with no violation. SQLite passed at 10,000 jobs on 3 workers.
- **Bench defects found during the run**, each fixed in the bench alone:
  - The memory guard counted the database container's page cache as used memory and stopped PostgreSQL in the 64 KB payload trials. It now counts only memory the kernel cannot reclaim (`fb8f8414`).
  - The owned PostgreSQL server allowed 500 connections, too few for the full chaos fleet. The refused attempts exhausted the host's TCP ports, so a replacement worker could not start. It now allows 2,500 (`fb8f8414`, `988764e6`).
  - A replacement worker whose SQL Server login timed out on the saturated host ended F09. The bench now starts a replacement again, up to three times, as a process supervisor would, and records every failed start; neither rerun needed one (`988764e6`).
  - 10,000 chaos jobs drained before enough kills hit busy workers on the servers. The reruns seeded 1,000,000, the scale useacta.net states.
  - The full profile validated only at 0.8 of a case's discovered rate, which discovery's 15 s trials can overstate, so a case that missed it had no validated rate at all. A case where no rate holds at 0.8 is now measured at 0.7, 0.6, and 0.5 in turn (`05e5c27c`).
  - On Linux, a bench worker read its controller's start time a few milliseconds off and stopped before it was ready; the tag's NativeAOT check found it. The evidence ran on Windows, where the two reads agree, and the release carries the fix (`5511252d`).
- **Reruns.** PostgreSQL `--full` on `fb8f8414`, then F09 alone on SQL Server and on PostgreSQL on `988764e6`, both passing. Then every capacity row still unvalidated, on `05e5c27c`: P01 on SQLite; M02, M04, O02, O03, and P01 on SQL Server; L01, C01, P01, I05, M03, M04, O03, E01, and E03 on PostgreSQL. The other SQLite and SQL Server cases come from `599e28e8`.
- **Capacity.** A row is validated only when an arrival rate passes every independent validation trial. One PostgreSQL worker of 64 executors validated at 7,680 no-op jobs per second end to end; at 8,960, one of five trials was limited by the bench's own load generator, and at 10,240 the backlog grew. Still INCONCLUSIVE, with correctness passed: O03, one hot concurrency key, on PostgreSQL and SQL Server; I05 on PostgreSQL, which sustained about 310 roots/s, below its lowest frozen rate of 400; and PostgreSQL's E01 soak, where the load generator fell behind. SQL Server's E03 offered 3,200 roots/s without growing the backlog, so it established no overload.
- **useacta.net.** The homepage states 7,680 fully executed, durably completed jobs per second from L01 and a 77,662 roots/s steady drain at 8 workers from L04. The batch-enqueue and pickup-latency figures from rc.5's round are gone, since this bench has no comparable row. The million-job line points at this evidence.

## 1.0.0-rc.5

Tagged 2026-10-17. Upgrading from rc.4 takes the changes below.

### What a consumer must change

**Drop and reprovision every database**, including one an earlier rc.5 build provisioned: the migrations table changed shape and nothing translates it.

Packages and namespaces:

- `Acta.Relational` and `Acta.Runtime` are gone; their code ships in `Acta`, which every provider brings. Drop the references; `using Acta.Runtime;` becomes `using Acta;`, a log filter on `Acta.Runtime.X` becomes `Acta.X`, and the system-job manifest `RuntimeJobs` is `ActaJobs`.
- Each package has one namespace, its own name: replace sub-namespace usings (`Acta.Postgres.Configuration`, `Acta.AspNetCore.Configuration`, `Acta.Testing.Hosting`, ...) with the package root.

Configuration:

- `UsePostgres`, `UseSqlServer`, and `UseSqlite` take `Action<SqlProviderOptions>`; the per-provider options types are gone. On SQLite the schema is always `main`.
- An outbox source is chosen by connection string: `source.UsePostgres(connectionString)` and its siblings; the `XOutboxSourceOptions` types are gone.
- `JobsOptions.ExecutionProfile`, the `ExecutionProfile` enum, and `JobsOptions.ClaimBatchSize` are gone. A worker claims for every free executor; lower `MaxConcurrentExecutors` to claim less.
- `DriverVersionPolicy` is gone: a driver of another major version logs one warning and runs.

Jobs and statuses:

- `JobStatusCode.Dispatched` (40) is gone; a claim moves a job straight to `Executing`.
- `sys.recovery` is gone: crash recovery runs on the workers' heartbeat. `RegisterSystemJobs` still turns it off.
- A claim writes no `job.execution-started` event: `EventCode.JobExecutionStarted` (40), `ExecutionStatusCode.Executing` (50), and `ExecutionBehavior.Live` are gone. An attempt is recorded once, by `job.execution-finished`.
- `EnqueueBatchAsync` takes at most 1,000 rows (was 5,000).
- Tags attach only to tenants, namespaces, definitions, and jobs: `TagTarget.ForSchedule`, `ForWorker`, `ForAlert`, `ForEvent`, their `/tags` routes, and the `Tags` filter on the schedule, worker, alert, and event lists are gone.
- Durable settings (`ISettings`, the `settings` table, event 160) are gone.
- The `Acta.Testing` scenario API (`Scenario.For`, `ScenarioSession`) is gone: drive jobs through `IActaTestHost`.
- The gauge `acta.executing` is `acta.jobs.inflight`, filtered to `state = executing`.
- An uncaught `StepExhaustedException` fails the job at once with `job.non-retryable-exception` instead of spending its retry budget, since every retry would replay the exhausted step; on a recurring job it rolls over to the next occurrence.
- `NotSupportedException` and `NotImplementedException` roll a recurring job over to its next occurrence, alerting like any failure, instead of ending its schedule.
- `ctx.FailAsync`, `CancelAsync`, `RescheduleAsync`, and the waits called inside a step body decide the job, as they do from the handler body, instead of failing the step.
- An interval schedule fires at most once a second: a shorter interval is `ACTA0122` at compile time, and the override verb refuses one.

Identifiers:

- A name (namespace, job, schedule, signal, tag, lane, tenant, rate meter) is ASCII `dotted.kebab-case`, lowercased on input. A key (deduplication, concurrency, correlation, lock) is printable ASCII kept exactly as given, so `Order-1` and `order-1` are two keys. Display text is any Unicode.
- Two inputs tighten: a correlation key must be printable ASCII, and a tenant or lane must be a valid name.
- `IdentifierSyntax` and `AdminTextLimits` are replaced by `NameSyntax`, `KeySyntax`, and `TextLimits`.
- Renamed to match: `TenantKey` is `TenantName` (and `tenant_key` is `tenant_name`, in the outbox table too); `[Job(RateName = ...)]`; `alerts.deduplication_key`; `events.actor` and the `actor` argument of control verbs. On SQL Server an outbox table's `deduplication_key` and `concurrency_key` take the collation `Latin1_General_100_BIN2_UTF8`.

Responses and read models:

- Every list returns its own `ListXResponse` of `ListXItem`, every get its own `GetXResponse`, and every control verb its own `<Verb><Noun>Response`, replacing `PagedResult<T>`, the `*Detail` types, and the shared `*ControlResult` types. Members and JSON are unchanged; the OpenAPI schemas take the new names.
- `AdminControlAction` is gone: tenant and namespace verbs answer `ControlAction`, which gains `AlreadyInState = 6`; `VersionConflict` is 5 for every verb.
- HTTP control responses drop `message` (build text from `action` and `status`), and job controls no longer echo `jobRef`.
- `GetCheckpointsAsync`, `GetJobIdAsync`, `GetStatusAsync`, `GetInputAsync`, and `GetResultAsync` wrap their value in a response (`.Items`, `.JobId`, `.Status`, `.Input`, `.Result`); a lookup that matches nothing answers `null`.
- The enqueue contract is `EnqueueJobRequest`, `EnqueueJobResponse`, and `EnqueueJobAction`; `EnqueueBatchAsync` returns `EnqueueJobsResponse`.
- Wait results are `ChildWaitOutcome`, `ChildrenWaitOutcome`, and `SignalWaitOutcome`; `AlertDeliveryOutcome` is `AlertSendStatus`; `ActaControlDecision` is `ActaControlVerdict`; `ActaControlRequest` is `ActaControlContext`.
- Code families drop the `Job` prefix (`CheckpointKindCode`, `StepStatusCode`, `EventReasonCode`, ...), and `JobAuditLevelCode` is `DefinitionAuditLevelCode`. A stored id a newer Acta wrote decodes as the family's id-0 member instead of throwing.
- `GetOverviewQuery.IncludeSlowCounts` and the overview's `JobCount` and `SystemJobCount` are gone.
- A tag read answers `{"items": [...]}`; the input template carries its skeleton as JSON text in `templateJson` and answers 404 for an unknown job.
- A worker's last heartbeat is `LastSeenAtUtc` (`lastSeenAtUtc` in JSON) on `GetWorkerResponse` and `ListWorkersItem`, and `WorkerLastSeenAtUtc` on `GetJobExplanationLease`, the name the `workers` column has.

Schema and direct SQL:

- Migrations are `M{yyyyMMddHHmmss}_{name}.sql`, and the `migrations` table is `name` and `applied_at_utc`. Routines and views ship inside migrations; the startup installer is gone.
- `steps` and `schedules` are keyed by `(job_id, name)`: `steps_view` and `schedules_view` have no surrogate id, and a `ListSchedules` cursor from an earlier build is rejected.
- Code columns carry no value-list CHECKs, so a new code value needs no migration.
- `steps_view` and `checkpoints_view` name their decoded status `status`, as every other view does (was `state`), and `alerts_view` adds `acknowledged_at_utc`.
- Only the hot paths stay routines: enqueue, claim, complete, steps, checkpoints, signals and sleep timers, meters, the heartbeat, recovery, and retention. On SQL Server the routines and table types are named verb first (`enqueue_job`, `enqueue_jobs`, `claim_jobs`, `acta.enqueue_tags_batch`, ...).
- The SQLite outbox DDL uses `INTEGER PRIMARY KEY AUTOINCREMENT` and `STRICT`; a table from the earlier script keeps working.

### Execution engine

A worker is one claimer, its executors, and one finalizer, and the database renews only what the process holds. Nothing a consumer calls changes.

- A claim moves its rows straight to Executing: one execution path, no profiles.
- The finalizer lands up to 128 completions of any shape in one `complete_executions` call and repeats a write until it lands. A failing database stops intake instead of growing memory.
- The heartbeat renews only the executions and locks the process holds; anything else lapses to crash recovery.
- Crash recovery runs on the heartbeat: a dead worker's jobs are back within two leases, six minutes at the default heartbeat.
- An attempt cancels at its own lease deadline when renewals stop, so a database outage stops the handler.
- A definition override reaches every worker within one heartbeat.
- A claim starts where the worker's last full claim ended, so it skips the dead index entries earlier claims left behind.
- SQLite syncs every commit (`synchronous = FULL`), so the `AtMostOnce()` caveat for SQLite is gone. Single enqueues and workers with few executors pay for it in throughput, and on Windows one job at a time picks up in about 20 ms instead of 2 ([known limitations](./known-limitations.md)).
- The system slots claim at Realtime, so a Realtime backlog no longer starves them.
- Retention walks `runtimes` from a cursor instead of an index, and SQL Server no longer page-compresses `results`.

### Additions

- `JobRef`, `AlertRef`, and `WorkerRef` also parse the plain Guid behind the ref, hyphenated or not, with or without the prefix.

### Fixes

- SQL Server enqueue seeks its deduplication index instead of scanning `jobs` under `UPDLOCK`, which could escalate to a table lock.
- SQL Server enqueues of different deduplication keys no longer wait for each other: each locks its own key with `sp_getapplock`. A refused key lock raises error 51205, retried like a deadlock.
- SQL Server steps and checkpoints of different jobs no longer queue behind one range lock.
- SQL Server never escalates locks on `jobs` (`LOCK_ESCALATION = DISABLE`).
- A job's rows (`runtimes`, `results`, `steps`, `checkpoints`, `schedules`) carry no foreign key to `jobs`; `PurgeJob` and retention delete them with the job.
- The SQL Server claim no longer deadlocks with an operator verb.
- Raising or resolving an alert no longer deadlocks against a purge of its job on PostgreSQL.
- A SQL Server rate meter no longer refuses the last job of a burst.
- The overview's counts and the Failed list read indexes instead of scanning `runtimes`, and recovery reads only in-flight rows on PostgreSQL and SQLite.
- The dashboard's lists seek by namespace on PostgreSQL and SQLite, so a page costs the same at any history size.
- The overview no longer blames idle workers in one namespace for another namespace's backlog.
- `sys.alerts` skips first-attempt successes, so a failure's alert no longer waits behind them.
- The outbox relay drains a backlog in one tick, and a SQLite outbox source deletes relayed rows without scanning its backlog.
- A scoped service that throws while the attempt's scope is disposed no longer leaves the job Executing with its executor held.
- A handler cancel walks the job's descendants even when the first completion write committed and lost its answer.
- A metrics listener that throws no longer strands claimed rows until their lease lapses.
- On PostgreSQL two first starts of one step no longer fail the later one with a unique violation.
- Recovery's stale child-latch read seeks `ix_checkpoints_pending_latches` instead of scanning `checkpoints`.
- A SQLite completion stamps every row it writes with one instant.

### Documentation

- The guides are [`docs/developer-guide.md`](./developer-guide.md) for writing jobs and [`docs/operator-guide.md`](./operator-guide.md) for running them; contributor pages moved into `CONTRIBUTING.md`.
- The latest benchmark round is [`docs/benchmark.md`](./benchmark.md); the release notes record each certification verdict.

### Evidence

Certified on `ba12ef95`; the tagged commit adds test fixes and leaves `src/` unchanged. The three standard gates (10,000 jobs, 5 steps of 1 s, 7 minutes of chaos; PostgreSQL and SQL Server on 8 workers of 8 executors, SQLite on 6 of 8) and the PostgreSQL ensemble (three processes over two namespaces, 4,000 jobs) passed, with kills in flight in every work shape. Both million-job runs, on PostgreSQL and SQL Server (1,005,000 jobs each, 24 workers of 64 executors, 5 steps of 250 ms, 12 minutes of chaos), passed the same way. The benchmark round on the same commit is [`docs/reference/benchmark.md` at the tag](https://github.com/acta-dotnet/acta/blob/v1.0.0-rc.5/docs/reference/benchmark.md). Against rc.4's best profile on the same machine, drain more than doubles on PostgreSQL and SQL Server and grows fivefold on SQLite, and pickup latency and enqueue are level or ahead on the servers. Three cells trail: SQLite picks up one job at a time in about 20 ms on Windows ([known limitations](./known-limitations.md)), its single enqueues run at about 1,400 a second now that every commit syncs, and one deep lane runs about a quarter below rc.4's Direct profile, since each handoff waits for the previous completion to commit.

## Earlier releases

The notes for 1.0.0-rc.4 and every release before it are in [the file at that tag](https://github.com/acta-dotnet/acta/blob/v1.0.0-rc.4/docs/release-notes.md).
