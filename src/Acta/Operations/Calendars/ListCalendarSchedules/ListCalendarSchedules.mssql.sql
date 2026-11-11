-- Every schedule naming the family, as the admitter's row, so a publication plans it as a pass evaluates it; orphaned
-- ones included, as their cursors move too, a retired definition's left out, as its reactivating start retimes them.
-- A shared family in sys binds the name in every namespace. No locks; the apply validates the versions it carries.
WITH named AS (
    -- A namespace's family binds its own schedules only, which the namespace's definitions reach by key, so with
    -- read-committed snapshot off the read waits on no other namespace's rows.
    SELECT ns.schedule_id
    FROM {{schema}}.definitions nd WITH (FORCESEEK (ux_definitions_namespace_name (namespace_id)))
    INNER JOIN {{schema}}.schedules ns WITH (FORCESEEK (ux_schedules_definition_name (definition_id)))
        ON ns.definition_id = nd.definition_id
    WHERE
        @p_namespace_id <> 1 /* NameSyntax.SystemNamespaceId */
        AND nd.namespace_id = @p_namespace_id
        AND (
            (ns.expression_kind_code = 30 /* ScheduleExpressionKindCode.Calendar */ AND ns.expression = @p_calendar_name)
            OR ns.availability_calendar_name = @p_calendar_name
        )
    UNION ALL
    -- A shared family's schedules are every namespace's, which no index finds, so the scan skips a row another
    -- transaction holds; the publication's own check under its schedule lock rejects a plan that missed one.
    SELECT ss.schedule_id
    FROM {{schema}}.schedules ss WITH (READCOMMITTEDLOCK, READPAST, ROWLOCK)
    WHERE
        @p_namespace_id = 1 /* NameSyntax.SystemNamespaceId */
        AND (
            (ss.expression_kind_code = 30 /* ScheduleExpressionKindCode.Calendar */ AND ss.expression = @p_calendar_name)
            OR ss.availability_calendar_name = @p_calendar_name
        )
)
SELECT
    s.schedule_id,
    s.namespace_id,
    n.namespace_name,
    s.job_id,
    s.schedule_name,
    d.definition_name,
    s.expression,
    s.time_zone_id,
    s.expression_kind_code AS expression_kind,
    s.misfire_strategy_code AS misfire_strategy,
    s.interval_anchor_at_utc,
    s.availability_calendar_name,
    s.start_at_utc,
    s.end_before_utc,
    s.max_outstanding,
    s.full_behavior_code AS full_behavior,
    s.expire_after_seconds,
    s.expire_action_code AS expire_action,
    s.schedule_status_code AS status,
    s.paused_until_utc,
    s.version AS schedule_version,
    st.next_occurrence_at_utc,
    st.next_evaluation_at_utc,
    st.evaluation_status_code AS evaluation_status,
    st.outstanding_count,
    st.version AS state_version,
    r.job_status_code AS slot_status,
    r.version AS slot_version,
    cp.checkpoint_status_code AS firing_status
FROM named
INNER JOIN {{schema}}.schedules s WITH (FORCESEEK) ON s.schedule_id = named.schedule_id
INNER JOIN {{schema}}.schedules_state st WITH (FORCESEEK) ON st.schedule_id = s.schedule_id
INNER JOIN {{schema}}.definitions d WITH (FORCESEEK) ON d.definition_id = s.definition_id
INNER JOIN {{schema}}.namespaces n WITH (FORCESEEK) ON n.namespace_id = s.namespace_id
LEFT JOIN {{schema}}.jobs_state r WITH (FORCESEEK) ON r.job_id = s.job_id
LEFT JOIN {{schema}}.checkpoints cp WITH (FORCESEEK)
    ON cp.job_id = s.job_id AND cp.checkpoint_kind_code = 60 /* CheckpointKindCode.Firing */ AND cp.checkpoint_name = 'sys.firing'
WHERE d.definition_status_code <> 240 /* DefinitionStatusCode.Retired */
ORDER BY s.schedule_id;
