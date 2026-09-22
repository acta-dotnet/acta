# Ideas for after 1.0

These are possible directions for Acta after `1.0.0`, not promises for a roadmap. The aim is to add
useful things around Acta's durable-work model without slowly turning it into a message bus, a hosted
control plane, or a BPMN product.

## Declarative flows and checkpoint replay

One of the most interesting ideas is a small, code-first layer for building workflows from simple
records. A flow would be made from familiar building blocks such as a step, a delay, a signal wait, a
choice, parallel branches, a fan-out, or a child job. A fluent syntax would let people chain those
blocks together in code.

The fluent API would only be the pleasant way to create the flow. Underneath it, Acta would have a
plain, immutable description of the plan. That description is what makes the idea powerful. Acta
could validate it before running it, compare two versions of it, serialize it, test it, and export it
as Mermaid, DOT, or JSON. It could also use the same description to draw the flow in the dashboard.

This gives Acta a real replay mode, but it needs a precise name: **checkpoint replay**.

> Acta replays a versioned plan against durable checkpoints. After recovery, it starts evaluating the
> plan from the beginning. Completed nodes return their recorded outcomes, while unfinished nodes are
> allowed to run.

That is different from deterministic event-history replay. Acta would not rebuild arbitrary program
state from a log of past events. It would walk a declared plan again and use its checkpoints to decide
what has already happened. In simple terms, the versioned plan and the saved checkpoints tell Acta
what can run next.

The existing runtime already has most of the pieces. A flow step can use `RunStepAsync`; a delay can
use `SleepAsync`; a signal node can use `WaitSignalAsync`; and parallel work and fan-out can use
`ParallelAsync` and `MapAsync`. Child nodes can use the existing child-job operations. The flow layer
should compose those primitives instead of building a second scheduler inside Acta.

A few rules would make the model safe. Every node needs a stable, explicit name. If node identities
were based on their position, inserting one step near the beginning could make every later checkpoint
look like new work. A persisted plan also cannot contain captured delegates. A step needs to point to
a registered activity by durable name and carry serializable input, otherwise old plans could not be
inspected or executed reliably after a deployment.

Choices need special care. Once a branch has been selected, that decision must be checkpointed. Acta
must not ask the same question again after a crash and take another branch because some external data
has changed. Values passed between nodes should likewise come from saved step results or durable
variables, not from local state that disappears with the process.

Flows would be durable contracts, just like job names are today. An old flow definition should remain
registered while old instances can still resume. An incompatible edit should produce a new version,
for example `fulfil-order-v2`, rather than quietly changing what an existing instance means. Fan-out
and repetition should also have explicit bounds so a plan remains understandable and safe to run.

Checkpoint replay does not change Acta's side-effect boundary. It can remember that an Acta step
finished and avoid doing that work again, but an external payment, email, upload, or machine command
still needs idempotency, `AtMostOnce()` semantics, or a reconciliation path.

The first validator could catch duplicate node names, unreachable nodes, unbounded cycles, missing
activities, incompatible connections, ambiguous joins, and unbounded fan-out. Later, a compatibility
tool could compare two versions and explain which running instances would be affected by a change.

Diagram export would be useful on its own, but the better feature is a live diagram in the dashboard.
An operator could see that two steps have completed, eight of twelve fan-out branches are done, and
the flow is now waiting for an approval signal. Explain could say why a node is blocked and link to
the relevant job, worker, signal, or operator action.

This belongs in a new flows slice beside the existing modules: the runtime already organizes
itself into slices, and a flow layer that composes the existing primitives is one more slice, not
another product. If the word *workflow* is
used, the documentation should be clear that this is a code-first checkpoint model, not BPMN, a
visual designer, or deterministic event-history replay.

A sensible way to build it would be to start with the immutable plan and its validator. The fluent
builder and diagram exporters could come next, followed by the interpreter over `JobContext`. Once
those semantics are stable, Acta could add version comparison and finally the live dashboard view.

A short way to describe the feature might be: **Replay the plan, reuse the work.**

## Contract-evolution checks

