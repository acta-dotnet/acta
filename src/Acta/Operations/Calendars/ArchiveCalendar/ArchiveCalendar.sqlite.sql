-- SQLite runs one writer at a time, so no registration binds the family between the count and the archive.
DROP TABLE IF EXISTS temp._archive_calendar;

CREATE TEMP TABLE _archive_calendar AS
SELECT
    t.calendar_id,
    t.namespace_id,
    t.version,
    t.dependent_count,
    t.version = @p_expected_current_version AND t.dependent_count = 0 AS applies
FROM (
    SELECT
        c.calendar_id,
        c.namespace_id,
        c.version,
        (
            SELECT COUNT(*)
            FROM {{schema}}.schedules s
            INNER JOIN {{schema}}.definitions d ON d.definition_id = s.definition_id
            WHERE
                (s.namespace_id = c.namespace_id OR c.namespace_id = 1 /* NameSyntax.SystemNamespaceId */)
                AND s.schedule_status_code <> 230 /* ScheduleStatusCode.Orphaned */
                AND d.definition_status_code <> 240 /* DefinitionStatusCode.Retired */
                AND (
                    (
                        s.expression_kind_code = 30 /* ScheduleExpressionKindCode.Calendar */
                        AND s.expression = c.calendar_name
                        AND s.calendar_origin_code = 20 /* CalendarOriginCode.Catalog */
                    )
                    OR (
                        s.availability_calendar_name = c.calendar_name
                        AND s.availability_calendar_origin_code = 20 /* CalendarOriginCode.Catalog */
                    )
                )
        ) AS dependent_count
    FROM {{schema}}.namespaces n
    INNER JOIN {{schema}}.calendars c ON c.namespace_id = n.namespace_id
    WHERE
        n.namespace_name = @p_namespace_name
        AND c.calendar_name = @p_calendar_name
        AND c.calendar_status_code = 20 /* CalendarStatusCode.Published */
) t;

INSERT INTO {{schema}}.events (
    event_code,
    created_at_utc,
    namespace_id,
    entity_code,
    entity_id,
    event_message,
    details_format_id,
    details)
SELECT
    112 /* EventCode.CalendarArchived */,
    {{now}},
    t.namespace_id,
    15 /* EntityCode.Calendar */,
    t.calendar_id,
    @p_event_message,
    @p_details_format_id,
    @p_details
FROM temp._archive_calendar t
WHERE t.applies = 1;

UPDATE {{schema}}.calendars
SET calendar_status_code = 240 /* CalendarStatusCode.Archived */, modified_at_utc = {{now}}
WHERE calendar_id IN (SELECT t.calendar_id FROM temp._archive_calendar t WHERE t.applies = 1);

SELECT
    CASE
        WHEN t.calendar_id IS NULL THEN 2 /* ControlAction.NotFound */
        WHEN t.applies = 1 THEN 1 /* ControlAction.Applied */
        ELSE 3 /* ControlAction.Rejected */
    END AS action,
    t.version,
    COALESCE(t.dependent_count, 0) AS dependent_count
FROM (SELECT 1) one
LEFT JOIN temp._archive_calendar t ON 1 = 1;
