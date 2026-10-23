-- Acta ledger checks.
--
-- Every query answers one question about a trial. Unless marked otherwise each MUST return zero rows.
-- They read only the durable ledger Acta wrote about itself while under attack: no harness tables,
-- no instrumentation. That is deliberate - a seal whose evidence the harness invented certifies the
-- harness, not the system.
--
-- Acta Bench runs this file against every trial's schema once the trial is over. Its marker decides
-- when a check applies: [any time] checks always run, [QUIESCED ONLY] checks run only when the trial
-- resolved its accepted work, the [INVERTED] check is fault-landed evidence for the cases that kill a
-- busy worker, and [MEASURED] checks report their row and never fail. A check whose shape a trial did
-- not seed returns zero rows.
--
-- `{s}` is the schema prefix: the trial's schema on PostgreSQL and SQL Server, `main.` on SQLite.
-- `{now}` is the database clock in the dialect's instant encoding. The runner substitutes both.
--
-- Codes, from source rather than memory:
--   JobStatusCode        Ready=10 Blocked=15 Suspended=20 Paused=30 Executing=50
--                        Succeeded=100 Failed=200 Cancelled=220
--   EventCode            40=job.execution-succeeded 41=job.execution-failed 42=job.execution-orphaned
--                        43=job.execution-yielded 44=job.failed (40-44: one per ended attempt)
--                        90=job.note-recorded 122=worker.died
--   EntityCode           50=Job: a job event's subject is entity_id
--
-- TIMING - the evidence MUST stamp LeaseTtlSeconds, HeartbeatInterval and WorkerDeadAfter, because
-- every property here is timing-dependent. A shorter lease manufactures more handler overlap than
-- production would ever see by reclaiming live-but-slow workers sooner; a longer one hides overlap
-- production does see. F09 leaves the triad at the framework defaults (180s / 45s / 5min) in the
-- standard and full profiles, so those runs certify the shipped configuration; the quick profile
-- compresses it and its report says so. The two windows differ, which is why reclaim leads
-- dead-marking: lease 180s, dead-after 5min.
--
-- SCOPE - stated so no reader over-reads a green result. Most checks derive from the ledger alone.
-- Checks 7, 10, 13, 14 and 15 additionally need the witness notes the chaos shapes' bodies write via
-- ctx.NoteAsync, because `steps` keeps no per-attempt history and the claim writes no event.
--
-- Still absent, and not claimed anywhere below: handler-body overlap, which needs enter/exit notes
-- plus an external kill record carrying the killer's own timestamp. Nothing here says "lease
-- exclusivity"; see check 1 for what is actually proven instead.

-- ---------------------------------------------------------------------------------------------
-- 1. execution-event ownership consistency               [any time]
-- ---------------------------------------------------------------------------------------------
-- No two workers ever finished the same attempt. `jobs_state.execution_number` is incremented
-- atomically on each claim, and every attempt event (40-44) carries it beside `worker_id`, which
-- recovery fills with the worker that held the lapsed lease, so a finish recorded under a second worker
-- surfaces exactly, with no clock reasoning at all. A finish with no worker stores `worker_id` 0 and
-- names no second worker.
--
-- This is NOT lease exclusivity. The claim writes no event, so it cannot see a second claim of an
-- attempt that never finished, and it cannot detect two overlapping claims that carried different
-- execution numbers. Report it by this name.
SELECT 'exec-event-ownership' AS check_name, entity_id AS job_id, execution_number, COUNT(DISTINCT worker_id) AS workers
FROM   {s}events
WHERE  entity_code = 50 AND event_code BETWEEN 40 AND 44 AND execution_number IS NOT NULL AND worker_id <> 0
GROUP  BY entity_id, execution_number
HAVING COUNT(DISTINCT worker_id) > 1;

-- ---------------------------------------------------------------------------------------------
-- 2. no attempt finishes twice                           [any time]
-- ---------------------------------------------------------------------------------------------
-- No attempt records two outcomes.
--
-- An attempt with no finish is legal and deliberately not asserted against. Two live workers can
-- claim the same recurring slot moments apart and take different execution numbers: the later one
-- completes, the earlier one's completion is refused on a stale version, and a refused completion
-- writes no event, so its attempt has no finish forever. Nothing was lost or repeated - the slot ran
-- once - and the refusal is deliberate engine behaviour with a spec of its own. Unclosed attempts are
-- covered where they actually matter by check 4, which asks whether any job was left mid-flight.
SELECT 'attempt-pairing' AS check_name, entity_id AS job_id, execution_number, COUNT(*) AS finished
FROM   {s}events
WHERE  entity_code = 50 AND event_code BETWEEN 40 AND 44 AND execution_number IS NOT NULL
GROUP  BY entity_id, execution_number
HAVING COUNT(*) > 1;

