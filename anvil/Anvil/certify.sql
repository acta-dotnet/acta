-- Acta certification checks.
--
-- Every query answers one question about a run. Unless marked otherwise each MUST return zero rows.
-- They read only the durable ledger Acta wrote about itself while under attack: no harness tables,
-- no instrumentation. That is deliberate - a seal whose evidence the harness invented certifies the
-- harness, not the system.
--
-- `{s}` is the schema prefix: `acta.` on PostgreSQL and SQL Server, empty on SQLite (which has no
-- schemas). The runner substitutes it.
--
-- Codes, from source rather than memory:
--   JobStatusCode        Ready=10 Suspended=20 Paused=30 Dispatched=40 Executing=50
--                        Succeeded=100 Failed=200 Cancelled=220
--   ExecutionStatusCode  Executing=50 Succeeded=100 Rescheduled=150 Suspended=151 Paused=152
--                        Failed=200 Cancelled=220 Orphaned=230
--   EventCode         40=job.execution-started 41=job.execution-finished 122=worker.died
--
-- TIMING - the seal MUST stamp LeaseTtlSeconds, HeartbeatInterval and WorkerDeadAfter, because every
-- property here is timing-dependent. A shorter lease manufactures more handler overlap than
-- production would ever see by reclaiming live-but-slow workers sooner; a longer one hides overlap
-- production does see. Anvil deliberately leaves the triad at the framework defaults (180s / 45s /
-- 5min), so a run certifies the shipped configuration - keep it that way, or say otherwise on the
-- seal. The two windows differ, which is why reclaim leads dead-marking: lease 180s, dead-after 5min.
--
-- SCOPE - stated so no reader over-reads a green result. Most checks derive from the ledger alone.
-- Check 7 additionally needs the witness notes Anvil's step bodies write via ctx.NoteAsync, because
-- `steps` keeps no per-attempt history.
--
-- Still absent, and not claimed anywhere below: handler-body overlap, which needs enter/exit notes
-- plus an external kill record carrying the killer's own timestamp. Nothing here says "lease
-- exclusivity"; see check 1 for what is actually proven instead.

-- ---------------------------------------------------------------------------------------------
-- 1. execution-event ownership consistency               [any time]
-- ---------------------------------------------------------------------------------------------
-- No two workers ever emitted execution events for the same attempt. `runtimes.execution_number` is
-- incremented atomically on each claim and both 40/41 carry it beside `worker_id`, so a double-claim
-- surfaces exactly, with no clock reasoning at all.
--
-- This is NOT lease exclusivity. It cannot see a claim that died before emitting 40, and it cannot
-- detect two overlapping claims that carried different execution numbers. Report it by this name.
SELECT 'exec-event-ownership' AS check_name, job_id, execution_number, COUNT(DISTINCT worker_id) AS workers
FROM   {s}events
WHERE  event_code IN (40, 41) AND execution_number IS NOT NULL
GROUP  BY job_id, execution_number
HAVING COUNT(DISTINCT worker_id) > 1;

-- ---------------------------------------------------------------------------------------------
-- 2. no attempt starts twice or finishes twice          [QUIESCED ONLY]
-- ---------------------------------------------------------------------------------------------
-- No attempt starts twice, and no started attempt is left unclosed.
--
-- A 41 with no 40 is LEGAL and deliberately not asserted against: a worker that dies after claim but
-- before StartExecution produces exactly that, and recovery closes it as orphaned -
-- WorkerCrashRecoveryChaosSpec covers that case by name.
--
-- Run only after the workload has quiesced. Mid-flight this reports every attempt currently
-- executing as "started but not finished", which is a false failure, not a finding - observed live
-- on the first run of this file.
--
-- Quiesce is not instant, and stopping the chaos does not end it: jobs held by already-killed workers
-- stay Executing until their 180s lease lapses and the next recovery tick sweeps them. Wait for
-- in-flight to reach zero rather than for the producer to stop, or this check fires on the tail.
--
-- A 40 with no 41 is also legal, and this check used to assert otherwise. Observed 2026-08-13 on a
-- two-participant ensemble: two LIVE workers claimed the same recurring slot 16ms apart and took
-- different execution numbers. The later one completed; the earlier one's completion was refused on a
-- stale version, and a refused completion writes no event, so its attempt keeps a start with no finish
-- forever. Nothing was lost or repeated - the slot ran once - and the refusal is deliberate engine
-- behaviour with a spec of its own (the refused completion beside its successor). Asserting that every
-- start closes therefore demanded a guarantee the design does not make.
--
-- What survives is the pair of claims that are real: an attempt never starts twice, and never finishes
-- twice. Unclosed attempts are covered where they actually matter by check 4, which asks whether any
-- job was left mid-flight - the operational question - rather than whether every attempt record was
-- tidied up.
SELECT 'attempt-pairing' AS check_name, job_id, execution_number,
       SUM(CASE WHEN event_code = 40 THEN 1 ELSE 0 END) AS started,
       SUM(CASE WHEN event_code = 41 THEN 1 ELSE 0 END) AS finished
