IF @p_live_only IS NOT NULL
    BEGIN
        SELECT TOP (@p_take)
            st.job_id,
            s.definition_id,
            ns.namespace_name AS job_namespace,
            jd.definition_name AS job_name,
            st.schedule_name,
            s.schedule_origin_code AS origin,
            s.expression_kind_code AS expression_kind,
            s.expression_effective AS expression,
            s.time_zone_id_effective AS time_zone_id,
            s.misfire_strategy_code AS misfire_strategy,
            st.next_occurrence_at_utc,
            st.last_advanced_occurrence_at_utc,
            s.schedule_status_code AS status,
            s.paused_until_utc,
            s.created_at_utc,
            s.modified_at_utc,
            s.version,
            sj.job_ref
        FROM {{schema}}.schedules_state st
        JOIN {{schema}}.schedules s ON s.job_id = st.job_id AND s.schedule_name = st.schedule_name
        JOIN {{schema}}.namespaces ns ON ns.namespace_id = st.namespace_id
        JOIN {{schema}}.definitions jd ON jd.definition_id = s.definition_id
        LEFT JOIN {{schema}}.jobs sj ON sj.job_id = st.job_id
        WHERE
            st.next_occurrence_at_utc IS NOT NULL
            AND s.schedule_status_code <> 230 /* ScheduleStatusCode.Orphaned */
            AND jd.definition_status_code <> 240 /* DefinitionStatusCode.Retired */
            AND (@p_namespace_name IS NULL OR ns.namespace_name = @p_namespace_name)
            AND (@p_job_name IS NULL OR jd.definition_name = @p_job_name)
            AND (@p_schedule_origin_code IS NULL OR s.schedule_origin_code = @p_schedule_origin_code)
            AND (
                @p_cursor_next_occurrence_at_utc IS NULL
                OR st.next_occurrence_at_utc > @p_cursor_next_occurrence_at_utc
                OR (st.next_occurrence_at_utc = @p_cursor_next_occurrence_at_utc AND st.job_id > @p_cursor_id)
                OR (
                    st.next_occurrence_at_utc = @p_cursor_next_occurrence_at_utc
                    AND st.job_id = @p_cursor_id
                    AND st.schedule_name > @p_cursor_schedule_name
                )
            )
        ORDER BY st.next_occurrence_at_utc ASC, st.job_id ASC, st.schedule_name ASC
        OPTION (RECOMPILE);

        SELECT
            CASE WHEN @p_include_total IS NOT NULL THEN (
                SELECT COUNT(*)
                FROM {{schema}}.schedules_state st
                JOIN {{schema}}.schedules s ON s.job_id = st.job_id AND s.schedule_name = st.schedule_name
                JOIN {{schema}}.namespaces ns ON ns.namespace_id = st.namespace_id
                JOIN {{schema}}.definitions jd ON jd.definition_id = s.definition_id
                WHERE
                    st.next_occurrence_at_utc IS NOT NULL
                    AND s.schedule_status_code <> 230 /* ScheduleStatusCode.Orphaned */
                    AND jd.definition_status_code <> 240 /* DefinitionStatusCode.Retired */
                    AND (@p_namespace_name IS NULL OR ns.namespace_name = @p_namespace_name)
                    AND (@p_job_name IS NULL OR jd.definition_name = @p_job_name)
                    AND (@p_schedule_origin_code IS NULL OR s.schedule_origin_code = @p_schedule_origin_code)
            ) END
        OPTION (RECOMPILE);
    END
ELSE
    BEGIN
        -- Every schedule, a NULL cursor included, so the full list pages by its key.
        SELECT TOP (@p_take)
            s.job_id,
            s.definition_id,
            ns.namespace_name AS job_namespace,
            jd.definition_name AS job_name,
            s.schedule_name,
            s.schedule_origin_code AS origin,
            s.expression_kind_code AS expression_kind,
            s.expression_effective AS expression,
            s.time_zone_id_effective AS time_zone_id,
            s.misfire_strategy_code AS misfire_strategy,
            st.next_occurrence_at_utc,
            st.last_advanced_occurrence_at_utc,
            s.schedule_status_code AS status,
            s.paused_until_utc,
            s.created_at_utc,
            s.modified_at_utc,
            s.version,
            sj.job_ref
        FROM {{schema}}.schedules s
        JOIN {{schema}}.namespaces ns ON ns.namespace_id = s.namespace_id
        JOIN {{schema}}.definitions jd ON jd.definition_id = s.definition_id
        LEFT JOIN {{schema}}.schedules_state st ON st.job_id = s.job_id AND st.schedule_name = s.schedule_name
        LEFT JOIN {{schema}}.jobs sj ON sj.job_id = s.job_id
        WHERE
            (@p_namespace_name IS NULL OR ns.namespace_name = @p_namespace_name)
            AND (@p_job_name IS NULL OR jd.definition_name = @p_job_name)
            AND (@p_schedule_origin_code IS NULL OR s.schedule_origin_code = @p_schedule_origin_code)
            AND (
                @p_cursor_id IS NULL
                OR s.job_id > @p_cursor_id
                OR (s.job_id = @p_cursor_id AND s.schedule_name > @p_cursor_schedule_name)
            )
        ORDER BY s.job_id ASC, s.schedule_name ASC
        OPTION (RECOMPILE);

        SELECT
            CASE WHEN @p_include_total IS NOT NULL THEN (
                SELECT COUNT(*)
                FROM {{schema}}.schedules s
                JOIN {{schema}}.namespaces ns ON ns.namespace_id = s.namespace_id
                JOIN {{schema}}.definitions jd ON jd.definition_id = s.definition_id
                WHERE
                    (@p_namespace_name IS NULL OR ns.namespace_name = @p_namespace_name)
                    AND (@p_job_name IS NULL OR jd.definition_name = @p_job_name)
                    AND (@p_schedule_origin_code IS NULL OR s.schedule_origin_code = @p_schedule_origin_code)
            ) END
        OPTION (RECOMPILE);
    END;