Acta could add a CI and command-line tool that compares the current generated manifest with a
committed baseline or a deployed database. Instead of merely reporting that something changed, it
would explain whether the change is compatible, risky, or breaking. It could show how many queued or
waiting jobs still depend on the old contract and recommend keeping the old handler or introducing a
new job version.

This would fill the gap between the current definition-level drift guard and the real deployment
question: "Can the work already in this database still run after this release?" Declarative flows
would make the report even more useful because the tool could compare their node names, branches,
signals, delays, and data connections.

## More precise authorization and payload redaction

At the moment, access to the read surface effectively means access to every inline job input, result,
and checkpoint payload. That is simple, but it will be too broad for some production environments.

Acta could provide a host-supplied read policy alongside the existing control authorizer. The host
would still own authentication and user identities. The policy would only tell Acta whether a caller
may see a resource in full, see metadata without payloads, see a redacted payload, or not see the
resource at all. It could make that decision using the namespace, tenant, job, and kind of payload.
The typed operations API, HTTP API, and dashboard would all follow the same decision.

## Atomic batch operations

The deferred batch-control idea is worth bringing back as a real set-based operation. An operator
should be able to select a group of jobs and pause, resume, cancel, reschedule, or reprioritize them
without the implementation issuing hundreds of unrelated single-job calls.

The operation should support a dry run, a clear upper bound, optimistic concurrency, one actor and
reason, and a batch reference connecting its audit events. Its result should say how many jobs were
changed, rejected, conflicted, or no longer present. The same release could expose the existing batch
enqueue operation through HTTP, with sensible size limits and per-item outcomes.

## Results from every retained execution

Acta currently keeps result rows by execution number, but the public API and dashboard only expose
the latest result. `GetResultAsync` asks the store for the newest row, the job-detail response carries
one result, and the Result tab renders that one payload. The Executions tab shows the attempt history,
but it cannot open the result produced by a particular attempt.

A result-history viewer could connect those two parts of the job page. The Result tab would still
open on the latest value, but it could list every retained result with its execution number,
completion time, format, and size. Selecting one would open that payload and link back to the matching
execution and its events. For recurring jobs this would make it possible to inspect the outputs of
several recent occurrences instead of seeing only the newest one.

The API should expose a paged metadata list and fetch the selected payload separately. Returning every
result body inside the existing job-detail response would make one screen unexpectedly expensive.
The typed API could likewise gain a way to read a result by execution number while keeping the current
latest-result method as the convenient default.

The viewer can only show rows that are still retained. In particular, a recurring definition's
`RecurringResultCap` defaults to one, so operators would need to raise that value when they want a
deeper result history. The UI should state that limit rather than making an intentionally trimmed
history look like missing data. Existing payload size limits, format handling, and any future read
authorization rules should apply to every historical result in exactly the same way as they apply to
the latest one.

## Schedules created by operators

Acta already has most of the vocabulary for this idea. `ScheduleOriginCode.Operator` is reserved, and
the schedule table says that operator-origin schedules can be added and removed, but no current code
path creates one. Today an operator can pause, resume, trigger, and override a schedule that was
declared with `[JobSchedule]`; the operator cannot create a new schedule from the dashboard or API.

An operator-created schedule would attach a new named cadence to a registered job definition. The
creation screen could accept a name, cron or interval expression, time zone, misfire strategy,
description, and reason. Before saving, it should preview the next occurrences using the same parser
and clock rules as the real scheduler. Once created, it should appear beside code-declared schedules,
clearly marked as operator-owned.

Ownership should remain easy to understand. A code-declared schedule can be paused or overridden by
an operator, but its declaration still comes from code. An operator-created schedule can be edited or
removed by an operator and must survive ordinary catalog synchronization and application restarts.
Removing it should write a durable audit event. If it was the last live schedule on the recurring
slot, the slot should become visibly exhausted or paused rather than silently disappearing with its
history.

The main design question is the recurring slot's input. Several schedules for one definition share
one stable slot job and therefore share its stored input. The smallest first version could allow an
operator schedule only when that definition already has a recurring slot. A fuller version could
also create the slot for an unscheduled definition when the handler has no input, has a generated
default input, or the operator supplies a valid initial payload. The dashboard must explain that the
input belongs to the shared slot, not independently to each schedule.

