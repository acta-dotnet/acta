<!-- engineering-lab
lab: can-one-permanent-job-keep-a-system-in-sync
also-labs: can-recurring-jobs-run-forever-on-one-row, can-sql-replace-a-job-queue
views: jobs_view, schedules_view, checkpoints_view, events_view
alternatives: timer-loop, queue-plus-state-table, platform-cron, workflow-engine
-->

# Engineering Lab: one permanent job keeps an external system synchronized

## The problem

An application has to stay current with an external system, a web shop here, that publishes changes
faster than anyone wants to poll by hand: orders are created, paid, and shipped all day. The usual
answer is a loop in a hosted service with a timer, a retry policy, a table that remembers how far
the loop got, and a prayer that the process is running. When the process dies mid-batch, the loop's
in-memory position dies with it, and nothing says whether the last batch landed.

## Common approaches

- A `BackgroundService` with a `PeriodicTimer`: progress lives in memory or in a hand-maintained
  table, and nothing records what each run did.
- A queue that fans out "sync now" messages to workers, plus a state table, plus a scheduler to
  enqueue them.
- A platform cron that starts a process per tick, which must find its own position and never
  overlap the previous tick.
- A workflow engine with a long-running activity that loops forever.

## Why this design

One permanent job, `sync-orders`, with one schedule, `every-5s`, and one durable variable,
`watermark`. The handler, as `SyncOrdersJob.cs` has it without its console lines and the lab's
crash switch:

```csharp
[Job("sync-orders", MaxAttempts = 3, Backoff = "2s", EventRecording = EventRecordingCode.Audit)]
[Schedule("every-5s", "PT5S")]
public async Task Sync(JobContext ctx, CancellationToken ct)
{
    var watermark = await ctx.GetVariableOrDefaultAsync("watermark", 0L, ct);
    var changes = await shop.ChangesAfterAsync(watermark, 40, ct);
    if (changes.Count == 0) return;                                    // up to date: nothing to do until the next firing
    await store.ApplyAsync(changes, ct);                               // an upsert: a replayed batch lands once
    await ctx.SetVariableAsync("watermark", changes[^1].ChangeId, ct);
}
```

The web shop is a simulator: an append-only table of order changes in the lab's own SQLite file,
beside the local orders and Acta's tables, seeded the same way on every machine. The handler reads
it through `ShopFeed` as it would read a remote API, never through Acta.

The schedule admits a firing onto the permanent job every five seconds; the firing applies one
batch of forty changes and returns; the job then sits `Idle` until the next admission, holding no
thread. The watermark, the id of the last shop change applied, is a variable on the job. Acta keeps
it the way it keeps the job itself: a row in `checkpoints_view`, carried across firings, retries,
and workers, and the handler reads it at the top of every execution. Nothing else remembers how far
the sync has come: no table of the application's, no field, no loop.

That is what makes the crash boring. When the worker dies after a batch, the next worker, or the
same one restarted, reclaims the firing through the lease, re-enters the handler from the top,
reads the watermark the dead attempt wrote, and applies the next batch. The same job identity, the
same firing id, one more execution number.

Acta's own progress is separate. The schedule cursor says when the job is next due, and the firing
envelope says which schedule admitted the firing and how it settled. A firing that finds nothing to
sync still moves the cursor and leaves the watermark alone; a manual trigger moves the watermark
and leaves the cursor alone.

## Trade-offs

Execution is at-least-once, and the handler is written for it. The orders land in the store's
transaction and the watermark in Acta's, one after the other, so an attempt that dies between the
two is run again and replays one batch; the upsert keeps only a newer change per order, so the
replay changes nothing. Written the other way round, watermark first, a death between the two would
skip a batch. A sync that writes back to the external system needs that system's idempotency key,
as the agent guide's webhook sample shows.

A full resync is a reset: `ctx.ResetStateAsync()` as the handler's final action clears the
watermark with the rest of the job's state, and the next firing starts from change 0, where the
upsert lands every change once more. The lab never needs it, so it never calls it.

