-- An incident is one open row per key in its namespace (ux_alerts_deduplication_key).
SELECT a.alert_ref
FROM {{schema}}.alerts a
WHERE
    a.namespace_id = @p_namespace_id
    AND a.deduplication_key = @p_deduplication_key
    AND a.resolved_at_utc IS NULL;
