-- One batch, one implicit transaction; only the final SELECT returns rows. The alert row is locked; a second
-- resolve keeps the first instant and audits nothing.
DROP TABLE IF EXISTS pg_temp._resolve_alert;
CREATE TEMP TABLE _resolve_alert (
    namespace_id INT NOT NULL,
    job_id BIGINT,
    job_ref UUID,
    already_resolved BOOLEAN NOT NULL
) ON COMMIT DROP;

INSERT INTO _resolve_alert (namespace_id, job_id, job_ref, already_resolved)
SELECT a.namespace_id, a.job_id, a.job_ref, a.resolved_at_utc IS NOT NULL
FROM {{schema}}.alerts a
WHERE a.alert_ref = @p_alert_ref
FOR UPDATE;

-- Same settlement as the automatic resolve: a queued notification for a condition an operator declared cleared
-- is cancelled, and an already-settled status stands as the record of what the send did.
UPDATE {{schema}}.alerts
SET
    resolved_at_utc = now(),
    delivery_status_code = CASE
        WHEN delivery_status_code IN (10 /* AlertDeliveryStatusCode.Pending */, 20 /* AlertDeliveryStatusCode.RetryAfter */)
            THEN 30 /* AlertDeliveryStatusCode.Suppressed */
        ELSE delivery_status_code
    END,
    retry_after_utc = NULL,
    modified_at_utc = now(),
    version = version + 1
WHERE alert_ref = @p_alert_ref AND EXISTS (SELECT 1 FROM _resolve_alert t WHERE NOT t.already_resolved);

INSERT INTO {{schema}}.events (
    event_code, created_at_utc, namespace_id, actor_code, actor, job_id, job_ref, execution_number,
    lineage_root_id, definition_id, reason_message)
SELECT
    141 /* EventCode.AlertResolved */, now(), t.namespace_id, @p_actor_code, @p_actor, t.job_id, t.job_ref,
    r.execution_number, COALESCE(j.lineage_root_id, t.job_id), j.definition_id, @p_reason_message
FROM _resolve_alert t
LEFT JOIN {{schema}}.jobs j ON j.id = t.job_id
LEFT JOIN {{schema}}.runtimes r ON r.job_id = t.job_id
WHERE NOT t.already_resolved;

SELECT
    CASE WHEN a.id IS NULL THEN 2 /* ControlAction.NotFound */ ELSE 1 /* ControlAction.Applied */ END::SMALLINT AS action,
    a.acknowledged_at_utc AS acknowledged_at_utc,
    a.resolved_at_utc AS resolved_at_utc
FROM (SELECT 1) one
LEFT JOIN {{schema}}.alerts a ON a.alert_ref = @p_alert_ref;