The feed's contract carries the correctness: change ids only grow, changes come back in id order,
and none is ever deleted. A source without ordered, retained change tracking cannot be synchronized
by watermark, and this lab does not pretend otherwise.

## Run the experiment

Everything runs on embedded SQLite from a fresh clone; the lab keeps its own database file. The
first command runs the whole story through three firings and exits; the rest reproduce the crash by
hand, in two terminals, with a real process kill (`Environment.FailFast`). The tests in
[`804-continuous-synchronization-tests`](../../800-testing/804-continuous-synchronization-tests/)
prove the same recovery inside one process instead, by expiring the lease and fencing the late
attempt, and Acta's release validation kills worker processes at scale.

```bash
# 1. Seed 120 order changes, watch three firings sync them in batches of forty, print the rows, exit.
dotnet run --project learn/100-scheduling/109-continuous-synchronization

# 2. Terminal one: run the worker; a firing every five seconds.
dotnet run --project learn/100-scheduling/109-continuous-synchronization -- worker

# 3. Terminal two: publish sixty more changes, then arm a crash after the next batch.
dotnet run --project learn/100-scheduling/109-continuous-synchronization -- more 60
dotnet run --project learn/100-scheduling/109-continuous-synchronization -- crash
#    Terminal one dies with a non-zero exit after its next batch.

# 4. Inspect the durable state the dead worker left behind.
dotnet run --project learn/100-scheduling/109-continuous-synchronization -- inspect

# 5. Terminal one: restart the worker. Its start finds the killed worker's process gone and reclaims the firing.
dotnet run --project learn/100-scheduling/109-continuous-synchronization -- worker

# 6. Inspect again: the same job and firing, one more execution, the watermark at the feed's head.
dotnet run --project learn/100-scheduling/109-continuous-synchronization -- inspect

# 7. Start over.
dotnet run --project learn/100-scheduling/109-continuous-synchronization -- reset
```

`outage` switches the shop feed off and on again: a firing that cannot read the feed fails its
attempt, the ledger records `job.execution-failed`, and Acta retries it under the job's budget
(`MaxAttempts = 3`, two seconds apart); a spent budget settles the firing as failed and returns the
job to `Idle`, and the next scheduled firing syncs as soon as the feed is back. The watermark is
untouched by a failed attempt, so nothing is lost and nothing is applied twice.

The lab shortens the heartbeat to one second, so the lease is four seconds and the restarted worker
recovers within about ten; production keeps the default forty-five seconds.

## Rows to inspect

Run with `--all-columns` to execute the visible `SELECT *` Explore query first. The default Notice
queries select the fields that prove the lesson, and the text below explains their meaning. The rows
below are from a run on 2026-10-30.

**After the tour (step 1).** Three firings synced 120 changes in three batches of forty, and the job
is back to `Idle` with no lease; the cursor already names the next occurrence, the envelope records
how the last firing settled, and the watermark sits on the job as a variable.

```text
job_ref                              | job_status | execution_number | failure_count | leased_by_worker_host_name | lease_expires_at_utc
01a18cba-b966-7a5a-aaf2-83061ba0af99 | idle       | 3                | 0             | NULL                       | NULL

schedule_name | schedule_status | next_occurrence_at_utc   | next_evaluation_at_utc   | evaluation_status
every-5s      | active          | 2026-10-30 12:13:49.813Z | 2026-10-30 12:13:49.813Z | scheduled

checkpoint_name | checkpoint_status | value_text
sys.firing      | succeeded         | {"firingId":"01a18cba-f42b-7cd7-b4d…

checkpoint_name | checkpoint_kind | value_text | modified_at_utc
watermark       | variable        | 120        | 2026-10-30 12:13:44.882Z

event_id | event                   | execution_number
7        | job.execution-succeeded | 3
6        | schedule.admitted       | 2
5        | job.execution-succeeded | 2
4        | schedule.admitted       | 1
3        | job.execution-succeeded | 1
2        | schedule.admitted       | 0

feed head: change 120; local orders: 40 (newest change 120)
```

