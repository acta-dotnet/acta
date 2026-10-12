-- One batch, one implicit transaction; only the final SELECT returns rows. Lock order: the lane, then the
-- job's rows (docs/internals/sql-execution-policy.md, "Lane lock order"); lane_id never changes, so the
-- unlocked read that finds it is safe.
DROP TABLE IF EXISTS pg_temp._restart_job_lane;
DROP TABLE IF EXISTS pg_temp._restart_job;
CREATE TEMP TABLE _restart_job_lane (id BIGINT NOT NULL) ON COMMIT DROP;
CREATE TEMP TABLE _restart_job (
    from_status SMALLINT NOT NULL,
    from_version INT NOT NULL,
    lane_id BIGINT,
    parent_id BIGINT,
    namespace_id INT NOT NULL,
    lineage_root_id BIGINT,
    definition_id INT NOT NULL,
    tenant_id INT,
    execution_number INT NOT NULL,
    audit_level_code SMALLINT NOT NULL,
    job_ref UUID NOT NULL,
    action SMALLINT NOT NULL,
    to_status SMALLINT NOT NULL
) ON COMMIT DROP;

INSERT INTO _restart_job_lane (id)
SELECT l.id
FROM {{schema}}.lanes l
WHERE l.id = (SELECT r.lane_id FROM {{schema}}.runtimes r WHERE r.job_id = @p_id)
FOR UPDATE;

INSERT INTO _restart_job (
    from_status, from_version, lane_id, parent_id, namespace_id, lineage_root_id, definition_id, tenant_id,
    execution_number, audit_level_code, job_ref, action, to_status)
SELECT
    r.status_code, r.version, r.lane_id, j.parent_id, j.namespace_id, j.lineage_root_id, j.definition_id, j.tenant_id,
    r.execution_number, j.audit_level_code, j.job_ref,
    CASE
        WHEN @p_expected_version IS NOT NULL AND r.version <> @p_expected_version THEN 5 /* ControlAction.VersionConflict */
        WHEN r.status_code = 50 /* JobStatusCode.Executing */ THEN 3 /* ControlAction.Rejected */
        ELSE 1 /* ControlAction.Applied */
    END,
    10 /* JobStatusCode.Ready */
FROM {{schema}}.runtimes r
JOIN {{schema}}.jobs j ON j.id = r.job_id
WHERE r.job_id = @p_id
FOR UPDATE OF r;

-- A finished laned job is not reactivated while an unfinished ancestor or descendant sits in its lane, the
-- state the enqueue ancestor guard refuses.
UPDATE _restart_job s
SET action = 3 /* ControlAction.Rejected */
WHERE
    s.action = 1 /* ControlAction.Applied */
    AND s.lane_id IS NOT NULL
    AND s.from_status IN (100 /* JobStatusCode.Succeeded */, 200 /* JobStatusCode.Failed */, 220 /* JobStatusCode.Cancelled */)
    AND EXISTS (
        WITH RECURSIVE
        ancestors AS (
            SELECT a.id, a.parent_id
            FROM {{schema}}.jobs a
            WHERE a.id = (SELECT x.parent_id FROM _restart_job x)
            UNION ALL
            SELECT a.id, a.parent_id
            FROM {{schema}}.jobs a
            INNER JOIN ancestors c ON a.id = c.parent_id
        ),
        descendants AS (
            SELECT d.id
            FROM {{schema}}.jobs d
            WHERE d.parent_id = @p_id
            UNION ALL
            SELECT d.id
            FROM {{schema}}.jobs d
            INNER JOIN descendants c ON d.parent_id = c.id
        ),
        lineage AS (
            SELECT a.id FROM ancestors a
            UNION ALL
            SELECT d.id FROM descendants d
        )
        SELECT 1
        FROM lineage c
        INNER JOIN {{schema}}.runtimes lr ON lr.job_id = c.id
        WHERE
            lr.lane_id = (SELECT x.lane_id FROM _restart_job x)
            AND lr.status_code IN (
                10 /* JobStatusCode.Ready */, 15 /* JobStatusCode.Blocked */, 20 /* JobStatusCode.Suspended */,
                30 /* JobStatusCode.Paused */, 50 /* JobStatusCode.Executing */
            )
    );

-- A laned job restarts Blocked while another member runs, or when it was not running and an older member is
-- unfinished.
UPDATE _restart_job s
SET to_status = 15 /* JobStatusCode.Blocked */
WHERE
    s.action = 1 /* ControlAction.Applied */
    AND s.lane_id IS NOT NULL
    AND EXISTS (
        SELECT 1
        FROM {{schema}}.runtimes o
        WHERE
            o.lane_id = s.lane_id
            AND o.lane_id IS NOT NULL
            AND o.status_code IN (
                10 /* JobStatusCode.Ready */, 15 /* JobStatusCode.Blocked */, 20 /* JobStatusCode.Suspended */,
                30 /* JobStatusCode.Paused */, 50 /* JobStatusCode.Executing */
            )
            AND o.job_id <> @p_id
            AND (
                o.status_code IN (
                    10 /* JobStatusCode.Ready */, 20 /* JobStatusCode.Suspended */,
                    50 /* JobStatusCode.Executing */
                )
                OR (
                    s.from_status NOT IN (10 /* JobStatusCode.Ready */, 20 /* JobStatusCode.Suspended */)
                    AND o.job_id < @p_id
                )
            )
    );

UPDATE {{schema}}.runtimes AS r
SET
    status_code = s.to_status,
    failure_count = 0,
    next_run_at_utc = COALESCE(@p_next_run_at_utc, now()),
    leased_by_worker_id = NULL,
    lease_expires_at_utc = NULL,
    retention_until_utc = NULL,
    modified_at_utc = now(),
    version = r.version + 1
FROM _restart_job s
WHERE r.job_id = @p_id AND s.action = 1 /* ControlAction.Applied */;

-- An exhausted step runs again from a fresh budget and retry window; an interrupted at-most-once step stays,
-- since only someone who reconciled its outcome can say it may run again.
DELETE FROM {{schema}}.steps st
WHERE
    st.job_id = @p_id
    AND st.status_code = 200 /* StepStatusCode.Exhausted */
    AND EXISTS (SELECT 1 FROM _restart_job s WHERE s.action = 1 /* ControlAction.Applied */);

INSERT INTO {{schema}}.events (
    event_code, created_at_utc, namespace_id, actor_code, actor, job_id, job_ref, execution_number,
    lineage_root_id, definition_id, tenant_id, from_status_code, to_status_code, reason_code, reason_message)
SELECT
    73 /* EventCode.JobRestarted */, now(), s.namespace_id, @p_actor_code, @p_actor, @p_id, s.job_ref,
    s.execution_number, COALESCE(s.lineage_root_id, @p_id), s.definition_id, s.tenant_id, s.from_status, s.to_status,
    @p_reason_code, @p_reason_message
FROM _restart_job s
WHERE s.action = 1 /* ControlAction.Applied */ AND s.audit_level_code = 20 /* DefinitionAuditLevelCode.Audit */;

SELECT
    COALESCE(s.action, 2 /* ControlAction.NotFound */)::SMALLINT AS action,
    CASE WHEN s.action = 1 /* ControlAction.Applied */ THEN s.to_status ELSE s.from_status END AS status_code,
    CASE WHEN s.action = 1 /* ControlAction.Applied */ THEN s.from_version + 1 ELSE s.from_version END AS version
FROM (SELECT 1) one
LEFT JOIN _restart_job s ON true;
