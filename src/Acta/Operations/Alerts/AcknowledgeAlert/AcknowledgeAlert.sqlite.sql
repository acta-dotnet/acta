DROP TABLE IF EXISTS temp._ack_job_alert;

CREATE TEMP TABLE _ack_job_alert AS
SELECT
    a.alert_id,
    a.alert_ref,
    a.namespace_id,
    a.job_id,
    a.acknowledged_at_utc,
    a.resolved_at_utc,
    CASE WHEN a.acknowledged_at_utc IS NOT NULL THEN 1 ELSE 0 END AS already_ack,
    {{now}} AS now_utc
FROM {{schema}}.alerts a
WHERE a.alert_ref = @p_alert_ref;

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
    140 /* EventCode.AlertAcknowledged */,
    t.now_utc,
    t.namespace_id,
    10 /* EntityCode.Alert */,
    t.alert_id,
    j.definition_id,
    j.tenant_id,
    COALESCE(j.lineage_root_job_id, t.job_id),
    @p_event_message,
    @p_details_format_id,
    @p_details
FROM temp._ack_job_alert t
LEFT JOIN {{schema}}.jobs j ON j.job_id = t.job_id
WHERE t.already_ack = 0;

UPDATE {{schema}}.alerts
SET
    acknowledged_at_utc = (SELECT now_utc FROM temp._ack_job_alert),
    modified_at_utc = (SELECT now_utc FROM temp._ack_job_alert),
    version = version + 1
WHERE
    alert_ref = @p_alert_ref
    AND EXISTS (SELECT 1 FROM temp._ack_job_alert t WHERE t.already_ack = 0);

SELECT
    CASE
        WHEN t.alert_id IS NULL THEN 2 /* ControlAction.NotFound */
        ELSE 1 /* ControlAction.Applied */
    END AS action,
    CASE
        WHEN t.alert_id IS NULL THEN NULL
        WHEN t.already_ack = 1 THEN t.acknowledged_at_utc
        ELSE t.now_utc
    END AS acknowledged_at_utc,
    t.resolved_at_utc AS resolved_at_utc
FROM (SELECT @p_alert_ref AS qref) q
LEFT JOIN temp._ack_job_alert t ON t.alert_ref = q.qref;