-- ---------------------------------------------------------------------------------------------
-- 3. namespace isolation                                 [any time]
-- ---------------------------------------------------------------------------------------------
-- No execution event was ever recorded under a namespace other than the owning job's. A worker
-- claiming work it does not own is the worst failure this model can have, and it is silent without
-- an explicit check.
SELECT 'namespace-isolation' AS check_name, e.entity_id AS job_id, e.namespace_id AS event_ns, j.namespace_id AS job_ns
FROM   {s}events e
JOIN   {s}jobs j ON j.job_id = e.entity_id
WHERE  e.entity_code = 50 AND e.event_code BETWEEN 40 AND 44
  AND  e.namespace_id <> j.namespace_id;

-- ---------------------------------------------------------------------------------------------
-- 4. no stranded work                                    [QUIESCED ONLY]
-- ---------------------------------------------------------------------------------------------
-- Nothing is left mid-flight once the trial is over: no job still Executing.
--
-- Note what this does NOT assert: that every job Succeeded. A realistic workload contains shapes
-- designed to fail, so "everything is Succeeded" would fail a healthy trial. Terminal-state
-- completeness is the invariant that must hold here; each job's expected outcome belongs to the
-- bench's identity oracle, which reconciles every logical job against its model.
--
-- One exception: a system recurring job whose lease is still live. A trial's workers stop with the
-- trial, and sys.alerts fires on every minute, so a check that follows a fire meets an attempt whose
-- lease has not yet lapsed. A system job stuck behind a lapsed lease still fails the check.
SELECT 'no-stranded-work' AS check_name, r.job_id, r.job_status_code, r.leased_by_worker_id
FROM   {s}jobs_state r
JOIN   {s}jobs j ON j.job_id = r.job_id
JOIN   {s}definitions d ON d.definition_id = j.definition_id
WHERE  r.job_status_code = 50
  AND  NOT (d.definition_name LIKE 'sys.%' AND r.lease_expires_at_utc > {now});

-- ---------------------------------------------------------------------------------------------
-- 5. expected outcome per job shape                      [retired]
-- ---------------------------------------------------------------------------------------------
-- The bench's identity oracle owns each job's expected terminal state and result, so this file keeps
-- no second copy of it. The at-most-once charge may end Failed there, and only there: a kill inside
-- its body terminalizes the ambiguity instead of running the charge again, and its real assertion is
-- check 10, which counts how many times the body ran.

-- ---------------------------------------------------------------------------------------------
-- 6. terminal-state integrity                            [QUIESCED ONLY]
-- ---------------------------------------------------------------------------------------------
-- A job that reached a terminal status holds no execution lease.
SELECT 'terminal-integrity' AS check_name, r.job_id, r.job_status_code, r.leased_by_worker_id
FROM   {s}jobs_state r
WHERE  r.job_status_code IN (100, 200, 220)
  AND  r.leased_by_worker_id IS NOT NULL;

-- ---------------------------------------------------------------------------------------------
-- 7. no step body ran more times than the step was attempted   [QUIESCED ONLY]
-- ---------------------------------------------------------------------------------------------
-- The differentiating claim, and the reason the note API exists: "kill the worker, completed steps
-- do not re-run."
--
-- `steps` keeps only current state - one row per (job, name), UPDATE-in-place, no per-attempt
-- history - so a body that ran twice leaves no trace there. The witness is the note each body writes
-- before doing its work, and `attempt_number` is how many times the engine admitted a body.
--
-- Counting, not timestamps. Comparing a note's time against the step's `modified_at_utc` reads "note
-- after success" as a replay, and that is wrong under load: every routine captures its clock at
-- entry, so a write blocked on locks lands with a stale stamp, and a step's `modified_at_utc` can even
-- precede its own `created_at_utc`. One note per admitted attempt is the same claim with no clock in
-- it.
--
-- A second note is NOT a violation by itself: at-least-once means a body interrupted before its
-- outcome committed legitimately re-runs, and that re-run is an attempt the engine counted. The
-- violation is a body that ran without the engine admitting it, which is exactly notes > attempts.
SELECT 'step-replay' AS check_name, s.job_id, s.step_name, s.attempt_number, n.notes
FROM   {s}steps s
JOIN   (SELECT entity_id, event_message, COUNT(*) AS notes
        FROM   {s}events
        WHERE  entity_code = 50 AND event_code = 90
        GROUP BY entity_id, event_message) n
  ON   n.entity_id = s.job_id
 AND   n.event_message = CONCAT('step-body ', s.step_name)   -- CONCAT: || is not SQL Server