Creation, update, and removal should use optimistic concurrency and the existing control
authorization and confirmation rules. Name collisions between operator-owned and code-declared
schedules must be rejected rather than resolved by precedence. The event ledger should record who
created, changed, or removed the schedule and why.

## An accessible and responsive dashboard

The dashboard should work well for operators regardless of how they navigate it or how large their
screen is. Accessibility and responsiveness should be treated as part of every dashboard feature,
not as a polish pass after the screens are finished.

The accessibility target should be WCAG 2.2 AA. Every page and control should be usable from the
keyboard, with a logical tab order, visible focus, skip links, semantic headings, and clear labels.
Dialogs need reliable focus trapping and restoration. Status, severity, and job outcomes must never
be communicated by colour alone. Live updates should not unexpectedly move focus or cause a screen
reader to announce a constantly changing page.

Tables, timelines, charts, and flow diagrams need accessible alternatives. Charts should include
exact values, useful labels, and an equivalent table or list. The flow diagram should expose the
same nodes, connections, and runtime states as structured text. Tooltips must also work through focus
and touch rather than requiring a mouse hover. The interface should respect reduced-motion and
high-contrast preferences and remain usable when text is enlarged or the page is zoomed substantially.

Responsive design should support phones and tablets as well as wide operator screens. On narrow
screens, dense tables can keep the primary identity and status visible while moving secondary fields
into an expandable row or detail sheet. Filters can become a drawer, navigation can collapse without
hiding the current location, and actions need touch-friendly targets. Important confirmations should
remain deliberate on mobile, especially for cancel, restart, purge, and batch operations.

Responsiveness also means handling content rather than only viewport width. Long job names, actor
keys, error messages, translated labels, and large counts should wrap or truncate predictably without
hiding the underlying value. The dashboard should remain understandable at 200% and 400% zoom and
should avoid horizontal scrolling at the page level; a genuinely wide data table can have its own
contained scroll area.

This work needs repeatable checks. Automated accessibility scans, keyboard-driven browser tests,
responsive viewport tests, and visual contrast checks can catch common regressions in CI. Periodic
manual testing with screen readers, zoom, forced colours, reduced motion, and touch devices is still
necessary because automation cannot judge whether an operator workflow actually makes sense.

## Keep the dashboard build small

The dashboard is embedded in `Acta.AspNetCore`, so its size affects more than the first page load. It
also contributes to the assembly and NuGet package carried by every application that references the
dashboard. The production build should therefore have an explicit goal of staying small rather than
being allowed to grow unnoticed as new screens, charts, and flow diagrams arrive.

Vite already minifies the production build, but the current output is mostly one JavaScript file.
Larger features can be loaded only when their route or panel is opened. A definition chart, flow
renderer, payload editor, or historical result viewer does not need to be part of the initial
Overview download. Tree shaking, small dependency choices, and route-level splitting should keep the
common path light while still allowing richer operator screens.

Network compression and package size need to be measured separately. Brotli or gzip can make assets
much smaller over HTTP, but embedding both compressed and uncompressed copies can make the shipped
assembly larger. Acta should either cooperate cleanly with ASP.NET Core response compression or serve
carefully chosen pre-compressed assets with the correct content encoding, cache, and `Vary` headers.
It should not keep several copies merely to make one size number look better.

The dashboard should remain self-contained. It should not move scripts, fonts, icons, or chart code
to a public CDN just to reduce the package measurement. Local operation, strict security headers,
and the ability to use the dashboard without internet access are more important than an artificially
small embedded bundle.

A CI size budget could report the raw, Brotli, and gzip sizes of each asset, the total embedded
dashboard contribution, and the JavaScript needed for the first route. It should fail on a meaningful
regression unless the increase is reviewed. Source maps and test-only code should never enter the
package, and long-lived hashed assets should keep their existing immutable caching behavior.

The size target must not work against accessibility. Semantic markup, keyboard behavior, readable
labels, reduced-motion support, and accessible chart alternatives are product behavior, not optional
bytes to remove. The goal is a minimal implementation of the complete dashboard, not a smaller but
less usable dashboard.

## An interactive view of the job pipeline

