-- One batch, one implicit transaction; only the final SELECT returns rows. Lock order: a slot's schedule lock, the
-- lane, then the job's rows (docs/sql-internals.md, "Schedule lock" and "Lane lock order"); kind, namespace and
-- lane_id never change, so the unlocked reads that find them are safe.
DROP TABLE IF EXISTS pg_temp._reschedule_job_lock;
DROP TABLE IF EXISTS pg_temp._reschedule_job_lane;
DROP TABLE IF EXISTS pg_temp._reschedule_job;
CREATE TEMP TABLE _reschedule_job_lock (granted INT NOT NULL) ON COMMIT DROP;
CREATE TEMP TABLE _reschedule_job_lane (lane_id BIGINT NOT NULL) ON COMMIT DROP;
CREATE TEMP TABLE _reschedule_job (
    from_status SMALLINT NOT NULL,
    from_version INT NOT NULL,
    lane_id BIGINT,
    namespace_id INT NOT NULL,
    lineage_root_job_id BIGINT,
    definition_id INT NOT NULL,
    tenant_id INT,
    execution_number INT NOT NULL,
    to_status SMALLINT NOT NULL,
    fires BOOLEAN NOT NULL,
    applies BOOLEAN NOT NULL
) ON COMMIT DROP;

-- A slot's manual firing is an admission, so it holds the namespace's schedule lock, shared, as a trigger does.
INSERT INTO _reschedule_job_lock (granted)
SELECT 1
FROM (
    SELECT pg_advisory_xact_lock_shared(hashtext('{{schema}}.schedules'), j.namespace_id)
    FROM {{schema}}.jobs j
    WHERE j.job_id = @p_job_id AND j.job_kind_code = 20 /* JobKindCode.Permanent */
) l;

INSERT INTO _reschedule_job_lane (lane_id)
SELECT l.lane_id
FROM {{schema}}.lanes l
WHERE l.lane_id = (SELECT r.lane_id FROM {{schema}}.jobs_state r WHERE r.job_id = @p_job_id)
FOR UPDATE;

-- An Idle or Paused permanent slot with no firing outstanding takes a manual firing at the instant; a slot mid-firing
-- moves its instant like any waiting job. Idle applies to a slot alone.
INSERT INTO _reschedule_job (
    from_status, from_version, lane_id, namespace_id, lineage_root_job_id, definition_id, tenant_id, execution_number,
    to_status, fires, applies)
SELECT
    r.job_status_code, r.version, r.lane_id, j.namespace_id, j.lineage_root_job_id, j.definition_id, j.tenant_id, r.execution_number,
    10 /* JobStatusCode.Ready */,
    j.job_kind_code = 20 /* JobKindCode.Permanent */
        AND r.job_status_code IN (40 /* JobStatusCode.Idle */, 30 /* JobStatusCode.Paused */)
        AND NOT EXISTS (
            SELECT 1
            FROM {{schema}}.checkpoints cp
            WHERE cp.job_id = j.job_id AND cp.checkpoint_kind_code = 60 /* CheckpointKindCode.Firing */
                AND cp.checkpoint_name = 'sys.firing' AND cp.checkpoint_status_code = 10 /* CheckpointStatusCode.Pending */
        ),
    (
        r.job_status_code IN (30 /* JobStatusCode.Paused */, 20 /* JobStatusCode.Suspended */, 10 /* JobStatusCode.Ready */, 15 /* JobStatusCode.Blocked */)
        OR (r.job_status_code = 40 /* JobStatusCode.Idle */ AND j.job_kind_code = 20 /* JobKindCode.Permanent */)
    )
    -- A retired definition's slot is inert: no manual firing until a build that carries the definition starts.
    AND NOT (
        j.job_kind_code = 20 /* JobKindCode.Permanent */
        AND EXISTS (
            SELECT 1 FROM {{schema}}.definitions d
            WHERE d.definition_id = j.definition_id AND d.definition_status_code = 240 /* DefinitionStatusCode.Retired */
        )
    )
FROM {{schema}}.jobs j
JOIN {{schema}}.jobs_state r ON r.job_id = j.job_id
WHERE j.job_id = @p_job_id
FOR UPDATE;