WHERE  s.step_status_code = 100
  AND  n.notes > s.attempt_number;

-- ---------------------------------------------------------------------------------------------
-- 8. the chaos was real                                  [INVERTED: must return exactly one row]
-- ---------------------------------------------------------------------------------------------
-- A run in which nothing was reclaimed proves nothing, and would pass every check above trivially.
-- This is the guard against a green seal that means nothing, applied to the cases that kill a busy
-- worker: a missing row leaves the trial INCONCLUSIVE, never PASS.
--
-- Keys on ORPHANED ATTEMPTS (42), not on worker.died (122). Recovery reclaims a killed worker's in-flight
-- work on lease expiry without necessarily marking the worker row dead, so orphaned attempts can
-- accumulate while 122 stays at zero. Requiring 122 would fail a run whose chaos was demonstrably
-- real. It is reported as a number, never asserted. A reclaim that spends the job's last attempt writes
-- 44 instead, so the count is a floor.
--
-- Note what this implies about run length: JobsOptions.LeaseTtlSeconds defaults to 180 and the
-- recovery pass runs once a lease, so a killed worker's job is not reclaimable for up to two leases.
-- A workload that drains faster than that reports zero reclaims no matter how many workers were
-- killed. Size a chaos run by duration against that floor, never by job count.
SELECT 'chaos-was-real' AS check_name,
       SUM(CASE WHEN event_code = 42 THEN 1 ELSE 0 END) AS orphaned_attempts,
       SUM(CASE WHEN event_code = 122 THEN 1 ELSE 0 END) AS workers_marked_dead
FROM   {s}events
HAVING SUM(CASE WHEN event_code = 42 THEN 1 ELSE 0 END) > 0;

-- ---------------------------------------------------------------------------------------------
-- 9. how far the recorded clock ran backwards            [MEASURED: always reports, never fails]
-- ---------------------------------------------------------------------------------------------
-- `events.event_id` is assigned in insert order, so a higher id carrying an earlier `created_at_utc` means
-- the recorded time moved backwards between those two writes. Reported, never asserted, because two
-- causes are indistinguishable from here and only one is a fault: a routine captures its clock at
-- entry, so a write blocked on locks lands with a stale stamp and looks identical to a container
-- whose clock actually stepped back.
--
-- It is here because a run that certifies timing-dependent properties must show what its clock did:
-- without it, a check failing on a bunched or reordered stamp has no named cause.
SELECT 'clock-backsteps' AS check_name, COUNT(*) AS backwards_writes
FROM   (SELECT created_at_utc,
               LAG(created_at_utc) OVER (ORDER BY event_id) AS previous_created_at_utc
        FROM   {s}events) ordered
WHERE  ordered.created_at_utc < ordered.previous_created_at_utc
HAVING COUNT(*) >= 0;

-- ---------------------------------------------------------------------------------------------
-- 10. an AtMostOnce body never ran twice                 [any time]
-- ---------------------------------------------------------------------------------------------
-- The double-spend claim. `AtMostOnce` promises a body runs zero or one times: on replay the slot is
-- poisoned rather than re-entered, and the caller gets StepInterruptedException instead of a second
-- charge. `steps` cannot show this - one row per (job, name), updated in place - so the witness is
-- the note the body writes before doing its work, which commits in its own operation and therefore
-- survives when the step's own outcome does not.
--
-- Zero notes is legal and common: the kill landed before the engine admitted the body. The violation
-- is strictly more than one, which is a body that ran again under a contract that forbids it.
SELECT 'at-most-once' AS check_name, n.job_id, n.bodies
FROM   (SELECT entity_id AS job_id, COUNT(*) AS bodies
        FROM   {s}events
        WHERE  entity_code = 50 AND event_code = 90 AND event_message = 'charge-body'
        GROUP  BY entity_id) n
WHERE  n.bodies > 1;

