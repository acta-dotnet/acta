DROP TABLE IF EXISTS temp._purge_job;

CREATE TEMP TABLE _purge_job AS
SELECT
    j.job_id,
    r.job_status_code AS from_status,
    j.namespace_id,
    j.definition_id,
    j.tenant_id,
    j.lineage_root_job_id,
    -- Held by lineage: a child row still points here, or this is a completed child of a live parent,
    -- whose replay dedupes onto this row and reads its result.
    CASE WHEN EXISTS (SELECT 1 FROM {{schema}}.jobs c WHERE c.parent_job_id = j.job_id)
        OR EXISTS (
            SELECT 1 FROM {{schema}}.jobs_state p
            WHERE p.job_id = j.parent_job_id
              AND p.job_status_code NOT IN (100 /* JobStatusCode.Succeeded */, 200 /* JobStatusCode.Failed */, 220 /* JobStatusCode.Cancelled */))
        THEN 1 ELSE 0 END AS lineage_hold,
    -- A finished job may go; a permanent slot only with no firing outstanding and no live schedule pinning it, the
    -- deliberate decommission of a slot whose schedules are all orphaned or whose definition is retired.
    CASE
        WHEN j.job_kind_code <> 20 /* JobKindCode.PermanentSlot */
            THEN CASE WHEN r.job_status_code IN (100 /* JobStatusCode.Succeeded */, 200 /* JobStatusCode.Failed */, 220 /* JobStatusCode.Cancelled */) THEN 1 ELSE 0 END
        WHEN r.job_status_code NOT IN (40 /* JobStatusCode.Idle */, 200 /* JobStatusCode.Failed */, 220 /* JobStatusCode.Cancelled */) THEN 0
        WHEN EXISTS (
            SELECT 1
            FROM {{schema}}.checkpoints cp
            WHERE cp.job_id = j.job_id AND cp.checkpoint_kind_code = 60 /* CheckpointKindCode.Firing */
                AND cp.checkpoint_name = 'sys.firing' AND cp.checkpoint_status_code = 10 /* CheckpointStatusCode.Pending */
        ) THEN 0
        WHEN EXISTS (
            SELECT 1
            FROM {{schema}}.schedules sc
            JOIN {{schema}}.definitions d ON d.definition_id = sc.definition_id
            WHERE sc.job_id = j.job_id AND sc.schedule_status_code <> 230 /* ScheduleStatusCode.Orphaned */
                AND d.definition_status_code <> 240 /* DefinitionStatusCode.Retired */
        ) THEN 0
        ELSE 1
    END AS eligible
FROM {{schema}}.jobs j
JOIN {{schema}}.jobs_state r ON r.job_id = j.job_id
WHERE j.job_id = @p_job_id;

/* A purgeable job the alert projector still owes an event is refused: an event it reads, of an alert-enabled
   definition, past the floor, whose raise reads the job row. */
SELECT ACTA_ERROR('ACTA:PURGE_BLOCKED:purge_job: the alert projector has not processed this job''s events')
FROM temp._purge_job s
WHERE
    s.eligible = 1
    AND s.lineage_hold = 0
    AND (
        EXISTS (
            SELECT 1
            FROM {{schema}}.events e
            JOIN {{schema}}.definitions d ON d.definition_id = e.definition_id
            WHERE
                e.entity_code = 50 /* EntityCode.Job */
                AND e.entity_id = s.job_id
                AND e.created_at_utc >= @p_alert_floor_utc
                AND (e.created_at_utc > @p_alert_floor_utc OR e.event_id > @p_alert_floor_event_id)
                AND (
                    e.event_code IN (41 /* EventCode.JobExecutionFailed */, 42 /* EventCode.JobExecutionOrphaned */, 44 /* EventCode.JobFailed */)
                    OR (e.event_code = 40 /* EventCode.JobExecutionSucceeded */ AND e.execution_number > 1)
                )
                AND d.alert_profile_code_effective <> 0 /* AlertProfileCode.None */)
    );