`jobs_view` is one row for the whole run: the same `job_ref`, one more `execution_number` per firing.
`schedules_view` is Acta's progress, the cursor and when the engine looks again. `checkpoints_view`
holds both the firing envelope under `sys.firing`, the schedule that admitted the firing, its
occurrence, and the firing's settlement, and the handler's watermark under its own name, kind
`variable`, written by `SetVariableAsync` and nowhere else. The ledger alternates an admission and a
settlement per firing; an admission row carries the execution number the job had when it was
admitted, so the one that opened execution 1 reads 0. The last line is the destination, the one
table that is the application's.

**After the crash (step 4).** Sixty more changes were published and the worker was armed. Its next
firing applied one batch, wrote watermark 160, and the process killed itself before the attempt
completed. The worker's last lines:

```text
[sync-orders] execution 7: 40 changes applied, watermark 160
[sync-orders] crash armed: the batch through change 160 is committed; the process disappears now.
Process terminated.
```

The rows the dead process left behind: the job still `executing` under a lease nobody will renew,
the envelope still `pending`, the watermark at the batch the dead worker applied, and the feed
twenty changes ahead of it.

```text
job_ref                              | job_status | execution_number | failure_count | leased_by_worker_host_name | lease_expires_at_utc
01a18cba-b966-7a5a-aaf2-83061ba0af99 | executing  | 7                | 0             | worker-host-01             | 2026-10-30 12:14:08.857Z

checkpoint_name | checkpoint_status | value_text
sys.firing      | pending           | {"firingId":"01a18cbb-422e-7ac2-9e3…

checkpoint_name | checkpoint_kind | value_text | modified_at_utc
watermark       | variable        | 160        | 2026-10-30 12:14:04.877Z

feed head: change 180; local orders: 40 (newest change 160)
```

**After the restart (step 6).** The restarted worker found the killed worker's process gone, released
its lease and reclaimed the firing, and the claim ran it as execution 8 of the same job and the same firing. The handler
re-entered from the top, read watermark 160, and applied the twenty changes the dead process never
reached; the firings after it found nothing to do.

```text
[sync-orders] execution 8: 20 changes applied, watermark 180
[sync-orders] execution 9: up to date at change 180

event_id | event                   | execution_number | event_message                        | details_text
19       | job.execution-orphaned  | 7                | Worker's process ended; released wh… | {"reason":"process-ended","to":"rea…
20       | job.execution-succeeded | 8                | NULL                                 | NULL
21       | schedule.skipped        | 8                | NULL                                 | {"schedule":"every-5s","from":"2026…
22       | schedule.admitted       | 8                | NULL                                 | {"schedule":"every-5s","occurrence"…
23       | job.execution-succeeded | 9                | NULL                                 | NULL

checkpoint_name | checkpoint_kind | value_text | modified_at_utc
watermark       | variable        | 180        | 2026-10-30 12:14:17.971Z

job_ref                              | job_status | execution_number | failure_count | leased_by_worker_host_name | lease_expires_at_utc
01a18cba-b966-7a5a-aaf2-83061ba0af99 | idle       | 12               | 0             | NULL                       | NULL

feed head: change 180; local orders: 40 (newest change 180)
```

`job.execution-orphaned` is the reclaim: attempt 7 lost its worker and the job went back to `Ready`
(`"to":"ready"`). No admission sits between the reclaim and execution 8, so execution 8 ran the
firing execution 7 lost, not a new one; the envelope that was `pending` under execution 7 settled
under execution 8. `schedule.skipped` is the misfire policy at work: the occurrences that fell due
while no worker ran were skipped, not queued, which is the default and the right one for a
controller that catches up by itself. Event 22 admits the next firing, with an id of its own.
Nothing was applied twice: the destination holds forty orders at change 180, one row per order,
and the watermark equals the feed's head.

## Break it

