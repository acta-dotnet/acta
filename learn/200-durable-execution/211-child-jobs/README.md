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

This lab enqueues each child with `EnqueueChildAsync` and waits for all of them with one `WaitChildrenAsync`, so
every latch is visible. When the children come from a list, `MapAsync` does the same over the same latches
in one call; lab 215 shows both, and lab 212 uses `MapAsync` for a map-reduce.

## Trade-offs

Independent jobs create more rows, claims, serialization, and failure policy than steps. The parent
handler still replays, and child names/latches are durable contract keys.

## Run the experiment

```bash
dotnet run --project learn/200-durable-execution/211-child-jobs
```

## Rows to inspect

Run with `--all-columns` to execute the visible `SELECT *` Explore query first. The default Notice
queries select the fields that prove the lesson, and the text below explains their meaning.

While children run, `jobs_view` shows a suspended, unleased parent plus independently executing children
linked by `parent_job_id`/`lineage_root_job_id`. `checkpoints_view` shows pending child latches. The final
snapshot proves the parent resumed on a later execution.

## Break it

Run the built-in child failure:

```bash
dotnet run --project learn/200-durable-execution/211-child-jobs -- --fail-child
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
- [`JobContext.cs`](../../../src/Acta/Handlers/JobContext.cs)
- [`ListStaleJobChildLatches.sqlite.sql`](../../../src/Acta/Operations/Execution/ListStaleJobChildLatches/ListStaleJobChildLatches.sqlite.sql)

## Remember

- `"bottom"`, `"middle"` and `"head"` are how `build-snowman` finds the children it already started when it runs again. A renamed child is new work: a second snowball is rolled.
- A parent and its children are each stored under their own job name. The names in `[Job("build-snowman")]` and `[Job("roll-snowball")]`, and the namespace in `Run<ChildJobsManifest>("child-jobs")`, are stored with every job. Treat them as permanent once jobs exist under them: a job stored under a name no handler carries stays Ready, and nothing runs it.
- `BuildSnowman` is stored with each job as its input, and `FinishedSnowman` as its result. Add a field as optional or with a default. A renamed, removed or retyped field needs a new job name such as `build-snowman-v2`, and the old handler stays registered until its jobs have finished: [Change a job's input without breaking the jobs already stored](../../500-payloads/502-job-versions/).
