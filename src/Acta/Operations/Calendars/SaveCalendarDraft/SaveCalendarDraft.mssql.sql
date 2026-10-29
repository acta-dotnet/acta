SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @entry_trancount INT = @@TRANCOUNT;
DECLARE @now DATETIME2(7) = SYSUTCDATETIME();
DECLARE @namespace_id INT, @latest_version INT, @family_role TINYINT, @calendar_id INT, @clash_namespace VARCHAR(64);
DECLARE @lock_result INT, @lock_resource NVARCHAR(255);

BEGIN TRY
    IF @entry_trancount = 0
        BEGIN TRANSACTION;

    -- A namespace no worker has run in yet is created here, so a calendar can be drafted before the first deploy.
    INSERT INTO {{schema}}.namespaces (namespace_name, namespace_status_code, created_at_utc, modified_at_utc, version)
    SELECT @p_namespace_name, 10 /* NamespaceStatusCode.Active */, @now, @now, 0
    WHERE NOT EXISTS (SELECT 1 FROM {{schema}}.namespaces n WHERE n.namespace_name = @p_namespace_name);

    SELECT @namespace_id = n.namespace_id
    FROM {{schema}}.namespaces n
    WHERE n.namespace_name = @p_namespace_name;

    /* The namespace's schedule lock, exclusive, serializes every catalog write in the namespace (docs/sql-internals.md,
       "Schedule lock"), so the latest version read below is still the family's latest when the draft lands. */
    SET @lock_resource = N'{{schema}}.schedules.' + CAST(@namespace_id AS NVARCHAR(12));
    EXEC @lock_result = sys.sp_getapplock @lock_resource, 'Exclusive', 'Transaction', -1;
    IF @lock_result < 0
        THROW 51205, 'ACTA:SCHEDULE_LOCK:The namespace schedule lock was not granted (deadlock victim or cancelled).', 1;

    SELECT @latest_version = COALESCE(MAX(c.version), 0), @family_role = MIN(c.calendar_role_code)
    FROM {{schema}}.calendars c
    WHERE
        c.namespace_id = @namespace_id
        AND c.calendar_name = @p_calendar_name;

    /* A name is one family: a namespace's calendar cannot take a shared calendar's name, and a shared one cannot take a
       name any namespace holds, so a schedule's plain name never resolves two ways. */
    SELECT @clash_namespace = MIN(o.namespace_name)
    FROM {{schema}}.calendars x
    INNER JOIN {{schema}}.namespaces o ON o.namespace_id = x.namespace_id
    WHERE
        x.calendar_name = @p_calendar_name
        AND x.namespace_id <> @namespace_id
        AND (@namespace_id = 1 /* NameSyntax.SystemNamespaceId */ OR x.namespace_id = 1 /* NameSyntax.SystemNamespaceId */);

    IF @latest_version = @p_expected_latest_version
        AND (@family_role IS NULL OR @family_role = @p_calendar_role_code)
        AND @clash_namespace IS NULL
        BEGIN
            INSERT INTO {{schema}}.calendars (
                namespace_id,
                calendar_name,
                version,
                calendar_role_code,
                definition_format_id,
                definition,
                content_hash,
                calendar_status_code,
                created_at_utc,
                modified_at_utc
            )
            VALUES (
                @namespace_id,
                @p_calendar_name,
                @p_expected_latest_version + 1,
                @p_calendar_role_code,
                @p_definition_format_id,
                @p_definition,
                @p_content_hash,
                10 /* CalendarStatusCode.Draft */,
                @now,
                @now
            );
            SET @calendar_id = CAST(SCOPE_IDENTITY() AS INT);

            INSERT INTO {{schema}}.events (
                event_code, created_at_utc, namespace_id,
                entity_code, entity_id,
                event_message, details_format_id, details
            )
            VALUES (
                110 /* EventCode.CalendarDraftSaved */, @now, @namespace_id,
                15 /* EntityCode.Calendar */, @calendar_id,
                @p_event_message, @p_details_format_id, @p_details
            );
        END;

    SELECT
        CAST(
            CASE
                WHEN @calendar_id IS NOT NULL THEN 1 /* ControlAction.Applied */
                WHEN @latest_version <> @p_expected_latest_version THEN 5 /* ControlAction.VersionConflict */
                ELSE 3 /* ControlAction.Rejected */
            END AS SMALLINT
        ) AS action,
        CASE WHEN @calendar_id IS NULL THEN @latest_version ELSE @p_expected_latest_version + 1 END AS version,
        CASE WHEN @calendar_id IS NULL THEN NULL ELSE @p_content_hash END AS content_hash,
        @family_role AS family_role,
        @clash_namespace AS clash_namespace;

    IF @entry_trancount = 0
        COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @entry_trancount = 0 AND XACT_STATE() <> 0
        ROLLBACK TRANSACTION;
    THROW;
END CATCH;