-- A laned job is rescheduled Blocked while another member runs, or when it was not running and an older
-- member is unfinished.
UPDATE _reschedule_job s
SET to_status = 15 /* JobStatusCode.Blocked */
WHERE
    s.lane_id IS NOT NULL
    AND EXISTS (
        SELECT 1
        FROM {{schema}}.jobs_state o
        WHERE
            o.lane_id = s.lane_id
            AND o.lane_id IS NOT NULL
            AND o.job_status_code IN (
                10 /* JobStatusCode.Ready */, 15 /* JobStatusCode.Blocked */, 20 /* JobStatusCode.Suspended */,
                30 /* JobStatusCode.Paused */, 50 /* JobStatusCode.Executing */
            )
            AND o.job_id <> @p_job_id
            AND (
                o.job_status_code IN (
                    10 /* JobStatusCode.Ready */, 20 /* JobStatusCode.Suspended */,
                    50 /* JobStatusCode.Executing */
                )
                OR (
                    s.from_status NOT IN (10 /* JobStatusCode.Ready */, 20 /* JobStatusCode.Suspended */)
                    AND o.job_id < @p_job_id
                )
            )
    );

-- A suspended namespace takes no manual firing, read here after the lock a suspension holds exclusive; a slot
-- mid-firing still moves its instant.
UPDATE _reschedule_job s
SET applies = false
WHERE
    s.fires
    AND NOT EXISTS (
        SELECT 1
        FROM {{schema}}.namespaces n
        WHERE n.namespace_id = s.namespace_id AND n.namespace_status_code = 10 /* NamespaceStatusCode.Active */
    );

-- A null instant is now; a manual firing starts with a fresh failure budget.
UPDATE {{schema}}.jobs_state AS r
SET
    next_run_at_utc = COALESCE(@p_next_run_at_utc, now()),
    job_status_code = s.to_status,
    failure_count = CASE WHEN s.fires THEN 0 ELSE r.failure_count END,
    modified_at_utc = now(),
    version = r.version + 1
FROM _reschedule_job s
WHERE
    r.job_id = @p_job_id
    AND (@p_expected_version IS NULL OR s.from_version = @p_expected_version)
    AND s.applies;

-- The manual envelope replaces the settled one of the slot's previous firing.
INSERT INTO {{schema}}.checkpoints (
    job_id, checkpoint_kind_code, checkpoint_name, checkpoint_status_code,
    value_format_id, value, created_at_utc, modified_at_utc, version)
SELECT
    @p_job_id, 60 /* CheckpointKindCode.Firing */, 'sys.firing', 10 /* CheckpointStatusCode.Pending */,
    1 /* JobPayloadFormat.Json */, convert_to(@p_envelope, 'UTF8'), now(), now(), 0
FROM _reschedule_job s
WHERE s.fires AND s.applies AND (@p_expected_version IS NULL OR s.from_version = @p_expected_version)
ON CONFLICT (job_id, checkpoint_kind_code, checkpoint_name) DO UPDATE SET
    checkpoint_status_code = EXCLUDED.checkpoint_status_code,
    value_format_id = EXCLUDED.value_format_id,
    value = EXCLUDED.value,
    modified_at_utc = now(),
    version = {{schema}}.checkpoints.version + 1;

INSERT INTO {{schema}}.events (
    event_code, created_at_utc, namespace_id,
    entity_code, entity_id,
    definition_id, tenant_id, lineage_root_job_id,
    execution_number,
    event_message, details_format_id, details
)
SELECT
    61 /* EventCode.JobRescheduled */, now(), s.namespace_id,
    50 /* EntityCode.Job */, @p_job_id,
    s.definition_id, s.tenant_id, COALESCE(s.lineage_root_job_id, @p_job_id),
    s.execution_number,
    @p_event_message, @p_details_format_id, @p_details
FROM _reschedule_job s
WHERE
    (@p_expected_version IS NULL OR s.from_version = @p_expected_version)
    AND s.applies;

SELECT
    CASE
        WHEN s.from_status IS NULL THEN 2 /* ControlAction.NotFound */
        WHEN @p_expected_version IS NOT NULL AND s.from_version <> @p_expected_version THEN 5 /* ControlAction.VersionConflict */
        WHEN NOT s.applies THEN 3 /* ControlAction.Rejected */
        ELSE 1 /* ControlAction.Applied */
    END::SMALLINT AS action,
    CASE
        WHEN s.from_status IS NULL THEN NULL
        WHEN @p_expected_version IS NOT NULL AND s.from_version <> @p_expected_version THEN s.from_status
        WHEN NOT s.applies THEN s.from_status
        ELSE s.to_status
    END AS status,
    CASE
        WHEN s.from_status IS NULL THEN NULL
        WHEN @p_expected_version IS NOT NULL AND s.from_version <> @p_expected_version THEN s.from_version
        WHEN NOT s.applies THEN s.from_version
        ELSE s.from_version + 1
    END AS version
FROM (SELECT 1) one
LEFT JOIN _reschedule_job s ON true;
