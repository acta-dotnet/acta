# Alerting

## Purpose

How a job failure becomes an alert row, how that row reaches a channel, and what an operator
configures. The organising principle is that **alerting works the same for every job**: one
`AlertProfile` means one thing whatever the job is, and an alert's lifecycle does not depend on how
the job was started. Two places today's behaviour does not honour that principle, and both are named
in [Where the principle breaks](#where-the-principle-breaks) rather than smoothed over.

Every behavioural claim below cites a symbol — the member or constant that carries the behaviour — or,
for SQL, a filename. [Sources](#sources) maps each filename to its repository path.

## One recurring job does all of it

`sys.alerts` is a framework recurring job registered per worker namespace: `Priority = Critical`,
`AuditLevel = Failures`, `AlertProfile = SysCritical`, `Cron.EveryMinute`
(`AlertsJob.Handle`), competitively claimed once per namespace (`AlertsJob`). One pass is
generate, then deliver, in that order (`AlertsJob.Handle`).

Four consequences an operator sees:

- **Alerts are not written at failure time.** The row appears on the next tick, so up to about a
  minute after the event that caused it.
- **Generate and deliver share a pass**, so a row raised in a pass is normally sent in the same pass.
- **Generate drains up to 10,240 events per pass; deliver stays at 256.** Generate reads batches of
  256 in an inner loop bounded by 40 batches or 30 seconds of elapsed time, whichever comes first
  (`AlertsJob.GenerateMaxBatches`, `AlertsJob.GenerateTimeBudget`, `AlertsJob.GenerateAsync`), so a
  burst of a few thousand failures clears in one tick. The time budget is checked *between* batches,
  so the batch in flight always finishes. Deliver is deliberately not drained the same way and stays
  at 256 transport attempts per pass (`AlertsJob.DeliverBatchSize`): pushing ten thousand webhooks
  through one tick would trade a database problem for an outage on the operator's own channel. A
  namespace that outruns either bound falls behind. Nothing is lost — the cursor is durable, and it is
  written after **every** completed batch, so a pass cut short by a bound, a crash, or the framework's
  300 s execution timeout keeps everything it already projected — but the lag grows until the burst
  clears.
- **Generate walks `events` forward from a durable cursor** kept on the slot's own variable bag
  (`AlertsJob.CursorVariableName`, `AlertsJob.GenerateAsync`).

A deterministic poison event — a channel name or deduplication key that fails canonicalization, or a
job row purged between the event write and its projection — is recorded as a durable variable on the
projector job and skipped, so one malformed event cannot wedge the cursor
(`AlertsJob.GenerateAsync`, `AlertsJob.RecordProjectionSkipAsync`, `AlertsJob.EmitAsync`). A transient
failure retains the cursor and retries the pass.

## What projects an alert

Only `job.execution-finished` events, and only three shapes of them (`GetAlertableEvents.sql`):

| Event shape | `to_status` | Reason code | Treated as |
| --- | --- | --- | --- |
| Terminal failure | `Failed` (200) | any | Terminal failure |
| Re-arm after a failed attempt | `Ready` (10) | `job.unhandled-exception` (20), `job.lease-expired` (21), `job.execution-timeout` (22) | Non-terminal failure |
| Successful execution | any | any | Resolution |

All of this rides the event stream, so the job's `AuditLevel` decides whether alerting can see the job
at all. The default `Audit` writes every `job.execution-finished`; **`AuditLevel = Failures` writes it
only for terminal, non-re-arm failures** (`CompleteExecution.routine.sql`). Under `Failures` there are
no re-arm events — so `FirstFailure` and `ThresholdReached` never fire — and no success events — so
automatic resolution never runs, and a `FinalFailure` raised for a job an operator later restarts to
success stays open until resolved by hand. A job that opts down to `Failures` keeps exactly one working
alert shape, `FinalFailure`, with no self-resolution. The full alert lifecycle requires the default
audit level.

Everything else is invisible to alerting. Two exclusions are worth stating outright:

- **A `Cancelled` landing never alerts, under any profile.** An operator cancel, `ctx.CancelAsync`
  (`JobExecution.RunAsync`), and a whole-job deadline — both the "overdue at admission" and the
  "next retry would exceed the deadline" paths (`JobExecution.RunAsync`) — all land the job
  `Cancelled`, and the alertable set has no `Cancelled` branch.
- **Not every re-arm alerts.** A budget-neutral re-arm carrying `job.exclusive-key-held` (62),
  `job.step-retry-scheduled` (61), or `job.attempt-aborted` (25) falls outside the three reason codes
  above, so the attempt ends without producing an alert even though it did not succeed
  (`JobEventReasonCode.JobExclusiveKeyHeld`, `JobEventReasonCode.JobStepRetryScheduled`,
  `JobEventReasonCode.JobAttemptAborted`).

A recurring slot's rollover carries the failed attempt's reason through onto its `Ready` event
(`JobExecution.ComputeRecurringOutcome`). That is the only reason a nightly job that throws every
night is visible to the projector at all.

## Profiles

`JobAttribute.AlertProfile` defaults to `OnFailure` (`JobAttribute.AlertProfile`), so a job that
declares nothing about alerting is opted in.

| Profile | Non-terminal failure | Terminal `Failed` | Success |
| --- | --- | --- | --- |
| `None` | nothing | nothing | nothing |
| `OnFailure` (default) | `FirstFailure` at `Warning`; `ThresholdReached` at `Error` on the Nth | `FinalFailure` at `Error` | resolves |
| `Info` | nothing | `FinalFailure` at `Info` | resolves |
| `OnTerminal` | nothing | `FinalFailure` at `Error` | resolves |
| `SysCritical` | `FirstFailure` at `Critical`; `ThresholdReached` at `Critical` | `FinalFailure` at `Critical` | resolves |

The branches are one method: `None` returns immediately (`AlertsJob.ProjectAsync`); a success resolves
and writes no alert of its own (`AlertsJob.ProjectAsync`); `to_status = Failed` emits `FinalFailure` for
every remaining profile (`AlertsJob.ProjectAsync`); anything else is a non-terminal failure and only
`OnFailure` and `SysCritical` continue past the gate (`AlertsJob.ProjectAsync`).

`SysCritical` is reserved for framework jobs. It raises severity and nothing else: routing is uniform
across every profile — the declared `AlertChannelName`, else the configured `default` channel — so
system-job failures reach the logs with no operator configuration (`AlertsJob.ProjectAsync`).

`None` suppresses automatic alerts only. `ctx.AlertAsync` still writes rows from a `None` job
(`AlertProfileCode.None`).

## Identity and deduplication

An automatic alert's deduplication key is

```
auto:{DefinitionId}:{JobId}:{kind}:{reasonCode ?? "none"}
```

(`AlertsJob.EmitAsync`). This key names an **incident**, not a time bucket: `alerts` carries a unique
index on `(namespace_id, dedupe_key)` filtered to unresolved rows, so at most one OPEN row exists per
key at any moment (`JobAlert.cs`). A repeat of the same condition while that row is open folds onto it
— `occurrence_count + 1`, title/message/severity refreshed — with no time component at all
(`RaiseJobAlert.routine.sql`). `resolved_at_utc` is terminal for a row (see [Resolution](#resolution)):
once a success stamps it, the row never reopens, and the next failure on the same key opens a fresh
incident with a fresh ref and fresh delivery state.

Four things follow from the key's shape:

- **Job-scoped.** A fan-out of sibling jobs of one definition each gets its own row, and a success
  resolves only that job's failures.
- **Kind-scoped.** `FirstFailure`, `ThresholdReached`, and `FinalFailure` are separate rows, not
  states of one row.
- **Reason-scoped.** A job alternating between `job.unhandled-exception` and `job.lease-expired`
  opens a separate incident for each reason; both can be open at once.
- **Stable across occurrences of a recurring job.** A recurring schedule is one job row re-armed, not
  a row per occurrence ([concepts](./concepts.md), glossary and "Schedules, workers, and providers"),
  so the job id — and therefore the alert identity — does not move from occurrence to occurrence.

Automatic writes are replay-safe. Each carries the projecting event's id, and the upsert increments and
re-stamps only when that id is strictly newer than the row's `last_projected_event_id`
(`AlertsJob.EmitAsync`, `RaiseJobAlert.routine.sql`). Because resolution is terminal, a failure event
replayed after the incident it belongs to has already closed must not reopen it: the insert arm is
guarded against every row of the identity, resolved ones included, not just the open one, so a raise
whose event the identity has already absorbed at or past that id opens nothing — the ghost-incident
guard (`RaiseJobAlert.routine.sql`). The public `alert_ref` is applied on the INSERT arm only, and is
absent from the `DO UPDATE SET` list (`RaiseJobAlert.routine.sql`), so a row keeps the ref its first
firing minted for as long as the incident stays open, and the next incident on the same key gets a ref
of its own.

## Resolution

A successful execution resolves that job's open automatic alerts and writes nothing
(`AlertsJob.ProjectAsync`, `ResolveJobAlerts.sql`). It is:

- **Job-instance-scoped** — `job_id` and `origin = Automatic`, kinds `FirstFailure` /
  `ThresholdReached` / `FinalFailure`, and only rows still unresolved (`ResolveJobAlerts.sql`).
- **Ordered.** Resolution applies only to alerts whose `last_projected_event_id` precedes the success
  event (`ResolveJobAlerts.sql`), so a replayed success cannot close an alert that a later failure
  opened.
- **Idempotent, and run on every success.** Within one batch a job can go fail → success → fail →
  success, where the second failure opens a fresh incident on the same key (the first is already
  resolved); resolving each success is what keeps that second incident from lingering unresolved
  (`AlertsJob.ProjectAsync`).
- **Dependent on the success event existing.** `AuditLevel = Failures` suppresses success events at the
  source (see [What projects an alert](#what-projects-an-alert)), so under it nothing ever drives this
  path — recovery does not resolve, whatever the profile's description promises.

Manual alerts are a separate path throughout — separate by key, not by wall. `ctx.AlertAsync`
writes `origin = Manual`, `kind = Manual` (`JobContext.AlertAsync`,
`AlertStoreSink.RaiseManualAsync`), which the automatic resolve never matches. A caller that
deliberately reuses an automatic `auto:` key merges its raise onto that incident and takes over
its lifecycle; Acta does not police key choice, it deduplicates on it. The operator verbs `IAlerts.AcknowledgeAsync` and `IAlerts.ResolveAsync`
(`IAlerts.AcknowledgeAsync`, `IAlerts.ResolveAsync`) are idempotent, emit `alert.acknowledged` (140) /
`alert.resolved` (141) regardless of the job's audit level (`EventCode.AlertAcknowledged`,
`EventCode.AlertResolved`), and leave `last_projected_event_id` untouched
(`ResolveJobAlertManual.routine.sql`). Neither verb changes the underlying job.

Reminders follow the same split. A manual alert that delivers is notified once and never again: Acta
has no way to tell whether one handler's statement still holds, so it schedules no reminder and the
caller owns resolving the row (`AlertsJob.SettleAsync`). A manual alert whose *send* failed is the
exception — nobody has been told yet, so it is re-attempted on the reminder cadence until it lands,
and stops there.

## How much a broken job actually sends

These numbers are measured against the code, not estimated.

`ThresholdReached` fires when the `FirstFailure` raise applies and the row's post-upsert occurrence
count **equals** `AlertFailureThreshold` (`AlertsJob.ProjectAsync`; default 3,
`JobsOptions.AlertFailureThreshold`). "Applies" is read off the raise's returned
`last_projected_event_id`: only the event the row just absorbed may escalate, so a crash-replay —
whose held raises all return the stored, already-at-threshold count — re-fires the escalation only
from the true crossing event, where the `ThresholdReached` row's own high-water guard settles it. The
count is monotonic **within one incident**: it starts at 1 when the incident opens and only climbs
until a success resolves it, so the threshold is crossed at most once per incident and reads as "N
failures since this broke" whatever the job's cadence — a slow job just takes longer to reach it, it
never becomes unreachable. Only the next incident, opened by the next failure after a resolution,
restarts the count at 1 (`JobsOptions.AlertFailureThreshold`).

### Volume

Delivery selects an unresolved incident whose delivery is `Pending`/`RetryAfter` due now, or whose
delivery already settled (`Delivered`/`Failed`) and whose scheduled reminder has come
(`GetDeliverableAlerts.sql`; see [Delivery](#delivery)). Every newly inserted row is born `Pending`
(`AlertsJob.EmitAsync`, `RaiseJobAlert.routine.sql`), so a new incident's first notification is
immediate.

**A job that is permanently broken produces exactly one open incident row per (kind, reason) — one for
`FirstFailure`, one for `ThresholdReached` once the count crosses it — no matter how fast or how long
it keeps failing.** `occurrence_count` on that row climbs with every repeat; no new row appears until
the incident resolves. A 10,000-job outage is 10,000 open rows (one per failing job, per kind and
reason it hits), not 2–3 accumulated rows per job per rolling window under the retired windowed model.

| | One-shot job | Recurring slot |
| --- | --- | --- |
| Failures before it stops | at most `MaxAttempts` (default 15, `JobAttribute.MaxAttempts`) | unbounded; the slot re-arms forever (`JobExecution.ComputeRecurringOutcome`) |
| Alert rows on defaults | at most three: `FirstFailure`, `ThresholdReached`, `FinalFailure` | two, for as long as the slot stays broken |
| Ends by itself | yes, at the terminal `Failed` | only on a success, or operator intervention |

Row count does not grow with time or failure rate; what changes with time is how often an open
incident is *delivered* again. `AlertReminderInterval` is that lever (default 24h,
`JobsOptions.AlertReminderInterval`): settling an incident's delivery to `Delivered` or `Failed` —
`Suppressed` is not reminded — schedules the next pass an interval out, so a permanently broken job
pages at most once per incident per interval rather than once per failure. A manual alert that
delivered schedules nothing (see [Resolution](#resolution)). Widening the interval slows the
reminder cadence; it does not change how many rows exist or whether `ThresholdReached` is reachable.

## Delivery

The deliver phase selects due rows and settles each one (`AlertsJob.DeliverAsync`). Due means an
unresolved incident (`resolved_at_utc IS NULL`) and one of two arms, both reading `retry_after_utc`
as the row's "not before" instant: `Pending`/`RetryAfter` with it null or past — a first attempt or a
due retry — or `Delivered`/`Failed` with it past — a reminder, at the instant that settlement
scheduled. `Suppressed` rows are never reminded: that status recorded a routing decision about the
channel, and re-sending would only re-take it. Ordered by id, 256 per pass
(`GetDeliverableAlerts.sql`).

The reminder is scheduled rather than inferred from `modified_at_utc`, and that is not a detail: every
repeat an open incident absorbs re-stamps that column, so an age rule would leave a job failing faster
than the interval permanently too young to remind — silencing exactly the outage worth re-notifying.
Nothing but a settlement or a resolution moves the instant, so neither a repeat nor an operator's
acknowledge can postpone a reminder.

For each row it resolves the channel by name in the firing namespace, resolves a transport by the
channel's kind, and decides in this fixed order (`AlertChannelDecision.Decide`):

| Condition | Outcome | Delivery status |
| --- | --- | --- |
| No channel of that name configured for the namespace | Failed | `Failed` (200) |
| Channel `Disabled` | Suppressed | `Suppressed` (30) |
| Channel `Deprecated` | Suppressed | `Suppressed` (30) |
| Alert severity below the channel's `MinSeverity` | Suppressed | `Suppressed` (30) |
| No transport registered for the channel's kind | Failed | `Failed` (200) |
| Otherwise | Send | per the transport's outcome |

A transport that throws is treated as retryable and logged at Warning, never propagated
(`AlertsJob.SendAsync`). Settlement (`AlertsJob.SettleAsync`) writes a compare-and-swap against the
`version` the row carried at selection (`UpdateAlertDelivery.sql`): a row that moved in the
meantime — an operator resolved it, or a competing worker already settled the same attempt — matches
no version, and the write is skipped with a Debug log line rather than retried, because the newer state
is the one that should stand (`AlertsJob.WriteSettlementAsync`).

- `Delivered` (100) — terminal, and `retry_count` resets to 0: the send series that just landed is
  over.
- `Retryable` — `retry_count + 1`; below `AlertDeliveryMaxRetries` the row becomes `RetryAfter` (20)
  with `retry_after_utc` set from a `30s..1h` doubling curve with 10% jitter, parsed once and
  independent of any job's backoff policy (`AlertsJob.RetryBackoff`, `AlertsJob.SettleAsync`).
- At the cap — `Failed` (200), terminal.

`retry_count` is the budget for one **send series**, not a lifetime count on the row
(`JobAlert.RetryCount`). `AlertDeliveryMaxRetries` is `internal` and fixed at 5
(`JobsOptions.AlertDeliveryMaxRetries`): five send attempts and four waits of roughly 30s, 1m, 2m, and
4m (`BackoffSchedule.ComputeDelaySeconds`), so the one-hour ceiling in the curve is never reached and a
series that cannot be delivered goes terminal `Failed` in under ten minutes of tick time. Because
delivery runs on the one-minute tick, each wait rounds up to the next tick boundary. `Delivered`
resets the budget to 0, so a reminder for a still-open incident starts with the whole curve again
rather than inheriting whatever the previous series spent — a row that took four attempts to land does
not walk into its next reminder one throw from the cap.

Delivery is at least once: a crash after send but before settlement can resend a duplicate
(`AlertsJob`).

The delivery guarantee:

> An alert resolved before delivery selection is not sent. Resolution suppresses further pending and
> retry attempts. A transport attempt already in progress may still complete.

Both resolve paths settle the delivery they close (`ResolveJobAlerts.sql`,
`ResolveJobAlertManual.routine.sql`): a `Pending` or `RetryAfter` row moves to `Suppressed` and its
retry timer clears, so a notification queued for a condition that has cleared is cancelled rather than
sent. An already-settled row (`Delivered`, `Failed`, `Suppressed`) keeps its status — resolving does
not edit the record of what the send actually did. Selection itself excludes every resolved row
(`GetDeliverableAlerts.sql`), and settlement's compare-and-swap is what makes a resolve race-safe
against a send already in flight: the resolve writes unconditionally and wins, and the in-flight
attempt's own settlement finds its expected version gone and quietly loses, per the guarantee above.

## Channels and routing

A channel is **process startup configuration**, declared on the worker builder and resolved from an
in-memory registry at delivery time (`WorkerBuilder.AddAlertChannel`, `AlertChannelRegistry`):

```csharp
j.Run("billing", w =>
{
    w.AddManifest<BillingJobs>();

    w.AddAlertChannel(
        "ops-slack",
        AlertTransportKinds.SlackWebhook,
        endpoint: builder.Configuration["Alerts:SlackWebhookUrl"]!,
        o =>
        {
            o.MinSeverity = AlertSeverityCode.Warning;
        });
});
```

- **Name and transport kind are operator-stable kebab identifiers; the endpoint is free-form**
  (a URL, ARN, or address) and only checked non-empty (`WorkerBuilder.AddAlertChannel`). Re-declaring
  a name replaces the earlier declaration rather than throwing (`WorkerBuilder.AddAlertChannel`).
- **Every worker namespace gets an implicit `default` channel** on the log transport, `Active`, with
  `MinSeverity = Info` (`AlertChannelRegistry.RegisterDefault`). Declaring `default` yourself
  overrides it. This is why failures reach the logs out of the box.
- **`MinSeverity` and `Status` are the two per-channel policies** (`AlertChannelOptions.MinSeverity`,
  `AlertChannelOptions.Status`). `Disabled` and `Deprecated` both suppress; the difference is what the
  log line says (`AlertsJob.LogSuppressedDecision`).
- **Built-in transport kinds are `log` and `slack-webhook`** (`AlertTransportKinds.Log`,
  `AlertTransportKinds.SlackWebhook`). A custom transport defines its own kebab kind and registers an
  `IAlertTransport`.
- **Acta SQL stores the channel *name* only.** Endpoints, webhook URLs, credentials, and routing keys
  are never persisted: the delivery target is assembled at send time from the registry declaration
  (`AlertsJob.SendAsync`). Keep real endpoints in configuration or a secret store.

Route a job with `[Job(AlertChannelName = "ops-slack")]` (`JobAttribute.AlertChannelName`); a job that
declares none routes to `default`. `RunbookUrl` on the same attribute (`JobAttribute.RunbookUrl`) is
resolved at delivery time from the definition and carried on the notification
(`GetDeliverableAlerts.sql`, `AlertsJob.SendAsync`); it is not on the list-query projection.

`AlertChannelValidationMode` checks at worker startup that every alerting definition routes to a
channel configured in its namespace: `Off` skips, `Warn` (default) logs each unroutable definition,
`Fail` throws out of worker initialization (`JobsOptions.AlertChannelValidationMode`,
`AlertRoutingCheck.ValidateRouting`). Disabled and deprecated channels count as configured — that is a
delivery decision, not a routing one. The check covers **definitions only**: a definition with
`AlertProfile = None` is skipped (`AlertRoutingCheck.ValidateRouting`), and a `channelName` passed to
`ctx.AlertAsync` is not validated at startup at all. A manual alert naming a channel the namespace does
not configure settles `Failed` with no retry on its first delivery pass.

## Operator surface

Alert rows are queryable like everything else. `IActaOperations.Alerts` offers `ListAsync` (filtered
by namespace, job, resolution, acknowledgement, severity, delivery status, and tags), `GetAsync`,
`AcknowledgeAsync`, and `ResolveAsync` (`IAlerts`, `ListAlertsQuery`). The dashboard and HTTP API
expose the two verbs at `POST /alerts/{alertRef}/acknowledge|resolve`, 200 or 404 and never 409, behind
`EnableControls` and the `X-Acta-Control` header (the gate lives in `ActaApiEndpoints`, the header
name in `ActaEndpointOptions.ControlConfirmationHeaderName`; the verbs themselves in
`AlertControlEndpoints`). In SQL, `acta.alerts_view` renders the coded columns and aliases
`dedupe_key` to `deduplication_key` (`AlertsView.view.sql`); see [SQL recipes](./sql-recipes.md) for
ready queries.

Acknowledge means an operator has seen the alert; resolve means the incident is considered settled.
Neither changes the job. Acknowledging also does not quiet the alert: a reminder already due still
fires, because seeing an outage is not fixing one. Resolving is what ends the reminders — it clears
the row's scheduled instant along with everything else it settles
(`ResolveJobAlertManual.routine.sql`).

Retention is separate from the job's. `sys.retention` deletes alerts older than `AlertRetention`
(default 90 days, `JobsOptions.AlertRetention`) **only when delivery has settled** — `Suppressed`,
`Delivered`, or `Failed` (`PurgeExpiredData.routine.sql`). A row stuck `Pending` or `RetryAfter` is
never aged out. Because an alert keeps its own copy of the job ref, it can outlive the job it was
raised for; a manual `PurgeAsync` on the job deletes its alerts immediately. See
[Operator guide § retention and purge](./operator-guide.md).

## Where the principle breaks

The intended rule is that one profile means one thing for every job and the alert lifecycle is
independent of how the job was started. Two divergences remain, and both trace to a single root
cause: **a recurring slot has no terminal state.** `MaxAttempts` is the one-shot retry budget, and a
recurring slot re-arms instead of terminalizing however many consecutive runs throw
(`JobExecution.ComputeRecurringOutcome`). That is deliberate — a nightly job must not die permanently
after three bad nights — but the alerting consequences are real.

### 1. `OnTerminal` and `Info` are silent for a recurring job's ordinary failures

Both wait for the terminal transition, and a rollover is not one, so a recurring job that throws or
loses its lease alerts nothing under either profile. Recorded in
[Known limitations § execution model](../technical/known-limitations.md); the recommendation there —
choose `OnFailure` for recurring work you want to hear about — is the right one.

They do still fire on the terminal `Failed` shapes a recurring slot can reach, all three of which
route through the handler-status completion path and stop the whole slot
(`JobExecution.RunAsync`):

| Shape | Reason code | Where |
| --- | --- | --- |
| Handler declares failure with `ctx.FailAsync` | `job.handler-failed` (52) | `JobExecution.RunAsync` |
| Handler throws `NotImplementedException` / `NotSupportedException` | `job.non-retryable-exception` (23) | `JobExecution.RunAsync` |
| An `AtMostOnce()` step was re-entered before its outcome was recorded, uncaught | `job.step-interrupted` (63) | `JobExecution.RunAsync` |

A whole-job deadline is **not** one of them: the deadline lands the job `Cancelled`, not `Failed`
(`JobExecution.RunAsync`, `JobEventReasonCode.JobDeadlineExceeded`), and the alertable set has no
`Cancelled` branch (`GetAlertableEvents.sql`). No profile alerts on a deadline — the `on-terminal`
`[Code]` description (`AlertProfileCode.OnTerminal`) and
[Known limitations](../technical/known-limitations.md) both say the same.

### 2. A one-shot job's alerting ends by itself; a recurring job's does not

A one-shot job fails at most `MaxAttempts` times and then terminalizes, so its `FirstFailure` /
`ThresholdReached` / `FinalFailure` incidents resolve or age out with the job. A permanently broken
recurring slot never produces the success that would resolve its incident, so `FirstFailure` (and,
once crossed, `ThresholdReached`) stay open indefinitely: one row each, `occurrence_count` climbing
with every re-arm, delivered again on `AlertReminderInterval` rather than replaced by a fresh row.
Same profile, same configuration, a lifecycle that ends by itself in one case and only on success or
operator intervention in the other.

Neither of these is a feature. They are the current behaviour, and they are the two places where
reading the profile name does not tell you what the job will do.

## Choosing a profile

**One-shot work.** `OnFailure` (the default) is the honest choice. You get a `FirstFailure` warning
on the first retryable failure, an `Error` escalation if the same reason recurs to the threshold
before the job exhausts its retries, and a `FinalFailure` when the budget is exhausted — at most three
rows, and the sequence ends by itself. Pick `OnTerminal` when the intermediate retries are noise and
only the final outcome should page; it costs you the early warning, and for a one-shot job it costs
nothing else. `Info` is `OnTerminal` at a severity most channels will not page on — useful for work
whose failure is a note rather than an incident, but check the routed channel's `MinSeverity` first,
because `Info` alerts are exactly what a `Warning` floor suppresses.

**Recurring work.** `OnFailure`, or you will hear nothing. `OnTerminal` and `Info` are silent for the
rollover case, which is how a recurring job fails nearly all the time. Accept that the incident does
not close on its own: it opens once, reminds on `AlertReminderInterval` (once a day by default) for as
long as the slot stays broken, and treat "this alert is still open" — not "a new alert appeared" — as
the signal that nobody has fixed it. `ThresholdReached` is reachable at every cadence, because the
count is per incident rather than per window: it fires once the Nth failure since the incident opened
lands, however slowly that takes. If you want a recurring job to stop rather than keep failing, that
is a handler decision — `ctx.FailAsync` — not a profile one.

**System work.** `SysCritical` is reserved for Acta's own jobs (`sys.alerts`, `sys.recovery`,
`sys.retention`, `sys.outbox`) and behaves as `OnFailure` pinned to `Critical` severity. There is no
reason for an application job to declare it: the same emissions are available from `OnFailure`, and
taking `Critical` for application work removes the one severity that distinguishes framework failure
from application failure on a shared channel.

**Handler-owned alerting.** `None` turns off automatic alerting without turning off
`ctx.AlertAsync`, so a handler that wants to alert on its own terms — its own key, its own severity,
its own channel — can, while
the framework stays quiet. It also removes the job from the startup routing check, so a manual alert
from a `None` job naming an unconfigured channel is not caught until it fails delivery.

## Sources

| Cited as | Path |
| --- | --- |
| `AlertsJob.cs` | `src/Acta.Runtime/Modules/Alerting/AlertsJob.cs` |
| `AlertChannelDecision.cs` | `src/Acta.Runtime/Modules/Alerting/AlertChannelDecision.cs` |
| `AlertChannelRegistry.cs` | `src/Acta.Runtime/Modules/Alerting/AlertChannelRegistry.cs` |
| `AlertRoutingCheck.cs` | `src/Acta.Runtime/Modules/Alerting/AlertRoutingCheck.cs` |
| `AlertStoreSink.cs` | `src/Acta.Runtime/Modules/Alerting/AlertStoreSink.cs` |
| `JobAlert.cs` | `src/Acta.Relational/Entities/JobAlert.cs` |
| `AlertProfileCode.cs` | `src/Acta/Alerts/AlertProfileCode.cs` |
| `AlertChannelOptions.cs` | `src/Acta/Alerts/AlertChannelOptions.cs` |
| `AlertTransportKinds.cs` | `src/Acta/Alerts/AlertTransportKinds.cs` |
| `IAlerts.cs` | `src/Acta/Alerts/IAlerts.cs` |
| `ListAlertsQuery.cs` | `src/Acta/Alerts/ListAlertsQuery.cs` |
| `AlertControlEndpoints.cs` | `src/Acta.AspNetCore/Features/Alerts/AlertControlEndpoints.cs` |
| `JobAttribute.cs` | `src/Acta/Jobs/JobAttribute.cs` |
| `JobsOptions.cs` | `src/Acta/Configuration/JobsOptions.cs` |
| `JobContext.cs` | `src/Acta/Execution/JobContext.cs` |
| `JobExecution.cs` | `src/Acta.Runtime/Modules/Execution/JobExecution.cs` |
| `JobEventReasonCode.cs` | `src/Acta/Events/JobEventReasonCode.cs` |
| `EventCode.cs` | `src/Acta/Events/EventCode.cs` |
| `BackoffSchedule.cs` | `src/Acta.Runtime/Kernel/BackoffSchedule.cs` |
| `WorkerBuilder.cs` | `src/Acta.Runtime/Hosting/WorkerBuilder.cs` |
| `*.sql` | `src/Acta.Postgres/Sql/{Alerting,Maintenance}/` (SQL Server and SQLite carry dialect twins) |

Runnable examples: `concepts/400-observability-and-alerts/402-alerts` (manual alert),
`403-alert-channel` (a declared channel with a severity floor), `405-real-alert-routing` (Slack
webhook and a shared deduplication key), `406-automatic-failure-alerts` (no `AlertAsync` at all), and
`411-alert-escalation` (`FirstFailure` → `ThresholdReached` → `FinalFailure`).

For failure semantics behind the alerts, see [Failure modes](./failure-modes.md); for the day-2
surfaces, [Operator guide](./operator-guide.md); for `AlertReminderInterval`, `AlertFailureThreshold`,
and the rest of the option surface, [Configuration](./configuration.md); for startup wiring,
[Production § alert channel setup](./production.md).