Move `crash.AfterBatchAsync` in `SyncOrdersJob.cs` between `store.ApplyAsync` and
`ctx.SetVariableAsync` and arm a crash: the orders hold the batch, the watermark does not, and the
recovered firing applies the same batch again, which the upsert absorbs. That is the replay
at-least-once allows, and the reason the orders land first. Then swap the two lines so the
watermark is written before the orders, and crash between them: the recovered firing starts after
a batch the orders never received, forty changes short, which is the bug. Finally drop the
`WHERE excluded.last_change_id > local_orders.last_change_id` clause from the upsert and replay a
batch: an older change overwrites a newer order, which is what the clause is for.

## When not to use

- A source that publishes changes without a stable order or that forgets them (a webhook that fires
  once, a feed that compacts). Watermarks need the contract above; capture such a source into a
  durable feed of your own first.
- Work where each occurrence is its own business event with its own retries, result, and retention:
  a nightly statement run, an hourly import whose files must each be tracked. That is a
  `PerOccurrence` definition, one ordinary root job per occurrence, not a permanent job.
- A sync that must write back to the external system: still a permanent job, but every write needs
  the external system's idempotency key, as in the agent guide's webhook sample.

## Source trail

- [The related Engineering Lab](../../../docs/engineering-labs.md)
- [`continuous-synchronization.cs`](./continuous-synchronization.cs), [`SyncOrdersJob.cs`](./SyncOrdersJob.cs), [`OrderStore.cs`](./OrderStore.cs), [`ShopFeed.cs`](./ShopFeed.cs)
- The tests that prove each claim without sleeping: [`804-continuous-synchronization-tests`](../../800-testing/804-continuous-synchronization-tests/)
- [`ScheduleAdmitter.cs`](../../../src/Acta/Engine/Scheduling/ScheduleAdmitter.cs), the admission pass that moves the permanent job from `Idle` to `Ready`
- [`ReclaimStuckJobs.routine.pg.sql`](../../../src/Acta/Operations/Execution/ReclaimStuckJobs/ReclaimStuckJobs.routine.pg.sql), the recovery that reclaims a lost firing
- [Scheduling and calendars](../../../docs/scheduling.md), the page on schedules

## Remember

- Never loop forever inside the handler and never poll from it. A firing that finds nothing to do returns, and the next firing comes on schedule.
- The watermark is a durable variable on the job. Acta carries it across firings, retries, and workers, so no table of your own has to remember how far the sync got.
- The service has to hand out changes in a fixed order and keep them. For a source that fires once or forgets, capture it into a durable feed of your own first.
- Firings that came due while no worker ran are skipped, not queued. The next firing reads the watermark and catches up by itself.
- With the default heartbeat, a killed worker's firing comes back three to about seven minutes after its last heartbeat; on SQLite a restart brings it back at once. The lab shortens the heartbeat so you can watch.
- A sync that writes back to the other system needs that system's idempotency key, so a repeated batch does not act twice there.
- `"watermark"` is the key to how far `sync-orders` has read. A renamed variable reads as absent, as if the job had never synchronized.
- A job on a schedule runs across every deploy, for as long as the application lives. The name in `[Job("sync-orders")]` is stored with every job. Treat it as permanent once jobs exist under it: a job stored under a name no handler carries stays Ready, and nothing runs it.

## Check yourself

**The worker is killed right after a batch was saved and the watermark written. What does the next execution do?**

- Starts again from change 0
- Reads the watermark the dead attempt wrote and applies the next batch
- Nothing, until someone restarts the job by hand

<details><summary>Answer</summary>

Reads the watermark the dead attempt wrote and applies the next batch. The next worker, or the same one restarted, reclaims the firing, runs the handler from its first line, and reads the watermark. Same job, same firing, one more execution number.

</details>

**An attempt dies after the orders were saved but before the watermark moved. What happens to that batch?**

- It is applied a second time, and the upsert changes nothing
- It is skipped
- The job fails for good

<details><summary>Answer</summary>

It is applied a second time, and the upsert changes nothing. The retry reads the old watermark and gets the same changes. Each order keeps only a newer change than the one it holds, so the replay leaves the data as it was.

</details>
