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
--   JobEventCode         40=job.execution-started 41=job.execution-finished 122=worker.dead
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
-- Still absent, and not claimed anywhere below: handler-body overlap (needs enter/exit notes plus an
-- external kill record with the killer's timestamp) and AtMostOnce double-spend. Nothing here says
-- "lease exclusivity"; see check 1 for what is actually proven instead.

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
-- 2. attempt pairing                                     [QUIESCED ONLY]
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
SELECT 'attempt-pairing' AS check_name, job_id, execution_number,
       SUM(CASE WHEN event_code = 40 THEN 1 ELSE 0 END) AS started,
       SUM(CASE WHEN event_code = 41 THEN 1 ELSE 0 END) AS finished
FROM   {s}events
WHERE  event_code IN (40, 41) AND execution_number IS NOT NULL
GROUP  BY job_id, execution_number
HAVING SUM(CASE WHEN event_code = 40 THEN 1 ELSE 0 END) > 1
    OR SUM(CASE WHEN event_code = 41 THEN 1 ELSE 0 END) <> 1;

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
  AND  d.name <> 'always-fails'
GROUP  BY d.name, r.status_code;

-- ---------------------------------------------------------------------------------------------
-- 6. terminal-state integrity                            [QUIESCED ONLY]
-- ---------------------------------------------------------------------------------------------
-- A job that reached a terminal status holds no execution lease.
SELECT 'terminal-integrity' AS check_name, r.job_id, r.status_code, r.leased_by_worker_id
FROM   {s}runtimes r
WHERE  r.status_code IN (100, 200, 220)
  AND  r.leased_by_worker_id IS NOT NULL;

-- ---------------------------------------------------------------------------------------------
-- 7. no step body ran after its result was recorded      [QUIESCED ONLY]
-- ---------------------------------------------------------------------------------------------
-- The differentiating claim, and the reason the note API exists: "kill the worker, completed steps
-- do not re-run."
--
-- `steps` keeps only current state - one row per (job, name), UPDATE-in-place, no per-attempt
-- history - so a body that ran twice leaves no trace there. The witness is the note each body writes
-- before doing its work.
--
-- A second note is NOT a violation by itself: at-least-once means a body interrupted before its
-- outcome committed legitimately re-runs. The violation is a body that ran *after* the step already
-- succeeded, which is what this finds by comparing note timestamps to the step's completion.
SELECT 'step-replay' AS check_name, s.job_id, s.name AS step_name, e.created_at_utc AS note_after_success
FROM   {s}steps s
JOIN   {s}events e
  ON   e.job_id = s.job_id
 AND   e.event_code = 90
 AND   e.reason_message = CONCAT('step-body ', s.name)   -- CONCAT: || is not SQL Server
WHERE  s.status_code = 100
  AND  e.created_at_utc > s.modified_at_utc;

-- ---------------------------------------------------------------------------------------------
-- 8. the chaos was real                                  [INVERTED: must return exactly one row]
-- ---------------------------------------------------------------------------------------------
-- A run in which nothing was reclaimed proves nothing, and would pass every check above trivially.
-- This is the guard against a green seal that means nothing.
--
-- Keys on ORPHANED ATTEMPTS, not on worker.dead (122). Observed live: recovery reclaims a killed
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
