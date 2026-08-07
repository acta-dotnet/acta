CREATE OR ALTER PROCEDURE {{schema}}.register_scheduled_jobs
    @p_namespace_id SMALLINT,
    @p_definitions {{schema}}.job_schedule_slot_batch READONLY,
    @p_schedules {{schema}}.job_schedule_upsert_batch READONLY
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @now DATETIME2(7) = SYSUTCDATETIME();
    DECLARE @lock_result INT;
    DECLARE @lock_resource NVARCHAR(255) = N'{{schema}}.register_scheduled_jobs';

    BEGIN TRY
        BEGIN TRANSACTION;

        EXEC @lock_result = sys.sp_getapplock @lock_resource, 'Exclusive', 'Transaction';

        IF @lock_result < 0
            BEGIN
                THROW 50004, 'register_scheduled_jobs could not acquire the recurring slot lock.', 1;
            END

        UPDATE j
        SET
            input_format_id = d.input_format_id,
            input = d.input,
            audit_level_code = d.audit_level_code
        FROM {{schema}}.jobs AS j
        INNER JOIN @p_definitions AS d ON d.deduplication_key = j.deduplication_key
        WHERE
            j.namespace_id = @p_namespace_id
            AND j.parent_id IS NULL;

        -- Re-registration re-asserts the definition's declared priority onto the slot, overwriting any operator reprioritize.
        UPDATE r
        SET
            status_code = d.slot_status_code,
            priority_code = jd.priority_code_effective,
            next_run_at_utc = d.slot_next_run_at_utc,
            modified_at_utc = @now,
            version = r.version + 1
        FROM {{schema}}.runtimes AS r
        INNER JOIN {{schema}}.jobs AS j ON j.id = r.job_id
        INNER JOIN @p_definitions AS d ON d.deduplication_key = j.deduplication_key
        INNER JOIN {{schema}}.definitions AS jd ON jd.id = d.definition_id
        WHERE
            j.namespace_id = @p_namespace_id
            AND j.parent_id IS NULL;

        INSERT INTO {{schema}}.jobs (
            job_ref, lineage_root_id, parent_id, deduplication_key, correlation_key,
            namespace_id, definition_id,
            input_format_id, input,
            exclusive_key, audit_level_code,
            created_at_utc
        )
        SELECT
            d.job_ref,
            NULL,
            NULL,
            d.deduplication_key,
            NULL,
            @p_namespace_id,
            d.definition_id,
            d.input_format_id,
            d.input,
            NULL,
            d.audit_level_code,
            @now
        FROM @p_definitions AS d
        WHERE NOT EXISTS (
            SELECT 1
            FROM {{schema}}.jobs AS j
            WHERE
                j.namespace_id = @p_namespace_id
                AND j.deduplication_key = d.deduplication_key
                AND j.parent_id IS NULL
        );

        INSERT INTO {{schema}}.runtimes (
            job_id, namespace_id, status_code, priority_code, next_run_at_utc,
            execution_number, failure_count, retention_until_utc,
            modified_at_utc, version
        )
        SELECT
            j.id,
            @p_namespace_id,
            d.slot_status_code,
            jd.priority_code_effective,
            d.slot_next_run_at_utc,
            0,
            0,
            NULL,
            @now,
            0
        FROM @p_definitions AS d
        INNER JOIN {{schema}}.jobs AS j
            ON
                j.namespace_id = @p_namespace_id
                AND j.deduplication_key = d.deduplication_key
                AND j.parent_id IS NULL
        INNER JOIN {{schema}}.definitions AS jd ON jd.id = d.definition_id
        WHERE NOT EXISTS (
            SELECT 1 FROM {{schema}}.runtimes AS r
            WHERE r.job_id = j.id
        );

        DECLARE @slots TABLE (definition_id INT NOT NULL PRIMARY KEY, slot_id BIGINT NOT NULL);

        INSERT INTO @slots (definition_id, slot_id)
        SELECT
            d.definition_id,
            j.id
        FROM @p_definitions AS d
        INNER JOIN {{schema}}.jobs AS j
            ON
                j.namespace_id = @p_namespace_id
                AND j.deduplication_key = d.deduplication_key
                AND j.parent_id IS NULL;

        UPDATE tgt
        SET
            expression = src.expression,
            time_zone_id = src.time_zone_id,
            expression_kind_code = src.expression_kind_code,
            misfire_strategy_code = src.misfire_strategy_code,
            next_run_at_utc = src.next_run_at_utc,
            definition_id = src.definition_id,
            status_code = CASE
                WHEN tgt.status_code = 230 /* ScheduleStatusCode.Orphaned */
                    THEN 10 /* ScheduleStatusCode.Active */
                ELSE tgt.status_code
            END,
            description = src.description,
            modified_at_utc = @now,
            version = tgt.version + 1
        FROM {{schema}}.schedules AS tgt
        INNER JOIN @slots AS sl ON sl.slot_id = tgt.job_id
        INNER JOIN @p_schedules AS src
            ON
                src.definition_id = sl.definition_id
                AND src.name = tgt.name;

        INSERT INTO {{schema}}.schedules (
            namespace_id, job_id, definition_id, name, origin_code,
            expression, time_zone_id, expression_kind_code, misfire_strategy_code,
            next_run_at_utc, expression_override, time_zone_id_override,
            status_code, paused_until_utc, description,
            created_at_utc, modified_at_utc, version
        )
        SELECT
            @p_namespace_id,
            sl.slot_id,
            src.definition_id,
            src.name,
            40 /* ScheduleOriginCode.Definition */,
            src.expression,
            src.time_zone_id,
            src.expression_kind_code,
            src.misfire_strategy_code,
            src.next_run_at_utc,
            NULL,
            NULL,
            10 /* ScheduleStatusCode.Active */,
            NULL,
            src.description,
            @now,
            @now,
            0
        FROM @p_schedules AS src
        INNER JOIN @slots AS sl ON sl.definition_id = src.definition_id
        WHERE NOT EXISTS (
            SELECT 1
            FROM {{schema}}.schedules AS tgt
            WHERE
                tgt.job_id = sl.slot_id
                AND tgt.name = src.name
        );

        DECLARE @candidates TABLE (id BIGINT NOT NULL PRIMARY KEY);

        INSERT INTO @candidates (id)
        SELECT js.id
        FROM {{schema}}.schedules AS js
        INNER JOIN @slots AS sl ON sl.slot_id = js.job_id
        WHERE
            js.status_code <> 230 /* ScheduleStatusCode.Orphaned */
            AND js.origin_code = 40 /* ScheduleOriginCode.Definition */
            AND NOT EXISTS (
                SELECT 1
                FROM @p_schedules AS src
                WHERE
                    src.definition_id = sl.definition_id
                    AND src.name = js.name
            );

        UPDATE js
        SET
            status_code = 230 /* ScheduleStatusCode.Orphaned */,
            paused_until_utc = NULL,
            modified_at_utc = @now,
            version = js.version + 1
        FROM {{schema}}.schedules AS js
        INNER JOIN @candidates AS c ON c.id = js.id;

        COMMIT TRANSACTION;

        SELECT
            definition_id,
            slot_id
        FROM @slots;
    END TRY
    BEGIN CATCH
        IF XACT_STATE() <> 0
            BEGIN
                ROLLBACK TRANSACTION;
            END;

        THROW;
    END CATCH;
END;
GO
