# Alerting

## Purpose

How a job failure becomes an alert row, how that row reaches a channel, and what an operator
configures. The organising principle is that **alerting works the same for every job**: one
`AlertProfile` means one thing whatever the job is, and an alert's lifecycle does not depend on how
the job was started. Two places today's behaviour does not honour that principle, and both are named
in [Where the principle breaks](#where-the-principle-breaks) rather than smoothed over.

Every behavioural claim below cites `file:line`. [Sources](#sources) maps each filename to its
repository path.

## One recurring job does all of it

`sys.alerts` is a framework recurring job registered per worker namespace: `Priority = Critical`,
`AuditLevel = Failures`, `AlertProfile = SysCritical`, `Cron.EveryMinute`
(`AlertsJob.cs:56-62`), competitively claimed once per namespace (`AlertsJob.cs:10-18`). One pass is
generate, then deliver, in that order (`AlertsJob.cs:63-68`).

Four consequences an operator sees:

- **Alerts are not written at failure time.** The row appears on the next tick, so up to about a
  minute after the event that caused it.
- **Generate and deliver share a pass**, so a row raised in a pass is normally sent in the same pass.
- **Each phase is capped at 256 rows per pass per namespace** (`AlertsJob.cs:34-35`). A namespace
  producing more alertable events than that per minute falls behind. Nothing is lost — the cursor is
  durable — but the lag grows until the burst clears.
- **Generate walks `events` forward from a durable cursor** kept on the slot's own variable bag
  (`AlertsJob.cs:31,72,95-98`).

A deterministic poison event — a channel name or deduplication key that fails canonicalization, or a
job row purged between the event write and its projection — is recorded as a durable variable on the
projector job and skipped, so one malformed event cannot wedge the cursor
(`AlertsJob.cs:86-93,101-126,235-251`). A transient failure retains the cursor and retries the pass.

## What projects an alert

Only `job.execution-finished` events, and only three shapes of them (`GetAlertableEvents.sql:14-23`):

| Event shape | `to_status` | Reason code | Treated as |
| --- | --- | --- | --- |
| Terminal failure | `Failed` (200) | any | Terminal failure |
| Re-arm after a failed attempt | `Ready` (10) | `job.unhandled-exception` (20), `job.lease-expired` (21), `job.execution-timeout` (22) | Non-terminal failure |
| Successful execution | any | any | Resolution |

Everything else is invisible to alerting. Two exclusions are worth stating outright:

- **A `Cancelled` landing never alerts, under any profile.** An operator cancel, `ctx.CancelAsync`
  (`JobExecution.cs:258-263`), and a whole-job deadline — both the "overdue at admission" and the
  "next retry would exceed the deadline" paths (`JobExecution.cs:407-415,447-457`) — all land the job
  `Cancelled`, and the alertable set has no `Cancelled` branch.
- **Not every re-arm alerts.** A budget-neutral re-arm carrying `job.exclusive-key-held` (62),
  `job.step-retry-scheduled` (61), or `job.attempt-aborted` (25) falls outside the three reason codes
  above, so the attempt ends without producing an alert even though it did not succeed
  (`JobEventReasonCode.cs:51-55,101-111`).

A recurring slot's rollover carries the failed attempt's reason through onto its `Ready` event
(`JobExecution.cs:729-733,759`). That is the only reason a nightly job that throws every night is
visible to the projector at all.

## Profiles

`JobAttribute.AlertProfile` defaults to `OnFailure` (`JobAttribute.cs:118-122`), so a job that
declares nothing about alerting is opted in.

| Profile | Non-terminal failure | Terminal `Failed` | Success |
| --- | --- | --- | --- |
| `None` | nothing | nothing | nothing |
| `OnFailure` (default) | `FirstFailure` at `Warning`; `ThresholdReached` at `Error` on the Nth | `FinalFailure` at `Error` | resolves |
| `Info` | nothing | `FinalFailure` at `Info` | resolves |
| `OnTerminal` | nothing | `FinalFailure` at `Error` | resolves |
| `SysCritical` | `FirstFailure` at `Critical`; `ThresholdReached` at `Critical` | `FinalFailure` at `Critical` | resolves |

The branches are one method: `None` returns immediately (`AlertsJob.cs:135-138`); a success resolves
and writes no alert of its own (`AlertsJob.cs:145-161`); `to_status = Failed` emits `FinalFailure` for
every remaining profile (`AlertsJob.cs:163-170`); anything else is a non-terminal failure and only
`OnFailure` and `SysCritical` continue past the gate (`AlertsJob.cs:172-192`).

`SysCritical` is reserved for framework jobs. It raises severity and nothing else: routing is uniform
across every profile — the declared `AlertChannelName`, else the configured `default` channel — so
system-job failures reach the logs with no operator configuration (`AlertsJob.cs:140-143`).

`None` suppresses automatic alerts only. `ctx.AlertAsync` still writes rows from a `None` job
(`AlertProfileCode.cs:9`).

## Identity, the window, and deduplication

An automatic alert's deduplication key is

```
auto:{DefinitionId}:{JobId}:{kind}:{reasonCode ?? "none"}
```

(`AlertsJob.cs:205-209`). The `alerts` table is unique on
`(namespace_id, dedupe_key, dedupe_window_start_utc)`, and a repeat inside one window folds onto that
row: `occurrence_count + 1`, `resolved_at_utc` cleared, title/message/severity refreshed
(`RaiseJobAlert.routine.sql:121-135`).

Four things follow from the key's shape:

- **Job-scoped.** A fan-out of sibling jobs of one definition each gets its own row, and a success
  resolves only that job's failures.
- **Kind-scoped.** `FirstFailure`, `ThresholdReached`, and `FinalFailure` are separate rows, not
  states of one row.
- **Reason-scoped.** A job alternating between `job.unhandled-exception` and `job.lease-expired`
  opens two rows in the same window.
- **Stable across occurrences of a recurring job.** A recurring schedule is one job row re-armed, not
  a row per occurrence ([concepts](./concepts.md), glossary and "Schedules, workers, and providers"),
  so the job id — and therefore the alert identity — does not move from occurrence to occurrence.

The window is `AlertWindow.FloorStart(now, AlertDedupeWindow)` (`AlertsJob.cs:73`,
`AlertWindow.cs:14-22`). `AlertDedupeWindow` is a public `JobsOptions` setting defaulting to four
hours (`JobsOptions.cs:194-209`). It is the rate limit on automatic alerting, and it also decides
whether `ThresholdReached` is reachable for a given job at all — see
[How much a broken job actually sends](#how-much-a-broken-job-actually-sends).

The start is **floored on the wall clock**, so windows are aligned buckets (…00:00, 04:00, 08:00,
12:00 on the default), not "four hours from the first failure". Two failures a second apart but
either side of a boundary land in different buckets and produce two rows.

Automatic writes are replay-safe. Each carries the projecting event's id, and the upsert increments,
re-opens, and re-stamps only when that id is strictly newer than the row's `last_projected_event_id`
(`AlertsJob.cs:228-233`, `RaiseJobAlert.routine.sql:133,136-139`). Re-projecting a batch after a crash
changes nothing. The public `alert_ref` is applied on the INSERT arm only
(`RaiseJobAlert.routine.sql:105`; absent from the `DO UPDATE SET` list at `:122-135`), so a row keeps
the ref its first firing minted however many times it re-fires.

## Resolution

A successful execution resolves that job's open automatic alerts and writes nothing
(`AlertsJob.cs:145-161`, `ResolveJobAlerts.sql:1-13`). It is:

- **Job-instance-scoped** — `job_id` and `origin = Automatic`, kinds `FirstFailure` /
  `ThresholdReached` / `FinalFailure`, and only rows still unresolved (`ResolveJobAlerts.sql:8-12`).
- **Ordered.** Resolution applies only to alerts whose `last_projected_event_id` precedes the success
  event (`ResolveJobAlerts.sql:13`), so a replayed success cannot close an alert that a later failure
  opened.
- **Idempotent, and run on every success.** Within one batch a job can go fail → success → fail →
  success; resolving each success is what stops the re-opened row from lingering unresolved
  (`AlertsJob.cs:148-155`).

Manual alerts are a separate path throughout. `ctx.AlertAsync` writes `origin = Manual`,
`kind = Manual` (`JobContext.cs:1122-1150`, `AlertStoreSink.cs:34-52`), which the automatic resolve
never matches. The operator verbs `IAlerts.AcknowledgeAsync` and `IAlerts.ResolveAsync`
(`IAlerts.cs:19-41`) are idempotent, emit `alert.acknowledged` (140) / `alert.resolved` (141)
regardless of the job's audit level (`EventCode.cs:158-166`), and leave `last_projected_event_id`
untouched (`ResolveJobAlertManual.routine.sql:45-50`). Neither verb changes the underlying job.

## How much a broken job actually sends

These numbers are measured against the code, not estimated.

`ThresholdReached` fires when the `FirstFailure` row's post-upsert occurrence count **equals**
`AlertFailureThreshold` (`AlertsJob.cs:179-192`; default 3, `JobsOptions.cs:219-231`). That count
belongs to the window's row, so it restarts at 1 in each new window and the threshold is crossed at
most once per window.

### The threshold counts failures inside one window, not consecutive failures

This is the limitation to read before tuning either setting. A job whose cadence does not fit
`AlertFailureThreshold` failures into a single dedupe window can never escalate, however long it
stays broken. With a four-hour window and a threshold of 3:

| Failure interval | Failures per window | `ThresholdReached` |
| --- | --- | --- |
| 80 minutes or shorter | 3 or more, in every window | every window |
| between 80 minutes and 2 hours | 2 or 3, depending on where the bucket boundary falls | some windows |
| 2 hours or longer | at most 2 | never |

The cutoff is the window divided by `AlertFailureThreshold`. At a one-hour window that put it at
twenty minutes: an hourly job failing for a year would produce a `FirstFailure` row every hour and
never once reach `ThresholdReached`. Four hours is the smallest window at which an hourly job
escalates, which is the reason for the default — not the noise arithmetic below, which a wider window
happens to improve as well. A nightly job still cannot escalate at any window you would want to
configure; for that cadence, `FirstFailure` is the whole signal.

### Volume

Delivery selects rows in `Pending` or `RetryAfter` (`GetDeliverableAlerts.sql:20`), and every newly
inserted row is born `Pending` (`AlertsJob.cs:227`, `RaiseJobAlert.routine.sql:114-116`). So each new
window's rows are real notifications, not silent bookkeeping.

**A job on a five-second schedule that is permanently broken produces, on defaults, two rows and two
notifications per four-hour window — 12 a day — while `occurrence_count` faithfully records the
roughly 2,880 failures in each of those windows.** The pair is one `FirstFailure` row and one
`ThresholdReached` row, new in each aligned bucket.

| | One-shot job | Recurring slot |
| --- | --- | --- |
| Failures before it stops | at most `MaxAttempts` (default 15, `JobAttribute.cs:19`) | unbounded; the slot re-arms forever (`JobExecution.cs:723-727`) |
| Alert rows on defaults | at most three: `FirstFailure`, `ThresholdReached`, `FinalFailure` | two per dedupe window, forever |
| Ends by itself | yes, at the terminal `Failed` | only on a success, or operator intervention |

The one lever over the recurring number is `AlertDedupeWindow`. Widening it divides the pair rate and
raises the cadence at which escalation is reachable; narrowing it does the reverse. It does not make
the sequence end.

## Delivery

The deliver phase reads due rows and settles each one (`AlertsJob.cs:269-293`). Due means
`Pending` or `RetryAfter` with `retry_after_utc` null or past, ordered by id, 256 per pass
(`GetDeliverableAlerts.sql:18-23`).

For each row it resolves the channel by name in the firing namespace, resolves a transport by the
channel's kind, and decides in this fixed order (`AlertChannelDecision.cs:24-49`):

| Condition | Outcome | Delivery status |
| --- | --- | --- |
| No channel of that name configured for the namespace | Failed | `Failed` (200) |
| Channel `Disabled` | Suppressed | `Suppressed` (30) |
| Channel `Deprecated` | Suppressed | `Suppressed` (30) |
| Alert severity below the channel's `MinSeverity` | Suppressed | `Suppressed` (30) |
| No transport registered for the channel's kind | Failed | `Failed` (200) |
| Otherwise | Send | per the transport's outcome |

A transport that throws is treated as retryable and logged at Warning, never propagated
(`AlertsJob.cs:324-342`). Settlement (`AlertsJob.cs:400-431`):

- `Delivered` (100) — terminal.
- `Retryable` — `retry_count + 1`; below `AlertDeliveryMaxRetries` the row becomes `RetryAfter` (20)
  with `retry_after_utc` set from a `30s..1h` doubling curve with 10% jitter, parsed once and
  independent of any job's backoff policy (`AlertsJob.cs:37-39,419-426`).
- At the cap — `Failed` (200), terminal.

`AlertDeliveryMaxRetries` is `internal` and fixed at 5 (`JobsOptions.cs:211-217`). That is five send
attempts and four waits of roughly 30s, 1m, 2m, and 4m (`BackoffSchedule.cs:15-42`), so the one-hour
ceiling in the curve is never reached and a row that cannot be delivered goes terminal in under ten
minutes of tick time. Because delivery runs on the one-minute tick, each wait rounds up to the next
tick boundary.

Delivery is at least once: a crash after send but before settlement can resend a duplicate
(`AlertsJob.cs:10-18`).

Two behaviours to know before you rely on the delivery status:

- **A reopened alert does not have its delivery state reset.** The `ON CONFLICT … DO UPDATE` arm
  clears `resolved_at_utc` and bumps `version` but never touches `delivery_status_code`,
  `retry_count`, or `retry_after_utc` (`RaiseJobAlert.routine.sql:122-135`), while delivery selects
  only `Pending` and `RetryAfter` (`GetDeliverableAlerts.sql:20`). An alert that already reached
  `Delivered`, `Failed`, or `Suppressed` and then re-fires inside the same window is therefore
  unresolved and unselectable: it shows as an open incident and sends nothing. This is a known defect
  with a fix planned for the 1.0.0-rc.1 alerts track ("reopen leaves alerts undeliverable"); do not
  treat a re-fire inside a window as a notification.
- **Resolution does not cancel a queued delivery.** The due-row query filters on delivery status
  only, with no `resolved_at_utc` predicate (`GetDeliverableAlerts.sql:18-23`), so an alert resolved
  between projection and the deliver phase is still sent.

## Channels and routing

A channel is **process startup configuration**, declared on the worker builder and resolved from an
in-memory registry at delivery time (`WorkerBuilder.cs:41-57`, `AlertChannelRegistry.cs:6-53`):

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
  (a URL, ARN, or address) and only checked non-empty (`WorkerBuilder.cs:43-48`). Re-declaring a name
  replaces the earlier declaration rather than throwing (`WorkerBuilder.cs:53-55`).
- **Every worker namespace gets an implicit `default` channel** on the log transport, `Active`, with
  `MinSeverity = Info` (`AlertChannelRegistry.cs:44-53`). Declaring `default` yourself overrides it.
  This is why failures reach the logs out of the box.
- **`MinSeverity` and `Status` are the two per-channel policies** (`AlertChannelOptions.cs:13,18`).
  `Disabled` and `Deprecated` both suppress; the difference is what the log line says
  (`AlertsJob.cs:369-395`).
- **Built-in transport kinds are `log` and `slack-webhook`** (`AlertTransportKinds.cs:11,14`). A
  custom transport defines its own kebab kind and registers an `IAlertTransport`.
- **Acta SQL stores the channel *name* only.** Endpoints, webhook URLs, credentials, and routing keys
  are never persisted: the delivery target is assembled at send time from the registry declaration
  (`AlertsJob.cs:316-322`). Keep real endpoints in configuration or a secret store.

Route a job with `[Job(AlertChannelName = "ops-slack")]` (`JobAttribute.cs:124-127`); a job that
declares none routes to `default`. `RunbookUrl` on the same attribute (`JobAttribute.cs:129-132`) is
resolved at delivery time from the definition and carried on the notification
(`GetDeliverableAlerts.sql:8`, `AlertsJob.cs:304-315`); it is not on the list-query projection.

`AlertChannelValidationMode` checks at worker startup that every alerting definition routes to a
channel configured in its namespace: `Off` skips, `Warn` (default) logs each unroutable definition,
`Fail` throws out of worker initialization (`JobsOptions.cs:233-239`, `AlertRoutingCheck.cs:29-67`).
Disabled and deprecated channels count as configured — that is a delivery decision, not a routing
one. The check covers **definitions only**: a definition with `AlertProfile = None` is skipped
(`AlertRoutingCheck.cs:39-42`), and a `channelName` passed to `ctx.AlertAsync` is not validated at
startup at all. A manual alert naming a channel the namespace does not configure settles `Failed`
with no retry on its first delivery pass.

## Operator surface

Alert rows are queryable like everything else. `IActaOperations.Alerts` offers `ListAsync` (filtered
by namespace, job, resolution, acknowledgement, severity, delivery status, and tags), `GetAsync`,
`AcknowledgeAsync`, and `ResolveAsync` (`IAlerts.cs:7-51`, `ListAlertsQuery.cs:17-28`). The dashboard
and HTTP API expose the two verbs at `POST /alerts/{alertRef}/acknowledge|resolve`, 200 or 404 and
never 409, behind `EnableControls` and the `X-Acta-Control` header
(`AlertControlEndpoints.cs:13-31,79-87`). In SQL, `acta.alerts_view` renders the coded columns and
aliases `dedupe_key` to `deduplication_key` (`AlertsView.view.sql:1-32`); see
[SQL recipes](./sql-recipes.md) for ready queries.

Acknowledge means an operator has seen the alert; resolve means the incident is considered settled.
Neither changes the job.

Retention is separate from the job's. `sys.retention` deletes alerts older than `AlertRetention`
(default 90 days, `JobsOptions.cs:23-29`) **only when delivery has settled** — `Suppressed`,
`Delivered`, or `Failed` (`PurgeExpiredData.routine.sql:83-104`). A row stuck `Pending` or
`RetryAfter` is never aged out. Because an alert keeps its own copy of the job ref, it can outlive
the job it was raised for; a manual `PurgeAsync` on the job deletes its alerts immediately. See
[Operator guide § retention and purge](./operator-guide.md).

## Where the principle breaks

The intended rule is that one profile means one thing for every job and the alert lifecycle is
independent of how the job was started. Two divergences remain, and both trace to a single root
cause: **a recurring slot has no terminal state.** `MaxAttempts` is the one-shot retry budget, and a
recurring slot re-arms instead of terminalizing however many consecutive runs throw
(`JobExecution.cs:723-727,759`). That is deliberate — a nightly job must not die permanently after
three bad nights — but the alerting consequences are real.

### 1. `OnTerminal` and `Info` are silent for a recurring job's ordinary failures

Both wait for the terminal transition, and a rollover is not one, so a recurring job that throws or
loses its lease alerts nothing under either profile. Recorded in
[Known limitations § execution model](../technical/known-limitations.md); the recommendation there —
choose `OnFailure` for recurring work you want to hear about — is the right one.

They do still fire on the terminal `Failed` shapes a recurring slot can reach, all three of which
route through the handler-status completion path and stop the whole slot
(`JobExecution.cs:489-509`):

| Shape | Reason code | Where |
| --- | --- | --- |
| Handler declares failure with `ctx.FailAsync` | `job.handler-failed` (52) | `JobExecution.cs:250-257` |
| Handler throws `NotImplementedException` / `NotSupportedException` | `job.non-retryable-exception` (23) | `JobExecution.cs:359-368` |
| An `AtMostOnce()` step was re-entered before its outcome was recorded, uncaught | `job.step-interrupted` (63) | `JobExecution.cs:341-351` |

A whole-job deadline is **not** one of them, despite what the `on-terminal` `[Code]` description
(`AlertProfileCode.cs:21-24`) and [Known limitations](../technical/known-limitations.md) currently
say: the deadline lands the job `Cancelled`, not `Failed` (`JobExecution.cs:407-415,447-457`,
`JobEventReasonCode.cs:45-49`), and the alertable set has no `Cancelled` branch
(`GetAlertableEvents.sql:19-23`). No profile alerts on a deadline.

### 2. Alert volume is bounded for one-shot work and unbounded for recurring work

A one-shot job fails at most `MaxAttempts` times and then terminalizes, so its alerting ends of its
own accord. A permanently broken recurring slot keeps producing a fresh `FirstFailure` /
`ThresholdReached` pair every dedupe window, forever, until it succeeds or an operator intervenes.
Same profile, same configuration, bounded outcome in one case and unbounded in the other. The single
lever is `AlertDedupeWindow`, which sets the rate of the repeat and not whether there is one.

Neither of these is a feature. They are the current behaviour, and they are the two places where
reading the profile name does not tell you what the job will do.

## Choosing a profile

**One-shot work.** `OnFailure` (the default) is the honest choice. You get a `FirstFailure` warning
on the first retryable failure, an `Error` escalation if the same reason recurs to the threshold
inside one window, and a `FinalFailure` when the budget is exhausted — at most three rows, and the
sequence ends by itself. Pick `OnTerminal` when the intermediate retries are noise and only the final
outcome should page; it costs you the early warning, and for a one-shot job it costs nothing else.
`Info` is `OnTerminal` at a severity most channels will not page on — useful for work whose failure is
a note rather than an incident, but check the routed channel's `MinSeverity` first, because
`Info` alerts are exactly what a `Warning` floor suppresses.

**Recurring work.** `OnFailure`, or you will hear nothing. `OnTerminal` and `Info` are silent for the
rollover case, which is how a recurring job fails nearly all the time. Accept that the alerting is
unbounded in the sense described above: budget for at most two notifications per dedupe window per
broken slot — six a day on the four-hour default — and treat "this alert is still open in the next
window" as the signal that nobody has fixed it. Check the cadence against the escalation cutoff
before you rely on `ThresholdReached`: at a slower cadence than window ÷ `AlertFailureThreshold`, the
only alert a broken recurring job will ever raise is `FirstFailure`. If you want a recurring job to
stop rather than keep failing, that is a handler decision — `ctx.FailAsync` — not a profile one.

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
| `AlertWindow.cs` | `src/Acta.Runtime/Modules/Alerting/AlertWindow.cs` |
| `AlertChannelDecision.cs` | `src/Acta.Runtime/Modules/Alerting/AlertChannelDecision.cs` |
| `AlertChannelRegistry.cs` | `src/Acta.Runtime/Modules/Alerting/AlertChannelRegistry.cs` |
| `AlertRoutingCheck.cs` | `src/Acta.Runtime/Modules/Alerting/AlertRoutingCheck.cs` |
| `AlertStoreSink.cs` | `src/Acta.Runtime/Modules/Alerting/AlertStoreSink.cs` |
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
surfaces, [Operator guide](./operator-guide.md); for `AlertDedupeWindow`, `AlertFailureThreshold`,
and the rest of the option surface, [Configuration](./configuration.md); for startup wiring,
[Production § alert channel setup](./production.md).
