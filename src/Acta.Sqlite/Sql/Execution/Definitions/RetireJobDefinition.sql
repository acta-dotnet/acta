DROP TABLE IF EXISTS temp._retire_job_definition;

CREATE TEMP TABLE _retire_job_definition AS
SELECT jd.id, jd.namespace_id, jd.status_code, jd.version, jd.retention_seconds_effective
FROM {{schema}}.definitions jd
WHERE jd.id = @p_id;

DROP TABLE IF EXISTS temp._retire_parked_jobs;

-- Snapshot of the rows the sweep is about to cancel, taken before the UPDATE so the events can carry
-- the status each job came from. SQLite admits one writer at a time, so nothing can join or leave the
-- set between the snapshot and the UPDATE.
CREATE TEMP TABLE _retire_parked_jobs AS
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
    r.status_code AS from_status_code,
    r.lane_id
FROM {{schema}}.jobs j
JOIN {{schema}}.runtimes r ON r.job_id = j.id
WHERE
    j.definition_id IN (
        SELECT s.id
        FROM temp._retire_job_definition s
        WHERE s.version = @p_version AND s.status_code <> 240 /* JobDefinitionStatusCode.Retired */
    )
    AND r.status_code IN (
        10 /* JobStatusCode.Ready */, 15 /* JobStatusCode.Blocked */, 20 /* JobStatusCode.Suspended */, 30 /* JobStatusCode.Paused */
    );

UPDATE {{schema}}.definitions
SET
    status_code = 240 /* JobDefinitionStatusCode.Retired */,
    modified_at_utc = {{now}},
    version = version + 1
WHERE
    id = @p_id
    AND version = @p_version
    AND status_code <> 240 /* JobDefinitionStatusCode.Retired */;

UPDATE {{schema}}.runtimes
SET
    status_code = 220 /* JobStatusCode.Cancelled */,
    leased_by_worker_id = NULL,
    lease_expires_at_utc = NULL,
    retention_until_utc = {{now}} + (SELECT s.retention_seconds_effective FROM temp._retire_job_definition s) * 1000,
    modified_at_utc = {{now}},
    version = version + 1
WHERE job_id IN (SELECT p.job_id FROM temp._retire_parked_jobs p);

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
    {{now}},
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
FROM temp._retire_parked_jobs p
WHERE p.audit_level_code = 20 /* JobAuditLevelCode.Audit */;

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
    {{now}},
    s.namespace_id,
    @p_actor_code,
    @p_actor_key,
    NULL,
    NULL,
    NULL,
    NULL,
    s.id,
    NULL,
    NULL,
    NULL,
    NULL,
    NULL,
    NULL,
    @p_reason_code,
    @p_reason_message
FROM temp._retire_job_definition s
WHERE s.version = @p_version AND s.status_code <> 240 /* JobDefinitionStatusCode.Retired */;

-- Once every parked member is cancelled, each of their lanes hands on to its lowest-id unfinished
-- member if that member is Blocked and no member still runs. The immediate transaction is the lanes'
-- mutex on SQLite, and this is the last write, so the final SELECT's changes() counts its promotions.
UPDATE {{schema}}.runtimes
SET
    status_code = 10 /* JobStatusCode.Ready */,
    next_run_at_utc = MAX(next_run_at_utc, {{now}}),
    modified_at_utc = {{now}},
    version = version + 1
WHERE
    status_code = 15 /* JobStatusCode.Blocked */
    AND NOT EXISTS (
        SELECT 1
        FROM {{schema}}.runtimes o
        WHERE
            o.lane_id = runtimes.lane_id
            AND o.lane_id IS NOT NULL
            AND o.status_code IN (
                10 /* JobStatusCode.Ready */, 20 /* JobStatusCode.Suspended */,
                40 /* JobStatusCode.Dispatched */, 50 /* JobStatusCode.Executing */
            )
    )
    AND job_id IN (
        SELECT (
            SELECT m.job_id
            FROM {{schema}}.runtimes m
            WHERE
                m.lane_id = l.lane_id
                AND m.lane_id IS NOT NULL
                AND m.status_code IN (
                    10 /* JobStatusCode.Ready */, 15 /* JobStatusCode.Blocked */, 20 /* JobStatusCode.Suspended */,
                    30 /* JobStatusCode.Paused */, 40 /* JobStatusCode.Dispatched */, 50 /* JobStatusCode.Executing */
                )
            ORDER BY m.job_id
            LIMIT 1
        )
        FROM (SELECT DISTINCT p.lane_id FROM temp._retire_parked_jobs p WHERE p.lane_id IS NOT NULL) l
    );

SELECT
    CASE
        WHEN s.id IS NULL THEN 2 /* DefinitionOverrideAction.NotFound */
        WHEN s.version <> @p_version THEN 3 /* DefinitionOverrideAction.VersionConflict */
        ELSE 1 /* DefinitionOverrideAction.Applied */
    END AS action,
    p.job_id,
    p.parent_id,
    CASE WHEN changes() > 0 THEN 1 ELSE 0 END AS lane_promoted
FROM (SELECT @p_id AS qid) q
LEFT JOIN temp._retire_job_definition s ON s.id = q.qid
LEFT JOIN temp._retire_parked_jobs p ON 1 = 1;
