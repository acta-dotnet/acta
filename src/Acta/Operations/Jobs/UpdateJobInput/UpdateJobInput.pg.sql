-- One batch, one implicit transaction; only the final SELECT returns rows. The job's rows are locked, and a
-- job that is not in flight takes the new input.
DROP TABLE IF EXISTS pg_temp._update_job_input;
CREATE TEMP TABLE _update_job_input (
    from_status SMALLINT NOT NULL,
    namespace_id INT NOT NULL,
    lineage_root_job_id BIGINT,
    definition_id INT NOT NULL,
    tenant_id INT,
    execution_number INT NOT NULL,
    event_recording_code SMALLINT NOT NULL,
    old_format_id SMALLINT NOT NULL,
    old_bytes INT
) ON COMMIT DROP;

INSERT INTO _update_job_input (
    from_status, namespace_id, lineage_root_job_id, definition_id, tenant_id, execution_number, event_recording_code,
    old_format_id, old_bytes)
SELECT
    r.job_status_code, j.namespace_id, j.lineage_root_job_id, j.definition_id, j.tenant_id, r.execution_number, j.event_recording_code,
    j.input_format_id, octet_length(j.input)
FROM {{schema}}.jobs j
JOIN {{schema}}.jobs_state r ON r.job_id = j.job_id
WHERE j.job_id = @p_job_id
FOR UPDATE;

UPDATE {{schema}}.jobs
SET
    input = @p_input,
    input_format_id = @p_input_format_id
WHERE
    job_id = @p_job_id
    AND EXISTS (
        SELECT 1 FROM _update_job_input s
        WHERE s.from_status <> 50 /* JobStatusCode.Executing */
    );

-- The details are the operator's, plus bounded JSON metadata about the previous payload (format name and byte
-- count), never the payload itself. The writer's details are a compact JSON object, so the metadata joins it as
-- more members before its closing brace.
INSERT INTO {{schema}}.events (
    event_code, created_at_utc, namespace_id,
    entity_code, entity_id,
    definition_id, tenant_id, lineage_root_job_id,
    execution_number,
    event_message, details_format_id, details
)
SELECT
    76 /* EventCode.JobInputAmended */, now(), s.namespace_id,
    50 /* EntityCode.Job */, @p_job_id,
    s.definition_id, s.tenant_id, COALESCE(s.lineage_root_job_id, @p_job_id),
    s.execution_number,
    @p_event_message,
    CASE WHEN o.members IS NULL THEN @p_details_format_id ELSE 1 /* JobPayloadFormat.Json */ END,
    CASE
        WHEN o.members IS NULL THEN @p_details
        WHEN @p_details IS NULL THEN convert_to('{' || o.members || '}', 'UTF8')
        ELSE
            substring(@p_details FROM 1 FOR octet_length(@p_details) - 1)
            || convert_to(',' || o.members || '}', 'UTF8')
    END
FROM _update_job_input s
CROSS JOIN LATERAL (
    SELECT
        CASE WHEN s.old_format_id <> 0 /* JobPayloadFormat.None */ THEN
            '"format":"'
            || CASE s.old_format_id
                WHEN 1 /* JobPayloadFormat.Json */ THEN 'json'
                WHEN 2 /* JobPayloadFormat.Bytes */ THEN 'bytes'
                ELSE 'custom-' || s.old_format_id::text
            END
            || '","bytes":' || s.old_bytes::text
        END AS members
) o
WHERE
    s.event_recording_code = 20 /* EventRecordingCode.Audit */
    AND s.from_status <> 50 /* JobStatusCode.Executing */;

SELECT
    CASE
        WHEN s.from_status IS NULL THEN 2 /* ControlAction.NotFound */
        WHEN s.from_status = 50 /* JobStatusCode.Executing */ THEN 3 /* ControlAction.Rejected */
        ELSE 1 /* ControlAction.Applied */
    END::SMALLINT AS action,
    s.from_status AS status
FROM (SELECT 1) one
LEFT JOIN _update_job_input s ON true;
