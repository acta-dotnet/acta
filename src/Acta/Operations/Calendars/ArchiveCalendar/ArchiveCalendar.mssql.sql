SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @entry_trancount INT = @@TRANCOUNT;
DECLARE @now DATETIME2(7) = SYSUTCDATETIME();
DECLARE @namespace_id INT, @calendar_id INT, @version INT, @dependent_count INT = 0, @archived BIT = 0;
DECLARE @lock_result INT, @lock_resource NVARCHAR(255);

BEGIN TRY
    IF @entry_trancount = 0
        BEGIN TRANSACTION;

    SELECT @namespace_id = n.namespace_id
    FROM {{schema}}.namespaces n
    WHERE n.namespace_name = @p_namespace_name;

    IF @namespace_id IS NOT NULL
        BEGIN
            /* The namespace's schedule lock, exclusive, comes first (docs/sql-internals.md, "Schedule lock"), so no
               registration binds the family between the count and the archive. */
            SET @lock_resource = N'{{schema}}.schedules.' + CAST(@namespace_id AS NVARCHAR(12));
            EXEC @lock_result = sys.sp_getapplock @lock_resource, 'Exclusive', 'Transaction', -1;
            IF @lock_result < 0
                THROW 51205, 'ACTA:SCHEDULE_LOCK:The namespace schedule lock was not granted (deadlock victim or cancelled).', 1;

            SELECT @calendar_id = c.calendar_id, @version = c.version
            FROM {{schema}}.calendars c
            WHERE
                c.namespace_id = @namespace_id
                AND c.calendar_name = @p_calendar_name
                AND c.calendar_status_code = 20 /* CalendarStatusCode.Published */;
        END;

    IF @calendar_id IS NOT NULL
        BEGIN
            SELECT @dependent_count = COUNT(*)
            FROM {{schema}}.schedules s
            INNER JOIN {{schema}}.definitions d ON d.definition_id = s.definition_id
            WHERE
                (s.namespace_id = @namespace_id OR @namespace_id = 1 /* NameSyntax.SystemNamespaceId */)
                AND s.schedule_status_code <> 230 /* ScheduleStatusCode.Orphaned */
                AND d.definition_status_code <> 240 /* DefinitionStatusCode.Retired */
                AND (
                    (
                        s.expression_kind_code = 30 /* ScheduleExpressionKindCode.Calendar */
                        AND s.expression = @p_calendar_name
                        AND s.calendar_origin_code = 20 /* CalendarOriginCode.Catalog */
                    )
                    OR (
                        s.availability_calendar_name = @p_calendar_name
                        AND s.availability_calendar_origin_code = 20 /* CalendarOriginCode.Catalog */
                    )
                );

            IF @version = @p_expected_current_version AND @dependent_count = 0
                BEGIN
                    UPDATE {{schema}}.calendars
                    SET calendar_status_code = 240 /* CalendarStatusCode.Archived */, modified_at_utc = @now
                    WHERE calendar_id = @calendar_id;
                    SET @archived = 1;

                    INSERT INTO {{schema}}.events (
                        event_code, created_at_utc, namespace_id,
                        entity_code, entity_id,
                        event_message, details_format_id, details
                    )
                    VALUES (
                        112 /* EventCode.CalendarArchived */, @now, @namespace_id,
                        15 /* EntityCode.Calendar */, @calendar_id,
                        @p_event_message, @p_details_format_id, @p_details
                    );
                END;
        END;

    SELECT
        CAST(
            CASE
                WHEN @calendar_id IS NULL THEN 2 /* ControlAction.NotFound */
                WHEN @archived = 0 THEN 3 /* ControlAction.Rejected */
                ELSE 1 /* ControlAction.Applied */
            END AS SMALLINT
        ) AS action,
        @version AS version,
        @dependent_count AS dependent_count;

    IF @entry_trancount = 0
        COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @entry_trancount = 0 AND XACT_STATE() <> 0
        ROLLBACK TRANSACTION;
    THROW;
END CATCH;
