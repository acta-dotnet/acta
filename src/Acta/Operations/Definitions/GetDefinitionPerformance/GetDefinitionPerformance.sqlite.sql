-- A definition's finished executions in a range, the newest first up to the limit, counted per interval, outcome event
-- and duration, and again per interval, outcome event and wait. A duration or a wait is truncated to two significant
-- figures, which keeps order, so a percentile taken over the counts is the exact percentile truncated the same way.
WITH a AS MATERIALIZED (
    SELECT
        e.created_at_utc,
        e.event_code,
        (e.created_at_utc - @p_created_from_utc) / (@p_interval_seconds * 1000) AS interval_number,
        CASE
            WHEN e.duration_ms < 100 THEN e.duration_ms
            WHEN e.duration_ms < 1000 THEN e.duration_ms / 10 * 10
            WHEN e.duration_ms < 10000 THEN e.duration_ms / 100 * 100
            WHEN e.duration_ms < 100000 THEN e.duration_ms / 1000 * 1000
            WHEN e.duration_ms < 1000000 THEN e.duration_ms / 10000 * 10000
            WHEN e.duration_ms < 10000000 THEN e.duration_ms / 100000 * 100000
            WHEN e.duration_ms < 100000000 THEN e.duration_ms / 1000000 * 1000000
            WHEN e.duration_ms < 1000000000 THEN e.duration_ms / 10000000 * 10000000
            ELSE e.duration_ms / 100000000 * 100000000
        END AS duration_group_ms,
        CASE
            WHEN e.wait_ms < 100 THEN e.wait_ms
            WHEN e.wait_ms < 1000 THEN e.wait_ms / 10 * 10
            WHEN e.wait_ms < 10000 THEN e.wait_ms / 100 * 100
            WHEN e.wait_ms < 100000 THEN e.wait_ms / 1000 * 1000
            WHEN e.wait_ms < 1000000 THEN e.wait_ms / 10000 * 10000
            WHEN e.wait_ms < 10000000 THEN e.wait_ms / 100000 * 100000
            WHEN e.wait_ms < 100000000 THEN e.wait_ms / 1000000 * 1000000
            WHEN e.wait_ms < 1000000000 THEN e.wait_ms / 10000000 * 10000000
            ELSE e.wait_ms / 100000000 * 100000000
        END AS wait_group_ms
    FROM {{schema}}.events e
    WHERE
        e.namespace_id = (SELECT jd.namespace_id FROM {{schema}}.definitions jd WHERE jd.definition_id = @p_definition_id)
        AND e.definition_id = @p_definition_id
        AND e.created_at_utc >= @p_created_from_utc
        AND e.created_at_utc < @p_created_to_utc
        -- ix_events_duration's filter restated whole, so the read is one range of that index.
        AND e.event_code IN (9 /* EventCode.JobExecutionFailed */, 11 /* EventCode.JobExecutionSucceeded */, 13 /* EventCode.JobFailed */)
        AND e.duration_ms IS NOT NULL
    ORDER BY e.created_at_utc DESC
    LIMIT @p_execution_limit
)
-- One read counted twice, as the server providers' grouping sets count it: a duration row leaves wait_group_ms NULL,
-- and a wait row leaves duration_group_ms NULL.
SELECT
    a.interval_number,
    a.event_code,
    a.duration_group_ms,
    NULL AS wait_group_ms,
    COUNT(*) AS execution_count,
    MIN(a.created_at_utc) AS oldest_at_utc
FROM a
GROUP BY a.interval_number, a.event_code, a.duration_group_ms
UNION ALL
SELECT
    a.interval_number,
    a.event_code,
    NULL AS duration_group_ms,
    a.wait_group_ms,
    COUNT(*) AS execution_count,
    MIN(a.created_at_utc) AS oldest_at_utc
FROM a
GROUP BY a.interval_number, a.event_code, a.wait_group_ms;
