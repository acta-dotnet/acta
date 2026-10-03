-- One batch, one implicit transaction. Only the final SELECT returns rows; every earlier statement
-- writes temp tables, so each later statement reads what the earlier ones did under the locks.
CREATE TEMP TABLE IF NOT EXISTS _retire_lanes (id BIGINT PRIMARY KEY) ON COMMIT DROP;
CREATE TEMP TABLE IF NOT EXISTS _retire_action (action SMALLINT NOT NULL) ON COMMIT DROP;
CREATE TEMP TABLE IF NOT EXISTS _retire_cancelled (job_id BIGINT NOT NULL, parent_id BIGINT) ON COMMIT DROP;
CREATE TEMP TABLE IF NOT EXISTS _retire_promoted (promoted INT NOT NULL) ON COMMIT DROP;

-- The lanes of the definition's parked laned jobs, locked in id order before any job row
-- (docs/internals/sql-execution-policy.md, "Lane lock order").
INSERT INTO _retire_lanes (id)
SELECT l.id
FROM {{schema}}.lanes l
WHERE l.id IN (
    SELECT r.lane_id
    FROM {{schema}}.runtimes r
    INNER JOIN {{schema}}.jobs j ON j.id = r.job_id
    WHERE
        j.definition_id = @p_id
        AND r.lane_id IS NOT NULL
        AND r.status_code IN (
            10 /* JobStatusCode.Ready */, 15 /* JobStatusCode.Blocked */, 20 /* JobStatusCode.Suspended */, 30 /* JobStatusCode.Paused */
        )
)
ORDER BY l.id
FOR UPDATE OF l;

WITH target AS (
    SELECT jd.id, jd.namespace_id, jd.status_code, jd.version
    FROM {{schema}}.definitions jd
    WHERE jd.id = @p_id
    FOR UPDATE
),
retired AS (
    UPDATE {{schema}}.definitions jd
    SET
        status_code = 240 /* JobDefinitionStatusCode.Retired */,
        modified_at_utc = now(),
        version = jd.version + 1
    FROM target t
    WHERE
        jd.id = t.id
        AND t.version = @p_version
        AND t.status_code <> 240 /* JobDefinitionStatusCode.Retired */
    RETURNING jd.id, jd.namespace_id
),
-- A laned job is parked here only in a lane locked above; Blocked is parked too, and an in-flight
-- attempt is left to finish.
parked AS (
    SELECT
        j.id AS job_id,
        j.job_ref,
        j.parent_id,
        j.namespace_id,
        j.tenant_id,
        j.definition_id,
        j.audit_level_code,
        COALESCE(j.lineage_root_id, j.id) AS lineage_root_id,
        r.execution_number,
        r.status_code AS from_status_code
    FROM {{schema}}.jobs j
    INNER JOIN {{schema}}.runtimes r ON r.job_id = j.id
    WHERE
        j.definition_id IN (SELECT d.id FROM retired d)
        AND r.status_code IN (
            10 /* JobStatusCode.Ready */, 15 /* JobStatusCode.Blocked */, 20 /* JobStatusCode.Suspended */, 30 /* JobStatusCode.Paused */
        )
        AND (r.lane_id IS NULL OR r.lane_id IN (SELECT rl.id FROM _retire_lanes rl))
    -- Locked here so the from_status the event records is the status the sweep below moves.
    FOR UPDATE OF r
),
swept AS (
    UPDATE {{schema}}.runtimes r
    SET
        status_code = 220 /* JobStatusCode.Cancelled */,
        leased_by_worker_id = NULL,
        lease_expires_at_utc = NULL,
        retention_until_utc = now() + make_interval(secs => jd.retention_seconds_effective),
        modified_at_utc = now(),
        version = r.version + 1
    FROM parked p
    INNER JOIN {{schema}}.definitions jd ON jd.id = p.definition_id
    WHERE
        r.job_id = p.job_id
        AND r.status_code IN (
            10 /* JobStatusCode.Ready */, 15 /* JobStatusCode.Blocked */, 20 /* JobStatusCode.Suspended */, 30 /* JobStatusCode.Paused */
        )
    RETURNING r.job_id
),
cancel_events AS (
    INSERT INTO {{schema}}.events (
        event_code,
        created_at_utc,
        namespace_id,
        actor_code,
        actor_key,
        job_id,
        job_ref,
        execution_number,
        lineage_root_id,
        definition_id,
        tenant_id,
        worker_id,
        from_status_code,
        to_status_code,
        execution_status_code,
        duration_ms,
        reason_code,
        reason_message)
    SELECT
        70 /* EventCode.JobCancelled */,
        now(),
        p.namespace_id,
        @p_actor_code,
        @p_actor_key,
        p.job_id,
        p.job_ref,
        p.execution_number,
        p.lineage_root_id,
        p.definition_id,
        p.tenant_id,
        NULL,
        p.from_status_code,
        220 /* JobStatusCode.Cancelled */,
        NULL,
        NULL,
        42 /* JobEventReasonCode.JobDefinitionRetired */,
        @p_reason_message
    FROM parked p
    INNER JOIN swept s ON s.job_id = p.job_id
    WHERE p.audit_level_code = 20 /* JobAuditLevelCode.Audit */
),
definition_event AS (
    INSERT INTO {{schema}}.events (
        event_code,
        created_at_utc,
        namespace_id,
        actor_code,
        actor_key,
        job_id,
        job_ref,
        execution_number,
        lineage_root_id,
        definition_id,
        tenant_id,
        worker_id,
        from_status_code,
        to_status_code,
        execution_status_code,
        duration_ms,
        reason_code,
        reason_message)
    SELECT
        31 /* EventCode.JobDefinitionRetired */,
        now(),
        d.namespace_id,
        @p_actor_code,
        @p_actor_key,
        NULL,
        NULL,
        NULL,
        NULL,
        d.id,
        NULL,
        NULL,
        NULL,
        NULL,
        NULL,
        NULL,
        @p_reason_code,
        @p_reason_message
    FROM retired d
),
action_row AS (
    INSERT INTO _retire_action (action)
    SELECT
        CASE
            WHEN t.id IS NULL THEN 2 /* DefinitionOverrideAction.NotFound */
            WHEN t.version <> @p_version THEN 3 /* DefinitionOverrideAction.VersionConflict */
            ELSE 1 /* DefinitionOverrideAction.Applied */
        END
    FROM (SELECT 1 AS probe) q
    LEFT JOIN target t ON TRUE
)
INSERT INTO _retire_cancelled (job_id, parent_id)
SELECT p.job_id, p.parent_id
FROM parked p
INNER JOIN swept s ON s.job_id = p.job_id;

