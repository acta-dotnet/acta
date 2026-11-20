-- SQLite owns one immediate transaction for the file.
DROP TABLE IF EXISTS temp._purge_alerts;

-- Past the window an alert goes whether its delivery settled or not, so a stuck row is not immortal, and an open
-- incident goes too: ux_alerts_deduplication_key covers unresolved rows only, so the delete frees the identity and
-- the next failure opens a fresh incident.
CREATE TEMP TABLE _purge_alerts AS
SELECT alert_id, delivery_status_code
FROM {{schema}}.alerts
WHERE
    namespace_id = @p_namespace_id
    AND created_at_utc <= @p_cutoff_utc
ORDER BY created_at_utc, alert_id
LIMIT @p_batch_size;

DELETE FROM {{schema}}.alerts WHERE alert_id IN (SELECT p.alert_id FROM temp._purge_alerts p);

SELECT
    COUNT(*) AS deleted_count,
    COUNT(CASE WHEN delivery_status_code IN (10 /* AlertDeliveryStatusCode.Pending */, 20 /* AlertDeliveryStatusCode.Retrying */) THEN 1 END)
        AS undelivered_count
FROM temp._purge_alerts;
