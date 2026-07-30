<!-- engineering-lab
lab: can-fan-out-be-ordinary-jobs
views: jobs_view, checkpoints_view, events_view
alternatives: steps, child-jobs, external-workflow, in-memory-parallelism
-->

# Engineering Lab: child jobs are ordinary jobs; the join is a latch

## The problem

Parallel sub-work needs independent execution and failure evidence, while the parent must wait without
holding a process or worker.

## Common approaches

- Run tasks in memory and join before the handler returns.
- Use durable steps inside one job identity.
- Create independently claimable child jobs and durable parent-owned latches.
- Move the graph to an external workflow engine.

## Why this design

Acta children are normal jobs with their own retries, workers, results, and events. The parent suspends
without a lease; child terminal outcomes set checkpoints owned by the parent, which later re-enters and
joins the results.

## Trade-offs

Independent jobs create more rows, claims, serialization, and failure policy than steps. The parent
handler still replays, and child names/latches are durable contract keys.

## Run the experiment

```bash
dotnet run --project concepts/200-durable-execution/211-child-jobs
```

## Rows to inspect

Run with `--all-columns` to execute the visible `SELECT *` Explore query first. The default Notice
queries select the fields that prove the lesson, and the text below explains their meaning.

While children run, `jobs_view` shows a suspended, unleased parent plus independently executing children
linked by `parent_id`/`lineage_root_job_id`. `checkpoints_view` shows pending child latches. The final
snapshot proves the parent resumed on a later execution.

## Break it

Run the built-in child failure:

```bash
dotnet run --project concepts/200-durable-execution/211-child-jobs -- --fail-child
```

The head child exhausts two attempts, the parent resumes from its latch and ends failed, and the program
prints both terminal rows instead of waiting only for success. Expected framework stack traces are
suppressed; `events_view` retains the complete failure evidence. Continue with lab 219 for explicit
child-failure outcome policy.

## When not to use

Prefer steps when work belongs to one identity, needs no independent ownership, and row/claim overhead
matters. Prefer child jobs for separate retries, parallelism, observability, or worker namespaces. Use a
workflow engine for very large or externally authored graphs.

## Source trail

- [The related Engineering Lab](../../../docs/engineering-labs.md)
- [`child-jobs.cs`](./child-jobs.cs)
- [`JobContext.cs`](../../../src/Acta/Execution/JobContext.cs)
- [`ChildLatches`](../../../src/Acta.Sqlite/Sql/Execution/ChildLatches/)