FROM   {s}events
WHERE  event_code IN (40, 41) AND execution_number IS NOT NULL
GROUP  BY job_id, execution_number
HAVING SUM(CASE WHEN event_code = 40 THEN 1 ELSE 0 END) > 1
    OR SUM(CASE WHEN event_code = 41 THEN 1 ELSE 0 END) > 1;

-- ---------------------------------------------------------------------------------------------
-- 3. namespace isolation                                 [any time]
-- ---------------------------------------------------------------------------------------------
-- No execution event was ever recorded under a namespace other than the owning job's. A worker
-- claiming work it does not own is the worst failure this model can have, and it is silent without
-- an explicit check.
SELECT 'namespace-isolation' AS check_name, e.job_id, e.namespace_id AS event_ns, j.namespace_id AS job_ns
FROM   {s}events e
JOIN   {s}jobs j ON j.id = e.job_id
WHERE  e.event_code IN (40, 41)
  AND  e.namespace_id <> j.namespace_id;

-- ---------------------------------------------------------------------------------------------
-- 4. no stranded work                                    [QUIESCED ONLY]
-- ---------------------------------------------------------------------------------------------
-- Nothing is left mid-flight once the run is over: no job still Dispatched or Executing.
--
-- Note what this does NOT assert: that every job Succeeded. A realistic workload contains shapes
-- designed to fail - Anvil seeds `always-fails` - so "everything is Succeeded" would fail a healthy
-- run. Terminal-state completeness is the honest invariant; per-shape outcomes are check 5.
SELECT 'no-stranded-work' AS check_name, r.job_id, r.status_code, r.leased_by_worker_id
FROM   {s}runtimes r
WHERE  r.status_code IN (40, 50);

-- ---------------------------------------------------------------------------------------------
-- 5. expected outcome per job shape                      [QUIESCED ONLY]
-- ---------------------------------------------------------------------------------------------
-- Every job landed in the terminal state its shape is designed for: only `always-fails` may end
-- Failed. Anything else failing is a real finding, and would otherwise hide inside check 4's pass.
SELECT 'expected-outcome' AS check_name, d.name AS job_name, r.status_code, COUNT(*) AS n
FROM   {s}runtimes r
JOIN   {s}jobs j ON j.id = r.job_id
JOIN   {s}definitions d ON d.id = j.definition_id
WHERE  r.status_code = 200
  AND  d.name NOT IN ('always-fails', 'at-most-once-charge')
GROUP  BY d.name, r.status_code;

-- `at-most-once-charge` is exempt for a different reason than `always-fails`, and the difference
-- matters. always-fails throws on purpose. at-most-once-charge fails only when a kill landed inside
-- its body: the AtMostOnce contract refuses to re-run it and terminalizes the ambiguity instead,
-- because for a charge an honest "this may have happened once" beats a confident second attempt.
-- Its real assertion is check 10, which is about how many times the body ran, not how it ended.

-- ---------------------------------------------------------------------------------------------
-- 6. terminal-state integrity                            [QUIESCED ONLY]
-- ---------------------------------------------------------------------------------------------
-- A job that reached a terminal status holds no execution lease.
SELECT 'terminal-integrity' AS check_name, r.job_id, r.status_code, r.leased_by_worker_id
FROM   {s}runtimes r
WHERE  r.status_code IN (100, 200, 220)
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
-- Counting, not timestamps. This check used to compare a note's time against the step's
-- `modified_at_utc` and read "note after success" as a replay. That is wrong under load: every
-- routine captures its clock at entry, so a write blocked on locks lands with a stale stamp, and a
-- million-job SQL Server run produced 2,208 rows where `modified_at_utc` preceded the row's own
-- `created_at_utc`. Every one of them had attempt_number = 1 and exactly one reasonMessage: nothing had
-- re-run. One note per admitted attempt is the same claim with no clock in it.
--
-- A second note is NOT a violation by itself: at-least-once means a body interrupted before its
-- outcome committed legitimately re-runs, and that re-run is an attempt the engine counted. The
-- violation is a body that ran without the engine admitting it, which is exactly notes > attempts.
SELECT 'step-replay' AS check_name, s.job_id, s.name AS step_name, s.attempt_number, n.notes
FROM   {s}steps s
JOIN   (SELECT job_id, reason_message, COUNT(*) AS notes
        FROM   {s}events
        WHERE  event_code = 90
        GROUP BY job_id, reason_message) n
  ON   n.job_id = s.job_id
 AND   n.reason_message = CONCAT('step-body ', s.name)   -- CONCAT: || is not SQL Server
