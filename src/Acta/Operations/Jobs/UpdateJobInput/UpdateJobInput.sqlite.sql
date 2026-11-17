DROP TABLE IF EXISTS temp._update_job_input;

-- A job that is not in flight and has an input takes the new one, compare-and-set when an expected version is given.
CREATE TEMP TABLE _update_job_input AS
SELECT
    j.job_id,
    r.job_status_code AS from_status,
    r.version AS from_version,
    r.job_status_code <> 50 /* JobStatusCode.Executing */
        AND j.input_format_id <> 0 /* JobPayloadFormat.None */
        AND (@p_expected_version IS NULL OR r.version = @p_expected_version) AS applies
FROM {{schema}}.jobs j
JOIN {{schema}}.jobs_state r ON r.job_id = j.job_id
WHERE j.job_id = @p_job_id;

-- The details are the operator's, plus bounded JSON metadata about the previous payload (format name and byte
-- count), never the payload itself, joined as more members before the writer's closing brace. Emitted BEFORE the
-- update so j.input_format_id / j.input still hold the old value, and gated on the same guard.
WITH old_input (members) AS (
    SELECT
        CASE WHEN j.input_format_id <> 0 /* JobPayloadFormat.None */ THEN
            '"format":"'
            || CASE j.input_format_id
                WHEN 1 /* JobPayloadFormat.Json */ THEN 'json'
                WHEN 2 /* JobPayloadFormat.Bytes */ THEN 'bytes'
                ELSE 'custom-' || j.input_format_id
            END
            || '","bytes":' || length(j.input)
        END
    FROM {{schema}}.jobs j
    WHERE j.job_id = @p_job_id
)
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
    actor,
    details_format_id,
    details)
SELECT
    76 /* EventCode.JobInputUpdated */,
    {{now}},
    j.namespace_id,
    4 /* EntityCode.Job */,
    j.job_id,
    j.definition_id,
    j.tenant_id,
    COALESCE(j.lineage_root_job_id, j.job_id),
    r.execution_number,
    @p_event_message,
    @p_actor,
    CASE WHEN o.members IS NULL THEN @p_details_format_id ELSE 1 /* JobPayloadFormat.Json */ END,
    CASE
        WHEN o.members IS NULL THEN @p_details
        WHEN @p_details IS NULL THEN CAST('{' || o.members || '}' AS BLOB)
        ELSE CAST(substr(@p_details, 1, length(@p_details) - 1) || ',' || o.members || '}' AS BLOB)
    END
FROM {{schema}}.jobs j
JOIN {{schema}}.jobs_state r ON r.job_id = j.job_id
CROSS JOIN old_input o
JOIN temp._update_job_input s ON s.job_id = j.job_id
WHERE
    j.job_id = @p_job_id
    AND s.applies;

UPDATE {{schema}}.jobs
SET
    input = @p_input,
    input_format_id = @p_input_format_id
WHERE
    job_id = @p_job_id
    AND EXISTS (SELECT 1 FROM temp._update_job_input s WHERE s.applies);

-- The input lives on jobs, the version on jobs_state: an update moves it as every other control does.
UPDATE {{schema}}.jobs_state
SET
    modified_at_utc = {{now}},
    version = version + 1
WHERE
    job_id = @p_job_id
    AND EXISTS (SELECT 1 FROM temp._update_job_input s WHERE s.applies);

SELECT
    CASE
        WHEN s.job_id IS NULL THEN 2 /* ControlAction.NotFound */
        WHEN @p_expected_version IS NOT NULL AND s.from_version <> @p_expected_version THEN 5 /* ControlAction.VersionConflict */
        WHEN s.applies THEN 1 /* ControlAction.Applied */
        ELSE 3 /* ControlAction.Rejected */
    END AS action,
    s.from_status AS status,
    CASE WHEN s.applies THEN s.from_version + 1 ELSE s.from_version END AS version
FROM (SELECT @p_job_id AS qid) q
LEFT JOIN temp._update_job_input s ON s.job_id = q.qid;
