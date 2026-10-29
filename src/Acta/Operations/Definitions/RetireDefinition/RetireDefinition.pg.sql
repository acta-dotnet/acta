-- One batch, one implicit transaction. Only the final SELECT returns rows; every earlier statement
-- writes temp tables, so each later statement reads what the earlier ones did under the locks.
CREATE TEMP TABLE IF NOT EXISTS _retire_lanes (lane_id BIGINT PRIMARY KEY) ON COMMIT DROP;
CREATE TEMP TABLE IF NOT EXISTS _retire_action (action SMALLINT NOT NULL, version INT) ON COMMIT DROP;
CREATE TEMP TABLE IF NOT EXISTS _retire_cancelled (job_id BIGINT NOT NULL, parent_job_id BIGINT) ON COMMIT DROP;
CREATE TEMP TABLE IF NOT EXISTS _retire_promoted (promoted INT NOT NULL) ON COMMIT DROP;
CREATE TEMP TABLE IF NOT EXISTS _retire_lock (granted INT NOT NULL) ON COMMIT DROP;

-- The namespace's schedule lock, exclusive, before any row lock (docs/sql-internals.md, "Schedule lock").
INSERT INTO _retire_lock (granted)
SELECT 1
FROM (
    SELECT pg_advisory_xact_lock(hashtext('{{schema}}.schedules'), jd.namespace_id)
    FROM {{schema}}.definitions jd
    WHERE jd.definition_id = @p_definition_id
) l;

-- The lanes of the definition's parked laned jobs, locked in id order before any job row
-- (docs/sql-internals.md, "Lane lock order").
INSERT INTO _retire_lanes (lane_id)
SELECT l.lane_id
FROM {{schema}}.lanes l
WHERE l.lane_id IN (
    SELECT r.lane_id
    FROM {{schema}}.jobs_state r
    INNER JOIN {{schema}}.jobs j ON j.job_id = r.job_id
    WHERE
        j.definition_id = @p_definition_id
        AND r.lane_id IS NOT NULL
        AND r.job_status_code IN (
            10 /* JobStatusCode.Ready */, 15 /* JobStatusCode.Blocked */, 20 /* JobStatusCode.Suspended */, 30 /* JobStatusCode.Paused */, 40 /* JobStatusCode.Idle */
        )
)
ORDER BY l.lane_id
FOR UPDATE OF l;