The dashboard could show how many jobs are currently moving through the pipeline in one compact,
interactive view. A pie or doughnut chart could divide the live population into Ready, Suspended,
Paused, Dispatched, and Executing jobs, with the total number of non-terminal jobs in the middle.

Every section should lead somewhere useful. Clicking the Ready section would open the jobs page with
the Ready filter already applied. Clicking Suspended would show the jobs waiting on signals, children,
or timers. Dispatched and Executing would open the corresponding in-flight jobs so an operator can
see their workers and leases. The chart should behave as navigation into the existing job list, not
as a decorative summary.

A second breakdown could show which job definitions make up the pipeline. Selecting a definition
would open its definition page, where the operator could see its configuration and performance
history, with another action to open all live jobs for that definition. When there are many
definitions, the chart should show the largest contributors and group the remainder under "Other"
rather than producing dozens of unreadable slices.

The view should follow the dashboard's current namespace and tenant filters, and those filters should
carry through when the user drills into a slice. It would also be useful to split Ready jobs into
work that is due now and work deliberately scheduled for later. Otherwise a healthy collection of
delayed jobs can look like a backlog that workers have failed to drain.

Recurring slots and `sys.*` jobs need explicit treatment because a small number of permanent system
or recurring rows can distort the meaning of the pipeline. They could be excluded by default with a
visible toggle, or shown as their own category. Whatever rule is chosen should be stated next to the
chart so the total is understandable.

The graphical view should have an equivalent count table and should not rely on colour alone. That
makes the information accessible, gives exact values on small screens, and still leaves the chart as
the fast visual entry point.

The first version can be a current snapshot read from the jobs and runtimes tables. A later version
could add a pipeline-depth graph over time, but that is a different feature: it would need to derive
historical counts from events or maintain disposable rollups rather than pretending the current rows
contain history.

## Performance history for every job definition

Each job definition could have a performance view calculated directly from its retained event
history. The `job.execution-finished` events already carry the definition, outcome, timestamp, and
attempt duration, so Acta can show useful numbers without adding another telemetry store or changing
the execution path.

The definition page could show p50, p95, and p99 execution time over a chosen window, together with a
histogram and a graph of those percentiles over time. Seeing the trend matters as much as seeing the
latest value: an operator should be able to notice that the median stayed flat while the slowest one
percent became much worse after a deployment. The view could also show the number of attempts and the
failure rate so that a percentile based on twelve runs is not mistaken for one based on twelve
million.

The first version should be very clear about what it measures. Handler execution time, time waiting
to be claimed, and total time from enqueue to completion answer different questions and should not be
mixed into one number. Execution time is the simplest honest starting point because `duration_ms` is
already the canonical per-attempt measurement. Successes and failures should be filterable and shown
separately, since a job that fails quickly can otherwise make a definition look deceptively fast.
Retries count as separate attempts; recurring jobs naturally contribute one measurement for each
finished execution.

Useful filters would include time window, outcome, tenant, worker deployment version, and execution
profile. A marker for deployment versions on the trend graph would make performance regressions much
easier to spot. Common windows such as the last hour, day, week, and month are probably enough at
first, with the available range limited by event retention.

The dashboard also needs to state when its sample is incomplete. The default `Audit` level records
every finished execution, but quieter audit levels suppress some or all of those events. In that
case, Acta should say that the numbers cover failures only or that no complete distribution is
available, rather than presenting a misleading percentile.

These queries should initially be bounded and run against the event ledger on demand. If very large
installations make that too expensive, Acta could later add disposable rollups or caching, while
keeping the event history as the source of truth. This would complement live OpenTelemetry metrics:
metrics are best for alerting now, while the durable event view explains how a particular job
definition has behaved across deployments and over its retained history.

## Distributed tracing

Acta already has metrics and a correlation field, so distributed tracing would complete its
observability story. It could emit activities for enqueue, outbox relay, claims, execution attempts,
durable steps, child jobs, fan-out branches, recovery, and important database operations. The host
would choose and configure the OpenTelemetry exporter.

A delay or signal wait might last for days, so it should not create one enormous open span. Each
execution attempt should be represented separately, with links back to the enqueue, the parent job,
and any earlier attempt. That would give operators an honest trace while still showing the complete
relationship between the pieces of work. A live flow diagram could then link directly to the relevant
external traces.

