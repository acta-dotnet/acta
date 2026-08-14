<!-- engineering-lab
lab: can-recurring-jobs-run-forever-on-one-row
views: schedules_view, jobs_view, events_view
alternatives: skip-missed, catch-up-once, materialize-every-occurrence
-->

# Engineering Lab: a missed schedule is a business decision

## The problem

After downtime, a schedule cursor can point into the past. “Run everything missed,” “run once now,” and
“skip ahead” have different business meanings and operational costs.

## Common approaches

- Skip missed instants and move to the next future occurrence.
- Catch up once, coalescing the outage into one recovery execution.
- Materialize every occurrence as independently tracked work.

## Why this design

Acta makes `Skip` versus `CatchUpOnce` explicit per schedule. Resume reconciles the stored cursor by
that policy rather than hiding an accidental default in a worker loop.

## Trade-offs

`Skip` loses missed work; catch-up-once loses the count and identity of individual missed occurrences.
Neither policy models “every interval is a liability.” That needs one-shot occurrence jobs.

## Run the experiment

```bash
dotnet run --project concepts/100-scheduling/106-schedule-misfire
```

## Rows to inspect

Run with `--all-columns` to execute the visible `SELECT *` Explore query first. The default Notice
queries select the fields that prove the lesson, and the text below explains their meaning.

The program prints `schedules_view` before the miss, while both cursors are overdue, and after resume.
Compare the decoded `misfire_strategy` and `next_run_at_utc` values. The program compares the stored
cursor before and after resume; it deliberately does not compare the database decision with the
application host's clock.

## Break it

Extend the paused interval across several occurrences. `CatchUpOnce` still retains one recovery fire,
not one per missed instant.

## When not to use

Use `Skip` for cache refresh, telemetry, and cleanup. Use catch-up-once for reconciliation/summary work
where one recovery pass matters. Use independently keyed one-shot jobs for accounting or financial
occurrences that must never coalesce.

## Source trail

- [The related Engineering Lab](../../../docs/engineering-labs.md)
- [`schedule-misfire.cs`](./schedule-misfire.cs)
- [`NextOccurrenceCalculator.cs`](../../../src/Acta.Runtime/Modules/Execution/Schedules/NextOccurrenceCalculator.cs)
