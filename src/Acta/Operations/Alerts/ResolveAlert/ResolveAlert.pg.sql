-- One batch, one implicit transaction; only the final SELECT returns rows. The alert row is locked; a second
-- resolve keeps the first instant and audits nothing.
DROP TABLE IF EXISTS pg_temp._resolve_alert;
CREATE TEMP TABLE _resolve_alert (
    alert_id BIGINT NOT NULL,
    namespace_id INT NOT NULL,
    job_id BIGINT,
    already_resolved BOOLEAN NOT NULL
) ON COMMIT DROP;

INSERT INTO _resolve_alert (alert_id, namespace_id, job_id, already_resolved)
SELECT a.alert_id, a.namespace_id, a.job_id, a.resolved_at_utc IS NOT NULL
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
    event_code, created_at_utc, namespace_id,
    entity_code, entity_id,
    definition_id, tenant_id, lineage_root_job_id,
    event_message, details_format_id, details
)
SELECT
    141 /* EventCode.AlertResolved */, now(), t.namespace_id,
    10 /* EntityCode.Alert */, t.alert_id,
    j.definition_id, j.tenant_id, COALESCE(j.lineage_root_job_id, t.job_id),
    @p_event_message, @p_details_format_id, @p_details
FROM _resolve_alert t
LEFT JOIN {{schema}}.jobs j ON j.job_id = t.job_id
WHERE NOT t.already_resolved;

SELECT
    CASE WHEN a.alert_id IS NULL THEN 2 /* ControlAction.NotFound */ ELSE 1 /* ControlAction.Applied */ END::SMALLINT AS action,
    a.acknowledged_at_utc AS acknowledged_at_utc,
    a.resolved_at_utc AS resolved_at_utc
FROM (SELECT 1) one
LEFT JOIN {{schema}}.alerts a ON a.alert_ref = @p_alert_ref;
