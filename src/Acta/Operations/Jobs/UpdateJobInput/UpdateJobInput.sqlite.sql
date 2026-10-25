DROP TABLE IF EXISTS temp._update_job_input;

CREATE TEMP TABLE _update_job_input AS
SELECT j.job_id, r.job_status_code AS from_status
FROM {{schema}}.jobs j
JOIN {{schema}}.jobs_state r ON r.job_id = j.job_id
WHERE j.job_id = @p_job_id;

-- The details are the operator's, plus bounded JSON metadata about the previous payload (format name and byte
-- count), never the payload itself, joined as more members before the writer's closing brace. Emitted BEFORE the
-- update so j.input_format_id / j.input still hold the old value, and gated on the same non-executing guard.
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
    details_format_id,
    details)
SELECT
    76 /* EventCode.JobInputAmended */,
    {{now}},
    j.namespace_id,
    50 /* EntityCode.Job */,
    j.job_id,
    j.definition_id,
    j.tenant_id,
    COALESCE(j.lineage_root_job_id, j.job_id),
    r.execution_number,
    @p_event_message,
    CASE WHEN o.members IS NULL THEN @p_details_format_id ELSE 1 /* JobPayloadFormat.Json */ END,
    CASE
        WHEN o.members IS NULL THEN @p_details
        WHEN @p_details IS NULL THEN CAST('{' || o.members || '}' AS BLOB)
        ELSE CAST(substr(@p_details, 1, length(@p_details) - 1) || ',' || o.members || '}' AS BLOB)
    END
FROM {{schema}}.jobs j
JOIN {{schema}}.jobs_state r ON r.job_id = j.job_id
CROSS JOIN old_input o
WHERE
    j.job_id = @p_job_id
    AND j.event_recording_code = 20 /* EventRecordingCode.Audit */
    AND r.job_status_code <> 50 /* JobStatusCode.Executing */;

UPDATE {{schema}}.jobs
SET
    input = @p_input,
    input_format_id = @p_input_format_id
WHERE
    job_id = @p_job_id
    AND EXISTS (
        SELECT 1
        FROM {{schema}}.jobs_state r
        WHERE
            r.job_id = @p_job_id
            AND r.job_status_code <> 50 /* JobStatusCode.Executing */);

SELECT
    CASE
        WHEN s.job_id IS NULL THEN 2 /* ControlAction.NotFound */
        WHEN s.from_status <> 50 /* JobStatusCode.Executing */ THEN 1 /* ControlAction.Applied */
        ELSE 3 /* ControlAction.Rejected */
    END AS action,
    s.from_status AS status
FROM (SELECT @p_job_id AS qid) q
LEFT JOIN temp._update_job_input s ON s.job_id = q.qid;