## Alerting under Failures

Under `AuditLevel.Failures` the completion writes an `events` row only when the attempt failed, and
the alert projector closes an incident only from a success event, so an incident opened under
`Failures` never closes. `sys.alerts` itself runs under `Failures` with the `SysCritical` profile, so
even the projector's own incident is unresolvable. The rc.3 experimentation branch fixed this by
reading recovery from the runtime row, which cost two columns, an index, and three resolve routines;
that was withdrawn. The design that costs no DDL:

- A failed attempt is defined explicitly in `complete_execution`: not succeeded, not a handler
  cancel or pause, and either terminal or a re-arm whose reason is an unhandled exception, a lost
  lease, or an execution timeout. That records every retryable failure and excludes sleep, signal
  waits, deliberate reschedules, and concurrency and rate denials, none of which is a failure.
- A success writes its event when the job's newest `job.execution-finished` row is a failure, read
  with one ordered seek on `ix_events_job_timeline` before the runtime row is updated. That fact is
  durable, survives an operator restart (which writes nothing under `Failures`), and does not depend
  on how far the projector has got. An open-incident probe was considered and rejected: fail, restart,
  succeed before the projector runs would find no incident yet, write nothing, and the incident
  opened later would never close. `execution_number` was rejected because it counts claims, so a
  healthy recurring job would write a success on every occurrence after its first. `failure_count`
  alone was rejected because restart zeroes it. The batch completion routine sends a `Failures` job
  whose newest event is a failure through the per-job path.
- `cancel_job` writes `job.cancelled` under `Failures` as well as `Audit`, for an executing and for
  a parked job; the projector's read selects cancellations and resolves through the existing
  `resolve_job_alerts`, so a cancelled job stops paging without an execution-finished row.
- `JobsOptionsValidator` requires `AlertRetention <= JobEventsRetention`, so the evidence outlives the
  incident it would close.
- Startup warns for `Off` paired with an alerting profile (nothing per job is recorded, so nothing
  can alert) and for a scheduled definition with `OnTerminal` or `Info` (silent for a slot's ordinary
  failures).

Facts to hold: a success on the first claim writes nothing; a healthy recurring job writes nothing
over three occurrences; sleep, signal wait, deliberate reschedule, concurrency denial, and rate
denial each write nothing; a retryable failure writes; fail, retry, fail, succeed resolves; restart
alone does not resolve and the restarted success does; fail, restart, succeed with no projection
pass in between, the success event present before any projection, both events aged behind the
horizon, ends raised and resolved after one pass; handler cancellation of an executing job and
operator cancellation of a parked job each close the incident; `sys.alerts`' own incident resolves.
The gate is the three provider conformance suites, not the fast gate.

## Rolling deploys

Landed after rc.3 as three rules and no schema change: registration never retires a definition
absent from a manifest; a worker that bounces a definition it has no handler for excludes it from
its claims for the rest of its process life, in the candidate scan and in the idle horizon; retiring
a definition and cancelling its queue is an operator verb. `docs/guide/production.md` "Rolling
deploys" is the home. Pools stayed a placement feature, below: two builds sharing a pool still bounce
each other's unknown jobs, so pools never answered this question.

## Pools: dedicated worker capacity inside a namespace

Pools are worth building if you want one application's jobs to run on different hardware or have
independently reserved worker capacity. That is their strongest justification.

Imagine a video service with two kinds of work:

- Short jobs: publishing, notifications, metadata updates.
- Expensive jobs: encoding videos, taking minutes and requiring GPUs.

With one shared worker fleet, a backlog of encodes can occupy every executor. Raising a
notification's priority does not interrupt encodes already running. Pools let you reserve separate
capacity:

| Deployment | Namespace | Served pool | Capacity |
| --- | --- | --- | --- |
| General workers | media | default | 24 concurrent jobs per worker |
| GPU workers | media | gpu | 2 concurrent jobs per worker |

A million queued encodes cannot consume the general workers' executor slots. Both fleets still
share database capacity, so this is worker isolation, not complete infrastructure isolation.

