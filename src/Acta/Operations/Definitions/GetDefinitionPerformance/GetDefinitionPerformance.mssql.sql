DECLARE @ns_id INT = (SELECT jd.namespace_id FROM {{schema}}.definitions jd WHERE jd.definition_id = @p_definition_id);

-- A definition's finished executions in a range, the newest first up to the limit, counted per interval, outcome event
-- and duration. A duration is truncated to two significant figures, which keeps order, so a percentile taken over the
-- counts is the exact percentile truncated the same way.
SELECT
    a.interval_number,
    a.event_code,
    a.duration_group_ms,
    COUNT_BIG(*) AS execution_count,
    MIN(a.created_at_utc) AS oldest_at_utc
FROM (
    SELECT TOP (@p_execution_limit)
        e.created_at_utc,
        e.event_code,
        CAST(DATEDIFF_BIG(MILLISECOND, @p_created_from_utc, e.created_at_utc) / 1000 / @p_interval_seconds AS INT) AS interval_number,
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
        END AS duration_group_ms
    -- A dirty read: it never waits on a completion, and it may count an execution whose transaction has not
    -- committed yet.
    FROM {{schema}}.events e WITH (NOLOCK, FORCESEEK (ix_events_duration (namespace_id, definition_id, created_at_utc)))
    WHERE
        e.namespace_id = @ns_id
        AND e.definition_id = @p_definition_id
        AND e.created_at_utc >= @p_created_from_utc
        AND e.created_at_utc < @p_created_to_utc
        -- ix_events_duration's filter restated whole, so the read is one range of that index.
        AND e.event_code IN (40 /* EventCode.JobExecutionSucceeded */, 41 /* EventCode.JobExecutionFailed */, 44 /* EventCode.JobFailed */)
        AND e.duration_ms IS NOT NULL
    ORDER BY e.created_at_utc DESC
) a
GROUP BY a.interval_number, a.event_code, a.duration_group_ms;
