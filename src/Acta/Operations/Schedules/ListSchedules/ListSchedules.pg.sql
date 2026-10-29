-- Two forms, one switched off by @p_live_only: the live list seeks upcoming occurrences in due order, and the full
-- list, every schedule with a NULL cursor included, pages by the schedule's key. The page is one result set, so the
-- forms meet in a UNION ALL.
SELECT page.*
FROM (
    (
        SELECT
            s.schedule_id,
            s.job_id,
            s.definition_id,
            ns.namespace_name AS job_namespace,
            jd.definition_name AS job_name,
            s.schedule_name,
            s.schedule_origin_code AS origin,
            s.expression_kind_code AS expression_kind,
            s.expression,
            s.time_zone_id,
            s.misfire_strategy_code AS misfire_strategy,
            st.next_occurrence_at_utc,
            st.next_evaluation_at_utc,
            st.evaluation_status_code AS evaluation_status,
            s.schedule_status_code AS status,
            s.paused_until_utc,
            s.created_at_utc,
            s.modified_at_utc,
            s.version,
            sj.job_ref,
            s.max_outstanding,
            st.outstanding_count,
            s.full_behavior_code AS full_behavior,
            jd.scheduled_delivery_code AS scheduled_delivery,
            s.expire_after_seconds,
            s.expire_action_code AS expire_action,
            s.calendar_origin_code AS calendar_origin,
            s.calendar_version,
            s.availability_calendar_name,
            s.availability_calendar_origin_code AS availability_origin,
            s.availability_calendar_version AS availability_version
        FROM {{schema}}.schedules_state st
        JOIN {{schema}}.schedules s ON s.schedule_id = st.schedule_id
        JOIN {{schema}}.namespaces ns ON ns.namespace_id = st.namespace_id
        JOIN {{schema}}.definitions jd ON jd.definition_id = s.definition_id
        LEFT JOIN {{schema}}.jobs sj ON sj.job_id = s.job_id
        WHERE
            @p_live_only IS NOT NULL
            AND st.next_occurrence_at_utc IS NOT NULL
            AND s.schedule_status_code <> 230 /* ScheduleStatusCode.Orphaned */
            AND jd.definition_status_code <> 240 /* DefinitionStatusCode.Retired */
            AND (
                @p_namespace_name IS NULL
                OR st.namespace_id = (SELECT n.namespace_id FROM {{schema}}.namespaces n WHERE n.namespace_name = @p_namespace_name)
            )
            AND (@p_job_name IS NULL OR jd.definition_name = @p_job_name)
            AND (@p_schedule_origin_code IS NULL OR s.schedule_origin_code = @p_schedule_origin_code)
            AND (@p_cursor_next_occurrence_at_utc IS NULL
                OR st.next_occurrence_at_utc > @p_cursor_next_occurrence_at_utc
                OR (st.next_occurrence_at_utc = @p_cursor_next_occurrence_at_utc AND st.schedule_id > @p_cursor_id))
        ORDER BY st.next_occurrence_at_utc ASC, st.schedule_id ASC
        LIMIT @p_take
    )
    UNION ALL
    (
        SELECT
            s.schedule_id,
            s.job_id,
            s.definition_id,
            ns.namespace_name AS job_namespace,
            jd.definition_name AS job_name,
            s.schedule_name,
            s.schedule_origin_code AS origin,
            s.expression_kind_code AS expression_kind,
            s.expression,
            s.time_zone_id,
            s.misfire_strategy_code AS misfire_strategy,
            st.next_occurrence_at_utc,
            st.next_evaluation_at_utc,
            st.evaluation_status_code AS evaluation_status,
            s.schedule_status_code AS status,
            s.paused_until_utc,
            s.created_at_utc,
            s.modified_at_utc,
            s.version,
            sj.job_ref,
            s.max_outstanding,
            st.outstanding_count,
            s.full_behavior_code AS full_behavior,
            jd.scheduled_delivery_code AS scheduled_delivery,
            s.expire_after_seconds,
            s.expire_action_code AS expire_action,
            s.calendar_origin_code AS calendar_origin,
            s.calendar_version,
            s.availability_calendar_name,
            s.availability_calendar_origin_code AS availability_origin,
            s.availability_calendar_version AS availability_version
        FROM {{schema}}.schedules s
        JOIN {{schema}}.namespaces ns ON ns.namespace_id = s.namespace_id
        JOIN {{schema}}.definitions jd ON jd.definition_id = s.definition_id
        LEFT JOIN {{schema}}.schedules_state st ON st.schedule_id = s.schedule_id
        LEFT JOIN {{schema}}.jobs sj ON sj.job_id = s.job_id
        WHERE
            @p_live_only IS NULL
            AND (
                @p_namespace_name IS NULL
                OR s.namespace_id = (SELECT n.namespace_id FROM {{schema}}.namespaces n WHERE n.namespace_name = @p_namespace_name)
            )
            AND (@p_job_name IS NULL OR jd.definition_name = @p_job_name)
            AND (@p_schedule_origin_code IS NULL OR s.schedule_origin_code = @p_schedule_origin_code)
            AND (@p_cursor_id IS NULL OR s.schedule_id > @p_cursor_id)
        ORDER BY s.schedule_id ASC
        LIMIT @p_take
    )
) page
ORDER BY CASE WHEN @p_live_only IS NOT NULL THEN page.next_occurrence_at_utc END, page.schedule_id;

SELECT CASE WHEN @p_include_total IS NOT NULL THEN (
    CASE WHEN @p_live_only IS NOT NULL THEN (
        SELECT COUNT(*)
        FROM {{schema}}.schedules_state st
        JOIN {{schema}}.schedules s ON s.schedule_id = st.schedule_id
        JOIN {{schema}}.definitions jd ON jd.definition_id = s.definition_id
        WHERE
            st.next_occurrence_at_utc IS NOT NULL
            AND s.schedule_status_code <> 230 /* ScheduleStatusCode.Orphaned */
            AND jd.definition_status_code <> 240 /* DefinitionStatusCode.Retired */
            AND (
                @p_namespace_name IS NULL
                OR st.namespace_id = (SELECT n.namespace_id FROM {{schema}}.namespaces n WHERE n.namespace_name = @p_namespace_name)
            )
            AND (@p_job_name IS NULL OR jd.definition_name = @p_job_name)
            AND (@p_schedule_origin_code IS NULL OR s.schedule_origin_code = @p_schedule_origin_code)
    ) ELSE (
        SELECT COUNT(*)
        FROM {{schema}}.schedules s
        JOIN {{schema}}.definitions jd ON jd.definition_id = s.definition_id
        WHERE
            (
                @p_namespace_name IS NULL
                OR s.namespace_id = (SELECT n.namespace_id FROM {{schema}}.namespaces n WHERE n.namespace_name = @p_namespace_name)
            )
            AND (@p_job_name IS NULL OR jd.definition_name = @p_job_name)
            AND (@p_schedule_origin_code IS NULL OR s.schedule_origin_code = @p_schedule_origin_code)
    ) END
) END;
