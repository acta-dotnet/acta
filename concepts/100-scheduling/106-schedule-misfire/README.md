<!-- engineering-lab
lab: can-recurring-jobs-run-forever-on-one-row
views: schedules_view, jobs_view, events_view
alternatives: skip-missed, catch-up-once, catch-up-all, materialize-every-occurrence
-->

# Engineering Lab: a missed schedule is a business decision

## The problem

After downtime or a pause, a schedule cursor points into the past. "Run everything missed," "run once now,"
and "skip ahead" have different business meanings and operational costs.

## Common approaches

- Skip missed instants and move to the next future occurrence.
- Catch up once, coalescing the outage into one recovery execution.
- Run every missed occurrence, in order, as its own execution.
- Materialize every occurrence as independently tracked work.

## Why this design

Each schedule names its policy, and `Skip` is the default when it names none. A resume, like a worker
start, decides nothing: it keeps the cursor where it was, and the next admission pass applies the policy
to the occurrences it finds already past. An occurrence less than a minute late still counts as on time.

- `Skip` skips the missed occurrences, except the latest when it is within that minute.
- `CatchUpOnce` runs one firing whose occurrence is the earliest missed and whose covered-through is the
  latest, so the handler knows the stretch it stands for.
- `CatchUpLatest` runs the latest however late it is and skips the earlier ones.
- `CatchUpAll` runs every missed occurrence in order, one firing per settlement.

`[Schedule(ExpireAfter = "1h", ExpireAction = ...)]` bounds how old an occurrence may be before any of these
apply: older ones are skipped with the reason `expired`, and `Pause` also pauses the schedule.

## Trade-offs

`Skip` loses missed work. `CatchUpOnce` keeps the stretch but not one execution per occurrence.
`CatchUpAll` keeps every occurrence but runs them one after another on one durable slot, so a long outage
becomes a long backlog; with `ScheduledDelivery = PerOccurrence` each one is a root job of its own.

## Run the experiment

```bash
dotnet run --project concepts/100-scheduling/106-schedule-misfire
```

The program pauses three schedules that fire every second, lets about three occurrences pass, resumes
them, and waits until each policy has decided the stretch. It prints how many firings each ran for the
missed occurrences: `skip-report` at most one, `catch-up-once-report` one covering the stretch, and
`catch-up-all-report` one per missed occurrence, in order.

## Rows to inspect

Run with `--all-columns` to execute the visible `SELECT *` Explore query first. The default Notice
queries select the fields that prove the lesson, and the text below explains their meaning.

`schedules_view` shows the three cursors in the past while the schedules are paused. After the resume,
`events_view` shows each policy's first decision: a `schedule.skipped` range for `skip-report` beside its
admission of the latest, one `schedule.admitted` for `catch-up-once-report` whose details carry the
covered-through instant, and the first of `catch-up-all-report`'s admissions.

## Break it

Pause these one-second schedules for as long as you like: `Skip` still runs the latest occurrence, since
one fell due less than a second before the resume and the grace is a minute. Give a schedule occurrences
more than a minute apart and resume it more than a minute after the one it missed, and `Skip` runs none of
them, while `CatchUpLatest` would still run that latest one.

## When not to use

Use `Skip` for cache refresh, telemetry, and cleanup. Use `CatchUpOnce` for reconciliation or summary work
where one recovery pass matters. Use `CatchUpAll`, or a per-occurrence definition, for accounting or
financial occurrences that must never coalesce.

## Source trail

- [The related Engineering Lab](../../../docs/engineering-labs.md)
- [`schedule-misfire.cs`](./schedule-misfire.cs)
- [`ScheduleEvaluator.cs`](../../../src/Acta/Domain/Schedules/ScheduleEvaluator.cs)
