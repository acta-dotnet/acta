<!-- engineering-lab
lab: can-recurring-jobs-run-forever-on-one-row
views: jobs_view, schedules_view, events_view
alternatives: row-per-occurrence, reusable-slot, external-scheduler, one-shot-jobs
-->

# Engineering Lab: one recurring slot, moving cursors

## The problem

Row-per-occurrence schedulers make every tick a new durable identity. That preserves independent
history, but it also creates unbounded job rows and more duplicate-fire edges.

## Common approaches

- Insert one job for every occurrence.
- Keep a reusable slot and advance schedule cursors.
- Let an external scheduler enqueue one-shot work.
- Use an in-process timer and accept process-local availability.

## Why this design

Acta stores one reusable job identity and one cursor per schedule. Due schedules re-arm the same slot;
coincident schedules can coalesce into one execution, whose `TriggeringScheduleNames` says what fired.

## Trade-offs

Occurrence history lives in the event/result ledger rather than in a new job identity. Each schedule
still needs its own cursor, and coalescing is intentionally not a record of every missed business event.

## Run the experiment

```bash
dotnet run --project concepts/100-scheduling/103-multiple-schedules
```

The twelve-second run lets the five- and ten-second cursors move before printing the proof.

## Rows to inspect

Run with `--all-columns` to execute the visible `SELECT *` Explore query first. The default Notice
queries select the fields that prove the lesson, and the text below explains their meaning.

Expect one `jobs_view` row, two `schedules_view` rows, and several `events_view` executions. The view
queries are operator/learning surfaces; code should normally use `IJobs` and `IJobSchedules`.

## Break it

Stop the process for longer than one interval, restart it, and inspect how the configured misfire policy
advances. Lab [`106-schedule-misfire`](../106-schedule-misfire/) isolates the `Skip` versus
`FireOnceCatchUp` decision.

## When not to use

Use one-shot jobs when each financial/accounting occurrence needs an independent identity, retention
period, deduplication key, or audit outcome. Use an external scheduler when scheduling authority must sit
outside the Acta database.

## Source trail

- [The related Engineering Lab](../../../docs/engineering-labs.md)
- [`multiple-schedules.cs`](./multiple-schedules.cs)
- [`ScheduleWalker.cs`](../../../src/Acta/Features/Schedules/ScheduleWalker.cs)
- [`SchedulesView.view.sql`](../../../src/Acta.Sqlite/Features/Schedules/Sql/SchedulesView.view.sql)
