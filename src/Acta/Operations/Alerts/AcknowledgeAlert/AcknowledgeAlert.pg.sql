-- One batch, one implicit transaction; only the final SELECT returns rows. The alert row is locked; a second
-- acknowledge keeps the first instant and audits nothing.
DROP TABLE IF EXISTS pg_temp._acknowledge_alert;
CREATE TEMP TABLE _acknowledge_alert (
    alert_id BIGINT NOT NULL,
    namespace_id INT NOT NULL,
    job_id BIGINT,
    already_acknowledged BOOLEAN NOT NULL
) ON COMMIT DROP;

INSERT INTO _acknowledge_alert (alert_id, namespace_id, job_id, already_acknowledged)
SELECT a.alert_id, a.namespace_id, a.job_id, a.acknowledged_at_utc IS NOT NULL
FROM {{schema}}.alerts a
WHERE a.alert_ref = @p_alert_ref
FOR UPDATE;

UPDATE {{schema}}.alerts
SET
    acknowledged_at_utc = now(),
    modified_at_utc = now(),
    version = version + 1
WHERE alert_ref = @p_alert_ref AND EXISTS (SELECT 1 FROM _acknowledge_alert t WHERE NOT t.already_acknowledged);

INSERT INTO {{schema}}.events (
    event_code, created_at_utc, namespace_id,
    entity_code, entity_id,
    definition_id, tenant_id, lineage_root_job_id,
    event_message, details_format_id, details
)
SELECT
    140 /* EventCode.AlertAcknowledged */, now(), t.namespace_id,
    10 /* EntityCode.Alert */, t.alert_id,
    j.definition_id, j.tenant_id, COALESCE(j.lineage_root_job_id, t.job_id),
    @p_event_message, @p_details_format_id, @p_details
FROM _acknowledge_alert t
LEFT JOIN {{schema}}.jobs j ON j.job_id = t.job_id
WHERE NOT t.already_acknowledged;

SELECT
    CASE WHEN a.alert_id IS NULL THEN 2 /* ControlAction.NotFound */ ELSE 1 /* ControlAction.Applied */ END::SMALLINT AS action,
    a.acknowledged_at_utc AS acknowledged_at_utc,
    a.resolved_at_utc AS resolved_at_utc
FROM (SELECT 1) one
LEFT JOIN {{schema}}.alerts a ON a.alert_ref = @p_alert_ref;
