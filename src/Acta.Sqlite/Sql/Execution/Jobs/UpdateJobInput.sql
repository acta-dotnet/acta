DROP TABLE IF EXISTS temp._update_job_input;

CREATE TEMP TABLE _update_job_input AS
SELECT j.id, r.status_code AS from_status
FROM {{schema}}.jobs j
JOIN {{schema}}.runtimes r ON r.job_id = j.id
WHERE j.id = @p_id;

-- The audit event carries only bounded JSON metadata about the previous payload (format name and
-- byte count), never the payload itself. Emitted BEFORE the update so j.input_format_id / j.input
-- still hold the old value, and gated on the same non-dispatched/executing guard.
INSERT INTO {{schema}}.events (
    event_code, created_at_utc, namespace_id,
    actor_code, actor_key,
    job_id, job_ref, execution_number,
    lineage_root_id, definition_id, tenant_id,
    worker_id,
    from_status_code, to_status_code,
    execution_status_code, duration_ms,
    detail_format_id, detail,
    reason_code, reason_message)
SELECT
    76 /* JobEventCode.JobInputAmended */, {{now}}, j.namespace_id,
    @p_actor_code, @p_actor_key,
    j.id, j.job_ref, r.execution_number,
    COALESCE(j.lineage_root_id, j.id), j.definition_id, j.tenant_id,
    NULL,
    NULL, NULL,
    NULL, NULL,
    CASE WHEN j.input_format_id = 0 /* JobPayloadFormat.None */
         THEN 0 /* JobPayloadFormat.None */
         ELSE 1 /* JobPayloadFormat.Json */ END,
    CASE WHEN j.input_format_id = 0 /* JobPayloadFormat.None */
         THEN NULL
         ELSE CAST(json_object(
                  'format',
                  CASE j.input_format_id
                      WHEN 1 /* JobPayloadFormat.Json */ THEN 'json'
                      WHEN 2 /* JobPayloadFormat.Bytes */ THEN 'bytes'
                      WHEN 3 /* JobPayloadFormat.Text */ THEN 'text'
                      ELSE 'custom-' || j.input_format_id
                  END,
                  'bytes', length(j.input)) AS BLOB)
         END,
    @p_reason_code, @p_reason_message
FROM {{schema}}.jobs j
JOIN {{schema}}.runtimes r ON r.job_id = j.id
WHERE j.id = @p_id
  AND j.audit_level_code = 20 /* JobAuditLevelCode.Audit */
  AND r.status_code NOT IN (40 /* JobStatusCode.Dispatched */, 50 /* JobStatusCode.Executing */);

UPDATE {{schema}}.jobs
   SET input           = @p_input,
       input_format_id = @p_input_format_id
 WHERE id = @p_id
   AND EXISTS (
       SELECT 1 FROM {{schema}}.runtimes r
        WHERE r.job_id = @p_id
          AND r.status_code NOT IN (40 /* JobStatusCode.Dispatched */, 50 /* JobStatusCode.Executing */));

SELECT
    CASE
        WHEN s.id IS NULL THEN 2 /* JobControlAction.NotFound */
        WHEN s.from_status NOT IN (40 /* JobStatusCode.Dispatched */, 50 /* JobStatusCode.Executing */) THEN 1 /* JobControlAction.Applied */
        ELSE 3 /* JobControlAction.Rejected */
    END AS action,
    s.from_status AS status_code
FROM (SELECT @p_id AS qid) q
LEFT JOIN temp._update_job_input s ON s.id = q.qid;