WHERE  s.status_code = 100
  AND  n.notes > s.attempt_number;

-- ---------------------------------------------------------------------------------------------
-- 8. the chaos was real                                  [INVERTED: must return exactly one row]
-- ---------------------------------------------------------------------------------------------
-- A run in which nothing was reclaimed proves nothing, and would pass every check above trivially.
-- This is the guard against a green seal that means nothing.
--
-- Keys on ORPHANED ATTEMPTS, not on worker.died (122). Observed live: recovery reclaims a killed
-- worker's in-flight work on lease expiry without necessarily marking the worker row dead - 56
-- orphaned attempts accumulated while 122 stayed at zero. Requiring 122 would have failed a run
-- whose chaos was demonstrably real. It is reported as a number, never asserted.
--
-- Note what this implies about run length, with the real numbers: JobsOptions.LeaseTtlSeconds
-- defaults to 180 and sys.recovery fires once a minute, so a killed worker's job is not reclaimable
-- for up to ~4 minutes. A workload that drains faster than that reports zero reclaims no matter how
-- many workers were killed, and that is INCONCLUSIVE, not PASS. Size the run by duration against
-- that floor, never by job count: a 1,000-job crash run drains in ~3.5 minutes and can finish before
-- the first lease lapses.
SELECT 'chaos-was-real' AS check_name,
       SUM(CASE WHEN event_code = 41 AND execution_status_code = 230 THEN 1 ELSE 0 END) AS orphaned_attempts,
       SUM(CASE WHEN event_code = 122 THEN 1 ELSE 0 END) AS workers_marked_dead
FROM   {s}events
HAVING SUM(CASE WHEN event_code = 41 AND execution_status_code = 230 THEN 1 ELSE 0 END) > 0;

-- ---------------------------------------------------------------------------------------------
-- 9. how far the recorded clock ran backwards            [MEASURED: always reports, never fails]
-- ---------------------------------------------------------------------------------------------
-- `events.id` is assigned in insert order, so a higher id carrying an earlier `created_at_utc` means
-- the recorded time moved backwards between those two writes. Reported, never asserted, because two
-- causes are indistinguishable from here and only one is a fault: a routine captures its clock at
-- entry, so a write blocked on locks lands with a stale stamp and looks identical to a container
-- whose clock actually stepped back.
--
-- It is here because a run that certifies timing-dependent properties must show what its clock did.
-- The million-job SQL Server run recorded 26,713 backwards steps of up to 30 seconds, and without
-- this line the only symptom was a check failing for a reason it could not name.
SELECT 'clock-backsteps' AS check_name, COUNT(*) AS backwards_writes
FROM   (SELECT created_at_utc,
               LAG(created_at_utc) OVER (ORDER BY id) AS previous_created_at_utc
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
FROM   (SELECT job_id, COUNT(*) AS bodies
        FROM   {s}events
        WHERE  event_code = 90 AND reason_message = 'charge-body'
        GROUP  BY job_id) n
WHERE  n.bodies > 1;

-- ---------------------------------------------------------------------------------------------
-- 11. tenant context survived every hop                  [any time]
-- ---------------------------------------------------------------------------------------------
-- The tenant supplied at enqueue is the tenant the handler observed. Deliberately NOT a comparison of
-- events.tenant_id to jobs.tenant_id: those are two projections of one stored value, so that query
-- passes by construction and proves nothing. The handler notes the TenantKey it actually saw, which
-- is the far side of enqueue, claim, dispatch and payload decode, and this compares that against the
-- row the job was stored with.
--
-- Not a security claim. docs/guide/concepts.md states plainly that the tenant field is not a security
-- or isolation boundary; this asserts that the value survives intact, not that it confines anything.
SELECT 'tenant-context' AS check_name, e.job_id, e.reason_message AS observed, COALESCE(t.tenant_key, '-') AS job_tenant
FROM   {s}events e
JOIN   {s}jobs j ON j.id = e.job_id
LEFT   JOIN {s}tenants t ON t.id = j.tenant_id
WHERE  e.event_code = 90
  AND  e.reason_message LIKE 'tenant %'
  AND  e.reason_message <> CONCAT('tenant ', COALESCE(t.tenant_key, '-'));