WITH target AS (
    SELECT jd.definition_id, jd.namespace_id, jd.definition_status_code, jd.version
    FROM {{schema}}.definitions jd
    WHERE jd.definition_id = @p_definition_id
    FOR UPDATE
),
retired AS (
    UPDATE {{schema}}.definitions jd
    SET
        definition_status_code = 240 /* DefinitionStatusCode.Retired */,
        modified_at_utc = now(),
        version = jd.version + 1
    FROM target t
    WHERE
        jd.definition_id = t.definition_id
        AND t.version = @p_version
        AND t.definition_status_code <> 240 /* DefinitionStatusCode.Retired */
    RETURNING jd.definition_id, jd.namespace_id, jd.version
),
-- A laned job is parked here only in a lane locked above; Blocked is parked too, and an in-flight
-- attempt is left to finish.
parked AS (
    SELECT
        j.job_id,
        j.parent_job_id,
        j.namespace_id,
        j.tenant_id,
        j.definition_id,
        j.event_recording_code,
        COALESCE(j.lineage_root_job_id, j.job_id) AS lineage_root_job_id,
        r.execution_number
    FROM {{schema}}.jobs j
    INNER JOIN {{schema}}.jobs_state r ON r.job_id = j.job_id
    WHERE
        j.definition_id IN (SELECT d.definition_id FROM retired d)
        AND r.job_status_code IN (
            10 /* JobStatusCode.Ready */, 15 /* JobStatusCode.Blocked */, 20 /* JobStatusCode.Suspended */, 30 /* JobStatusCode.Paused */, 40 /* JobStatusCode.Idle */
        )
        AND (r.lane_id IS NULL OR r.lane_id IN (SELECT rl.lane_id FROM _retire_lanes rl))
    -- Locked here so the execution_number each event records is the one the sweep below cancels.
    FOR UPDATE OF r
),
swept AS (
    UPDATE {{schema}}.jobs_state r
    SET
        job_status_code = 220 /* JobStatusCode.Cancelled */,
        leased_by_worker_id = NULL,
        lease_expires_at_utc = NULL,
        retention_until_utc = now() + make_interval(secs => jd.retention_seconds_effective),
        modified_at_utc = now(),
        version = r.version + 1
    FROM parked p
    INNER JOIN {{schema}}.definitions jd ON jd.definition_id = p.definition_id
    WHERE
        r.job_id = p.job_id
        AND r.job_status_code IN (
            10 /* JobStatusCode.Ready */, 15 /* JobStatusCode.Blocked */, 20 /* JobStatusCode.Suspended */, 30 /* JobStatusCode.Paused */, 40 /* JobStatusCode.Idle */
        )
    RETURNING r.job_id
),
-- A retired slot's outstanding firing settles with it.
settled AS (
    UPDATE {{schema}}.checkpoints cp
    SET
        checkpoint_status_code = 130 /* CheckpointStatusCode.Cancelled */,
        modified_at_utc = now(),
        version = cp.version + 1
    FROM parked p
    WHERE
        cp.job_id = p.job_id
        AND cp.checkpoint_kind_code = 60 /* CheckpointKindCode.Firing */
        AND cp.checkpoint_name = 'sys.firing'
        AND cp.checkpoint_status_code = 10 /* CheckpointStatusCode.Pending */
),
-- One write per state row, since a row updated twice in one statement keeps only one of the updates: the exhaustion,
-- and the capacity every cancelled charged root releases, so the count stays true on the exhausted schedule.
cleared AS (
    UPDATE {{schema}}.schedules_state st
    SET
        next_occurrence_at_utc = NULL,

        next_evaluation_at_utc = NULL,

        evaluation_status_code = 30 /* EvaluationStatusCode.Exhausted */,

        outstanding_count = st.outstanding_count - COALESCE(c.settled, 0),

        version = st.version + 1,
        modified_at_utc = now()
    FROM {{schema}}.schedules s
    INNER JOIN retired d ON d.definition_id = s.definition_id
    LEFT JOIN (
        SELECT j.schedule_id, COUNT(*) AS settled
        FROM parked p
        INNER JOIN {{schema}}.jobs j ON j.job_id = p.job_id
        WHERE j.schedule_id IS NOT NULL
        GROUP BY j.schedule_id
    ) c ON c.schedule_id = s.schedule_id
    WHERE
        st.schedule_id = s.schedule_id
),
cancel_events AS (
    INSERT INTO {{schema}}.events (
        event_code,
        created_at_utc,
        namespace_id,
        entity_code,
        entity_id,
        definition_id,
        tenant_id,
        lineage_root_job_id,
        execution_number,
        event_message,
        details_format_id,
        details)
    SELECT
        70 /* EventCode.JobCancelled */,
        now(),
        p.namespace_id,
        50 /* EntityCode.Job */,
        p.job_id,
        p.definition_id,
        p.tenant_id,
        p.lineage_root_job_id,
        p.execution_number,
        @p_event_message,
        1 /* JobPayloadFormat.Json */,
        @p_job_details
    FROM parked p
    INNER JOIN swept s ON s.job_id = p.job_id
    WHERE p.event_recording_code = 20 /* EventRecordingCode.Audit */
),
definition_event AS (
    INSERT INTO {{schema}}.events (
        event_code,
        created_at_utc,
        namespace_id,
        entity_code,
        entity_id,
        definition_id,
        event_message,
        details_format_id,
        details)
    SELECT
        31 /* EventCode.JobDefinitionRetired */,
        now(),
        d.namespace_id,
        30 /* EntityCode.Definition */,
        d.definition_id,
        d.definition_id,
        @p_event_message,
        @p_details_format_id,
        @p_details
    FROM retired d
),
action_row AS (
    INSERT INTO _retire_action (action, version)
    SELECT
        CASE
            WHEN t.definition_id IS NULL THEN 2 /* DefinitionOverrideAction.NotFound */
            WHEN t.version <> @p_version THEN 3 /* DefinitionOverrideAction.VersionConflict */
            ELSE 1 /* DefinitionOverrideAction.Applied */
        END,
        COALESCE(d.version, t.version)
    FROM (SELECT 1 AS probe) q
    LEFT JOIN target t ON TRUE
    LEFT JOIN retired d ON TRUE
)
INSERT INTO _retire_cancelled (job_id, parent_job_id)
SELECT p.job_id, p.parent_job_id
FROM parked p
INNER JOIN swept s ON s.job_id = p.job_id;

-- Once every parked member is cancelled, each locked lane hands on to its lowest-id unfinished member
-- if that member is Blocked and no member still runs, so no member of the retired definition is promoted.
WITH promoted AS (
    UPDATE {{schema}}.jobs_state pr
    SET
        job_status_code = 10 /* JobStatusCode.Ready */,
        next_run_at_utc = GREATEST(pr.next_run_at_utc, now()),
        modified_at_utc = now(),
        version = pr.version + 1
    WHERE
        pr.job_status_code = 15 /* JobStatusCode.Blocked */
        AND NOT EXISTS (
            SELECT 1
            FROM {{schema}}.jobs_state o
            WHERE
                o.lane_id = pr.lane_id
                AND o.lane_id IS NOT NULL
                AND o.job_status_code IN (
                    10 /* JobStatusCode.Ready */, 20 /* JobStatusCode.Suspended */,
                    50 /* JobStatusCode.Executing */
                )
        )
        AND pr.job_id IN (
            SELECT (
                SELECT m.job_id
                FROM {{schema}}.jobs_state m
                WHERE
                    m.lane_id = rl.lane_id
                    AND m.lane_id IS NOT NULL
                    AND m.job_status_code IN (
                        10 /* JobStatusCode.Ready */, 15 /* JobStatusCode.Blocked */, 20 /* JobStatusCode.Suspended */,
                        30 /* JobStatusCode.Paused */, 50 /* JobStatusCode.Executing */
                    )
                ORDER BY m.job_id
                LIMIT 1
            )
            FROM _retire_lanes rl
        )
    RETURNING 1
)
INSERT INTO _retire_promoted (promoted)
SELECT COUNT(*) FROM promoted;

SELECT
    a.action,
    a.version,
    c.job_id,
    c.parent_job_id,
    (SELECT p.promoted > 0 FROM _retire_promoted p) AS lane_promoted
FROM _retire_action a
LEFT JOIN _retire_cancelled c ON TRUE;