DELETE FROM {{schema}}.tags
WHERE
    entity_code = 50 /* EntityCode.Job */
    AND entity_id = @p_job_id
    AND EXISTS (SELECT 1 FROM temp._purge_job s WHERE s.eligible = 1 AND s.lineage_hold = 0);

DELETE FROM {{schema}}.events
WHERE
    entity_code = 50 /* EntityCode.Job */
    AND entity_id = @p_job_id
    AND EXISTS (SELECT 1 FROM temp._purge_job s WHERE s.job_id = @p_job_id AND s.eligible = 1 AND s.lineage_hold = 0);

DELETE FROM {{schema}}.alerts
WHERE
    job_id = @p_job_id
    AND EXISTS (SELECT 1 FROM temp._purge_job s WHERE s.job_id = @p_job_id AND s.eligible = 1 AND s.lineage_hold = 0);

DELETE FROM {{schema}}.checkpoints
WHERE
    job_id = @p_job_id
    AND EXISTS (SELECT 1 FROM temp._purge_job s WHERE s.job_id = @p_job_id AND s.eligible = 1 AND s.lineage_hold = 0);

DELETE FROM {{schema}}.steps
WHERE
    job_id = @p_job_id
    AND EXISTS (SELECT 1 FROM temp._purge_job s WHERE s.job_id = @p_job_id AND s.eligible = 1 AND s.lineage_hold = 0);

DELETE FROM {{schema}}.schedules_state
WHERE
    schedule_id IN (SELECT sc.schedule_id FROM {{schema}}.schedules sc WHERE sc.job_id = @p_job_id)
    AND EXISTS (SELECT 1 FROM temp._purge_job s WHERE s.job_id = @p_job_id AND s.eligible = 1 AND s.lineage_hold = 0);

DELETE FROM {{schema}}.schedules
WHERE
    job_id = @p_job_id
    AND EXISTS (SELECT 1 FROM temp._purge_job s WHERE s.job_id = @p_job_id AND s.eligible = 1 AND s.lineage_hold = 0);

DELETE FROM {{schema}}.jobs
WHERE
    job_id = @p_job_id
    AND EXISTS (SELECT 1 FROM temp._purge_job s WHERE s.job_id = @p_job_id AND s.eligible = 1 AND s.lineage_hold = 0);

DELETE FROM {{schema}}.jobs_state
WHERE
    job_id = @p_job_id
    AND EXISTS (SELECT 1 FROM temp._purge_job s WHERE s.job_id = @p_job_id AND s.eligible = 1 AND s.lineage_hold = 0);

-- The tombstone keeps the deleted job's id as its subject; its details snapshot what no surviving event carries.
INSERT INTO {{schema}}.events (
    event_code,
    created_at_utc,
    namespace_id,
    entity_code,
    entity_id,
    definition_id,
    tenant_id,
    lineage_root_job_id,
    event_message,
    details_format_id,
    details)
SELECT
    75 /* EventCode.JobPurged */,
    {{now}},
    s.namespace_id,
    50 /* EntityCode.Job */,
    s.job_id,
    s.definition_id,
    s.tenant_id,
    COALESCE(s.lineage_root_job_id, s.job_id),
    @p_event_message,
    @p_details_format_id,
    @p_details
FROM temp._purge_job s
WHERE
    s.job_id = @p_job_id
    AND s.eligible = 1
    AND s.lineage_hold = 0;

SELECT
    CASE
        WHEN s.job_id IS NULL THEN 2 /* ControlAction.NotFound */
        WHEN s.eligible = 0 THEN 3 /* ControlAction.Rejected */
        WHEN s.lineage_hold = 1 THEN 3 /* ControlAction.Rejected */
        ELSE 1 /* ControlAction.Applied */
    END AS action,
    CASE
        WHEN s.job_id IS NULL THEN NULL
        WHEN s.eligible = 0 THEN s.from_status
        WHEN s.lineage_hold = 1 THEN s.from_status
        ELSE NULL
    END AS status
FROM (SELECT @p_job_id AS qid) q
LEFT JOIN temp._purge_job s ON s.job_id = q.qid;