-- Once every parked member is cancelled, each locked lane hands on to its lowest-id unfinished member
-- if that member is Blocked and no member still runs, so no member of the retired definition is promoted.
WITH promoted AS (
    UPDATE {{schema}}.runtimes pr
    SET
        status_code = 10 /* JobStatusCode.Ready */,
        next_run_at_utc = GREATEST(pr.next_run_at_utc, now()),
        modified_at_utc = now(),
        version = pr.version + 1
    WHERE
        pr.status_code = 15 /* JobStatusCode.Blocked */
        AND NOT EXISTS (
            SELECT 1
            FROM {{schema}}.runtimes o
            WHERE
                o.lane_id = pr.lane_id
                AND o.lane_id IS NOT NULL
                AND o.status_code IN (
                    10 /* JobStatusCode.Ready */, 20 /* JobStatusCode.Suspended */,
                    40 /* JobStatusCode.Dispatched */, 50 /* JobStatusCode.Executing */
                )
        )
        AND pr.job_id IN (
            SELECT (
                SELECT m.job_id
                FROM {{schema}}.runtimes m
                WHERE
                    m.lane_id = rl.id
                    AND m.lane_id IS NOT NULL
                    AND m.status_code IN (
                        10 /* JobStatusCode.Ready */, 15 /* JobStatusCode.Blocked */, 20 /* JobStatusCode.Suspended */,
                        30 /* JobStatusCode.Paused */, 40 /* JobStatusCode.Dispatched */, 50 /* JobStatusCode.Executing */
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
    c.job_id,
    c.parent_id,
    (SELECT p.promoted > 0 FROM _retire_promoted p) AS lane_promoted
FROM _retire_action a
LEFT JOIN _retire_cancelled c ON TRUE;