Here is how the proposed API could look. `Job.Pool` and `JobsOptions.Pools` are proposals; they do
not exist today. The child-job calls follow Acta's existing API; `IVideoEncoder` is your
application service.

```csharp
public sealed record PublishVideo(string SourceUrl);
public sealed record EncodeVideo(string SourceUrl);
public sealed record EncodedVideo(string StreamUrl);

public sealed class PublishingJobs
{
    // No Pool specified -> default.
    [Job("publish-video")]
    public async Task<EncodedVideo> Publish(
        PublishVideo input,
        JobContext context,
        CancellationToken ct)
    {
        var result =
            await context.ExecuteChildAsync<EncodeVideo, EncodedVideo>(
                "encode",
                new EncodeVideo(input.SourceUrl),
                ct);

        return result.ValueOrThrow();
    }
}

public sealed class EncodingJobs(IVideoEncoder encoder)
{
    [Job("encode-video", Pool = "gpu")]
    public Task<EncodedVideo> Encode(
        EncodeVideo input,
        CancellationToken ct)
        => encoder.EncodeAsync(input, ct);
}
```

The publisher starts on a general worker. It creates the encoding child and waits durably, freeing
its executor while the child runs. A GPU worker executes the child. When it finishes, the publisher
resumes on a general worker.

The handler specifies where the work belongs; deployment configuration specifies which work a
process accepts.

Assuming `MediaJobs` is the generated manifest containing these handlers, the same application
image could run in both deployments:

```csharp
var builder = Host.CreateApplicationBuilder(args);
var gpuWorker = builder.Configuration["WorkerRole"] == "gpu";

builder.Services.UseActa(j =>
{
    j.UsePostgres(p =>
        p.ConnectionString =
            builder.Configuration["ConnectionStrings:Acta"]!);

    j.ConfigureOptions(o =>
    {
        // Proposed subscription API.
        o.Pools = gpuWorker ? ["gpu"] : ["default"];

        // Existing per-worker concurrency setting.
        o.MaxConcurrentExecutors = gpuWorker ? 2 : 24;
    });

    // Same namespace and complete manifest in both deployments.
    j.Run<MediaJobs>("media");
});

await builder.Build().RunAsync();
```

Set `WorkerRole=gpu` on GPU machines and `WorkerRole=general` on general machines. Adding another
GPU machine increases encoding capacity without changing producers, job names, or the namespace.

Both deployments register the same complete catalog; their pool subscriptions decide what they
claim. Under the proposed design, system jobs live in `default`, so the general fleet also keeps
recovery, alerting, and retention running. Explicit subscriptions matter here: deriving
subscriptions from a complete handler catalog would let both fleets serve both pools.

Underneath, the mechanism is straightforward:

1. Registration creates or finds each namespace's named pools and records the pool assigned to
   each definition.
2. Enqueue copies that definition's pool ID into the runtime row.
3. Workers claim only rows belonging to their served pools within their namespace.

A GPU job should remain queued when GPU capacity disappears. Automatically sending it to a general
worker would defeat the placement rule.

There is an important correction to the earlier argument: Acta already supports cross-namespace
child jobs, demonstrated in its cross-namespace example
(`concepts/700-topology-and-deployment/704-cross-namespace-child/cross-namespace-child.cs`). You
can build this workflow today using separate namespaces. Pools make the separation cleaner: `media`
remains the application boundary, while `default` and `gpu` describe execution placement.
Namespace-scoped settings and keys can remain shared.

The features answer different questions:

| Feature | Question it answers |
| --- | --- |
| Namespace | Which application boundary owns this job? |
| Pool | Which worker fleet may execute it? |
| Concurrency limit | How many matching jobs may run together? |
| Rate limit | How frequently may matching jobs start? |
| Priority | Which eligible job should start next? |

For me, the most compelling everyday example is actually bulk imports versus interactive work. Put
imports on a separately sized fleet so an overnight backlog cannot occupy the executors needed for
password emails and short user-triggered jobs. GPUs simply make the placement requirement obvious.

That makes pools a worthwhile product capability. It does not automatically make them necessary
for rc.3. Build them when dedicated capacity or hardware placement is a concrete target use case.
Basic rolling deployments alone do not justify them.

### The shape worth building

