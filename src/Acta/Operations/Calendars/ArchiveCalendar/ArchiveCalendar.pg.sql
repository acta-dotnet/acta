-- One batch, one implicit transaction; only the final SELECT returns rows. The namespace's schedule lock, exclusive,
-- comes first (docs/sql-internals.md, "Schedule lock"), so no registration binds the family between the count and the
-- archive.
DROP TABLE IF EXISTS pg_temp._archive_calendar_namespace;
DROP TABLE IF EXISTS pg_temp._archive_calendar_lock;
DROP TABLE IF EXISTS pg_temp._archive_calendar_target;
CREATE TEMP TABLE _archive_calendar_namespace ON COMMIT DROP AS
SELECT n.namespace_id
FROM {{schema}}.namespaces n
WHERE n.namespace_name = @p_namespace_name;

CREATE TEMP TABLE _archive_calendar_lock ON COMMIT DROP AS
SELECT 1 AS granted
FROM (
    SELECT pg_advisory_xact_lock(hashtext('{{schema}}.schedules'), t.namespace_id)
    FROM _archive_calendar_namespace t
) l;

CREATE TEMP TABLE _archive_calendar_target ON COMMIT DROP AS
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
    )::INT AS dependent_count
FROM _archive_calendar_namespace t
INNER JOIN {{schema}}.calendars c ON c.namespace_id = t.namespace_id
WHERE
    c.calendar_name = @p_calendar_name
    AND c.calendar_status_code = 20 /* CalendarStatusCode.Published */;

WITH archived AS (
    UPDATE {{schema}}.calendars AS c
    SET calendar_status_code = 240 /* CalendarStatusCode.Archived */, modified_at_utc = now()
    FROM _archive_calendar_target t
    WHERE
        c.calendar_id = t.calendar_id
        AND t.version = @p_expected_current_version
        AND t.dependent_count = 0
    RETURNING c.calendar_id, c.namespace_id
),
audit AS (
    INSERT INTO {{schema}}.events (
        event_code, created_at_utc, namespace_id,
        entity_code, entity_id,
        event_message, details_format_id, details
    )
    SELECT
        112 /* EventCode.CalendarArchived */, now(), a.namespace_id,
        15 /* EntityCode.Calendar */, a.calendar_id,
        @p_event_message, @p_details_format_id, @p_details
    FROM archived a
)
SELECT
    CASE
        WHEN t.calendar_id IS NULL THEN 2 /* ControlAction.NotFound */
        WHEN a.calendar_id IS NULL THEN 3 /* ControlAction.Rejected */
        ELSE 1 /* ControlAction.Applied */
    END::SMALLINT AS action,
    t.version,
    COALESCE(t.dependent_count, 0) AS dependent_count
FROM (SELECT 1) one
LEFT JOIN _archive_calendar_target t ON true
LEFT JOIN archived a ON true;