-- ---------------------------------------------------------------------------------------------
-- 11. every relayed outbox job landed Succeeded          [QUIESCED ONLY]
-- ---------------------------------------------------------------------------------------------
-- The ledger-reachable half of the outbox certification. The staging table lives in the producer's
-- database, which this file - bound to the Acta schema - cannot reach, so the check is split across
-- that ownership seam: F09 probes the producer for *drained* (zero rows Pending or Claimed after
-- quiesce) and bridges staged operation counts to the ledger for *delivered* (every committed
-- operation's job exists; a deduplicated handoff counts as delivered). What the ledger alone can
-- assert is what happened AFTER the handoff: every job the relay created ran to Succeeded. The staged
-- rows target a handler with no designed failure, so any other terminal state is a real finding.
-- Relayed jobs are recognized by the deduplication key the producer mints (`outbox-{n}`); a trial
-- that staged nothing passes vacuously.
SELECT 'outbox-relayed-outcome' AS check_name, r.job_id, r.job_status_code
FROM   {s}jobs_state r
JOIN   {s}jobs j ON j.job_id = r.job_id
WHERE  j.deduplication_key LIKE 'outbox-%'
  AND  r.job_status_code <> 100;

-- ---------------------------------------------------------------------------------------------
-- 12. how many outbox rows the relay delivered           [MEASURED: always reports, never fails]
-- ---------------------------------------------------------------------------------------------
-- The number F09 pairs with its producer-side staged count: only the harness can see both databases,
-- so the ledger half is reported here as evidence and F09's outbox-delivered check asserts the pair.
SELECT 'outbox-relayed' AS check_name, COUNT(*) AS relayed_jobs
FROM   {s}jobs j
WHERE  j.deduplication_key LIKE 'outbox-%'
HAVING COUNT(*) >= 0;

-- ---------------------------------------------------------------------------------------------
-- 13. tenant context survived every hop                  [any time]
-- ---------------------------------------------------------------------------------------------
-- The tenant supplied at enqueue is the tenant the handler observed. Deliberately NOT a comparison of
-- events.tenant_id to jobs.tenant_id: those are two projections of one stored value, so that query
-- passes by construction and proves nothing. The handler notes the TenantName it actually saw, which
-- is the far side of enqueue, claim, dispatch and payload decode, and this compares that against the
-- row the job was stored with.
--
-- Not a security claim. docs/developer-guide.md states plainly that the tenant field is not a security
-- or isolation boundary; this asserts that the value survives intact, not that it confines anything.
SELECT 'tenant-context' AS check_name, e.entity_id AS job_id, e.event_message AS observed, COALESCE(t.tenant_name, '-') AS job_tenant
FROM   {s}events e
JOIN   {s}jobs j ON j.job_id = e.entity_id
LEFT   JOIN {s}tenants t ON t.tenant_id = j.tenant_id
WHERE  e.entity_code = 50 AND e.event_code = 90
  AND  e.event_message LIKE 'tenant %'
  AND  e.event_message <> CONCAT('tenant ', COALESCE(t.tenant_name, '-'));

-- ---------------------------------------------------------------------------------------------
-- 14. the rate meter never over-admitted                 [COMPUTED IN PROCESS: see LedgerValidation]
-- ---------------------------------------------------------------------------------------------
-- The rate-limit claim: a definition declaring a rate never starts more handlers inside a window than
-- its meter allows, while workers are being killed and their attempts reclaimed and re-run. F09 runs
-- three meters side by side, `metered-10`, `metered-50`, and `metered-100` at 10/s, 50/s, and 100/s,
-- and each is checked against its own budgets, so one meter admitting on another's turns fails too.
--
-- The witness is the `metered-admitted` note each metered body writes as its first action (event
-- code 90), one per admitted attempt. Admission is metered per attempt, after the claim and
-- before the body, so a reclaimed attempt that runs again is metered again; `steps` keeps one row
-- per (job, name) and updates it in place, so it cannot count starts at all.
--
-- The contract is an upper envelope, not a smooth rate. For a declared rate of R admissions per
-- second with burst B, a window of T seconds holds at most R*(T + W) + B admitted starts, W being
-- how long a booked turn stays valid past its instant: one second, or the emission interval when
-- that is longer. The burst is one second's worth of the rate and W is one second at all three rates,
-- so the budgets for one and ten seconds are 30 and 120 at 10/s, 150 and 600 at 50/s, and 300 and
-- 1,200 at 100/s.
--
-- This is the one check that is not SQL. Counting a sliding window needs timestamp arithmetic, and
-- date addition has three different spellings across the providers this file runs on unchanged.
-- LedgerValidation reads each rate-limited definition's note instants over the same connection,
-- counts the windows in process, and records `rate-contract` beside these checks: one failure per
-- window over budget, plus the admitted total and the busiest window of each length, so a passing
-- trial shows its numbers too.
--
-- Read those numbers with check 9 beside them. A note blocked on locks lands with the stamp its
-- routine captured at entry, so a run whose recorded clock stepped backwards can bunch admissions
-- into a window they did not belong to. The envelope absorbs a little of that; a violation of a few
-- starts over budget on a run with many backsteps is worth re-reading before it is called a defect.

-- ---------------------------------------------------------------------------------------------
-- 15. a lane ran its members one at a time, in enqueue order   [QUIESCED ONLY]
-- ---------------------------------------------------------------------------------------------
-- The lane claim: of two consecutive members of a lane, the later one never started before the
-- earlier one finished for the last time, through kills, reclaims, and re-runs of the head. That is
-- both halves of it at once: nothing overtakes, and no two members of a lane are ever in flight.
--
-- No clock. `events.event_id` is assigned in insert order, and the settle that finishes a member promotes
-- the next in the same transaction, so everything the next member writes is inserted after that finish.
-- The claim writes no event, so a member's start is its first note, which its first attempt writes on
-- entering its body; a later member claimed early but silent until the earlier one finished escapes
-- this check, and so does a member that writes no note at all. Consecutive pairs are enough: each
-- member finishes after it starts, so the order is transitive. An earlier member with no finish at
-- all while a later one started is a violation too.
SELECT 'lane-order' AS check_name, p.lane_id, p.job_id AS earlier_job, p.next_job_id AS later_job
FROM   (SELECT r.lane_id, r.job_id,
               LEAD(r.job_id) OVER (PARTITION BY r.lane_id ORDER BY r.job_id) AS next_job_id
        FROM   {s}jobs_state r
        WHERE  r.lane_id IS NOT NULL) p
JOIN   (SELECT entity_id, MIN(event_id) AS first_start
        FROM   {s}events
        WHERE  entity_code = 50 AND event_code = 90
        GROUP  BY entity_id) s ON s.entity_id = p.next_job_id
LEFT   JOIN (SELECT entity_id, MAX(event_id) AS last_finish
             FROM   {s}events
             WHERE  entity_code = 50 AND event_code BETWEEN 40 AND 44
             GROUP  BY entity_id) f ON f.entity_id = p.job_id
WHERE  f.last_finish IS NULL
   OR  s.first_start < f.last_finish;

-- ---------------------------------------------------------------------------------------------
-- 16. every lane drained                                  [QUIESCED ONLY]
-- ---------------------------------------------------------------------------------------------
-- No laned job is left behind once the trial is over: none Ready, Blocked, Suspended, or Paused.
-- Check 4 covers only rows in flight, and a lane whose promotion was lost strands its members
-- Blocked, which nothing in flight would show.
SELECT 'lane-drained' AS check_name, r.job_id, r.lane_id, r.job_status_code
FROM   {s}jobs_state r
WHERE  r.lane_id IS NOT NULL
  AND  r.job_status_code NOT IN (100, 200, 220);

-- ---------------------------------------------------------------------------------------------
-- 17. where the chaos landed                             [MEASURED: always reports, never fails]
-- ---------------------------------------------------------------------------------------------
-- Check 8 proves kills reclaimed something; this says which of the shapes that exist to be
-- interrupted they reached: the at-most-once charge (check 10), the three meters (check 14), and the
-- laned jobs (checks 15-16). A zero means that shape's checks ran with no kill in flight and prove
-- less than their names say.
SELECT 'chaos-by-shape' AS check_name,
       SUM(CASE WHEN d.definition_name = 'at-most-once-charge' THEN 1 ELSE 0 END) AS charges_orphaned,
       SUM(CASE WHEN d.definition_name LIKE 'metered-%' THEN 1 ELSE 0 END) AS metered_orphaned,
       SUM(CASE WHEN d.definition_name = 'laned' THEN 1 ELSE 0 END) AS laned_orphaned
FROM   {s}events e
JOIN   {s}definitions d ON d.definition_id = e.definition_id
WHERE  e.event_code = 42
HAVING COUNT(*) >= 0;