Pools are per namespace, identified by an `int`, and the claim filters by pool id instead of by
namespace, so the hot index swaps one key for another at no extra cost. A global pool catalog was
considered and rejected: it creates one identity for a routing name across every namespace, and the
claim would then need both the namespace and the pool.

DDL, against the rc.3 baseline:

- new table `pools (id int identity, namespace_id int NOT NULL, name varchar(64) NOT NULL,
  created_at_utc)`, unique on `(namespace_id, name)`; a `default` row is created with every
  `namespaces` row;
- `definitions.pool_id int NOT NULL`, foreign key to `pools`;
- `runtimes.pool_id int NOT NULL`, denormalized at enqueue like `namespace_id`, which stays for the
  retention, in-flight, and overview paths;
- `ix_runtimes_claim_ready` becomes `(pool_id, priority_code DESC, next_run_at_utc, job_id,
  status_code) WHERE status_code IN (10, 20)`: `namespace_id` leaves the key and `pool_id` takes
  its place, same width and seek shape;
- SQL Server: a `pool_id_batch (pool_id int NOT NULL)` table type and a `pool_name varchar(64) NOT
  NULL` column on `job_definition_batch`.

Rules:

- An omitted `[Job(Pool = ...)]` means the namespace's `default`; system jobs live in `default`.
- An unset `JobsOptions.Pools` means `default` only. The catalog is complete in every deployment, so
  deriving the served set from handlers would let every fleet serve every pool. An explicit list
  selects exactly those names and never adds `default` implicitly; a dedicated fleet that omits it
  gives up the system jobs, which production.md must say, and every active namespace therefore
  needs default-serving workers (a topology test of named-pool workers plus default-serving workers
  proves recovery, alerting, and retention keep running).
- A pool id implies its namespace, so `claim_batch`, `claim_one`, and the idle next-ready lookup
  filter by served pool ids alone (an array on PostgreSQL, a table type on SQL Server, a list on
  SQLite) and a worker can never reach another namespace's rows.
- Registration upserts the worker's pool names inside its namespace, points each definition at its
  id, and relabels that definition's runtime rows when the id changed, in-flight rows included: the
  pool id is in no lease invariant, so the lease, the execution number, and a buffered claim stay
  valid. The manifest-generation guard keeps a rollback from overwriting newer routing. Running
  workers re-resolve their explicit pool names to ids on the definition-policy reload tick, so an
  older worker follows a newer deployment's rename.
- Several served pools keep the global `priority DESC, next_run_at_utc ASC, job_id` order with a
  bounded sort: one ordered top-N seek per served pool (`LATERAL` on PostgreSQL, `CROSS APPLY` on
  SQL Server, a union of per-pool limits on SQLite) merged by a final ORDER BY over at most
  `pools x limit` rows. Locks are taken inside each per-pool seek (`FOR UPDATE SKIP LOCKED`,
  `READPAST, UPDLOCK`), so a claim may lock up to `pools x limit` candidates for its own short
  transaction and claims at most `limit` of them; that over-locking is bounded and preferred to an
  unlocked merge, which can underfill a batch while eligible rows sit below the skipped ones. One
  served pool is a single index scan with no sort. Priority behaviour is never weakened for a plan.
- No enqueue-time pool override and no auto pools in the first cut.

Tests: competing claimers with locked rows at every pool's front (no duplicate claims, progress past
locked candidates, locked-versus-claimed counts); a pool change while an older worker runs, then
removal of the newer worker, then a restarted rollback worker; enqueue racing a relabel; a pool
nobody serves is a startup warning and an overview column; EXPLAIN for one pool is an index scan
with a limit, for three pools one sort over at most three times the limit.

## Possible order

Declarative flows and the contract checker fit together and feel like the strongest first direction.
The flow representation creates something concrete that can be validated, compared, replayed, and
drawn, while the contract checker makes it safer to evolve.

Read authorization would remove an adoption barrier for teams with sensitive payloads. Dashboard
accessibility, responsiveness, and a bundle-size budget should be established before adding richer
visualizations. Result history, operator-created schedules, batch operations, the interactive
pipeline view, definition performance history, and tracing would then deepen the operator story
without changing the execution model.
