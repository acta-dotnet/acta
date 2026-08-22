-- Scoped upsert, last write wins: targets resolve the scope (none = Global, namespace alone =
-- Namespace, namespace + job name = Definition); unregistered targets are NotFound. UPDLOCK+HOLDLOCK
-- serializes same-key writers, so the guarded INSERT cannot lose a race. Name validated upstream.
CREATE OR ALTER PROCEDURE {{schema}}.set_setting
    @p_name VARCHAR(128),
    @p_value_format_id TINYINT,
    @p_value VARBINARY(MAX),
    @p_description NVARCHAR(512),
    @p_namespace_name VARCHAR(128),
    @p_job_name VARCHAR(128),
    @p_actor_code TINYINT,
    @p_actor_key VARCHAR(128),
    @p_reason_message NVARCHAR(512),
    @p_expected_version INT = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    DECLARE @now DATETIME2(7) = SYSUTCDATETIME();
    DECLARE @scope_code SMALLINT = 10 /* SettingScopeCode.Global */;
    DECLARE @namespace_id INT, @definition_id INT, @scope_id INT, @version INT;

    IF @p_namespace_name IS NOT NULL
        BEGIN
            SELECT @namespace_id = n.id FROM {{schema}}.namespaces n
            WHERE n.name = @p_namespace_name;
            IF @namespace_id IS NULL
                BEGIN
                    SELECT
                        CAST(2 /* AdminControlAction.NotFound */ AS SMALLINT) AS action,
                        CAST(NULL AS INT) AS version;
                    RETURN;
                END;
            IF @p_job_name IS NULL
                BEGIN
                    SET @scope_code = 30 /* SettingScopeCode.Namespace */; SET @scope_id = @namespace_id;
                END
            ELSE
                BEGIN
                    SELECT @definition_id = d.id
                    FROM {{schema}}.definitions d
                    WHERE d.namespace_id = @namespace_id AND d.name = @p_job_name;
                    IF @definition_id IS NULL
                        BEGIN
                            SELECT
                                CAST(2 /* AdminControlAction.NotFound */ AS SMALLINT) AS action,
                                CAST(NULL AS INT) AS version;
                            RETURN;
                        END;
                    SET @scope_code = 40 /* SettingScopeCode.Definition */; SET @scope_id = @definition_id;
                END;
        END;

    BEGIN TRY
        BEGIN TRANSACTION;

        SELECT @version = s.version
        FROM {{schema}}.settings s WITH (UPDLOCK, HOLDLOCK, ROWLOCK)
        WHERE
            s.scope_code = @scope_code
            AND ((@scope_id IS NULL AND s.scope_id IS NULL) OR s.scope_id = @scope_id)
            AND s.name = @p_name;

        -- CAS misses never create and never write the event: report the row as it stands.
        IF @p_expected_version IS NOT NULL AND @version IS NULL
            BEGIN
                ROLLBACK TRANSACTION;
                SELECT
                    CAST(2 /* AdminControlAction.NotFound */ AS SMALLINT) AS action,
                    CAST(NULL AS INT) AS version;
                RETURN;
            END;
        IF @p_expected_version IS NOT NULL AND @version <> @p_expected_version
            BEGIN
                ROLLBACK TRANSACTION;
                SELECT
                    CAST(4 /* AdminControlAction.VersionConflict */ AS SMALLINT) AS action,
                    @version AS version;
                RETURN;
            END;

        IF @version IS NULL
            BEGIN
                INSERT INTO {{schema}}.settings
                (scope_code, scope_id, name, value_format_id, value, description, created_at_utc, modified_at_utc, version)
                VALUES (@scope_code, @scope_id, @p_name, @p_value_format_id, @p_value, @p_description, @now, @now, 0);
                SET @version = 0;
            END
        ELSE
            BEGIN
                UPDATE {{schema}}.settings
                SET
                    value_format_id = @p_value_format_id, value = @p_value, description = @p_description,
                    modified_at_utc = @now, version = version + 1
                WHERE
                    scope_code = @scope_code
                    AND ((@scope_id IS NULL AND scope_id IS NULL) OR scope_id = @scope_id)
                    AND name = @p_name;
                SET @version = @version + 1;
            END;

        -- namespace_id 1 is the seeded sys namespace (M001); detail identifies the setting by name.
        INSERT INTO {{schema}}.events (
            event_code, created_at_utc, namespace_id, actor_code, actor_key,
            job_id, job_ref, execution_number, lineage_root_id, definition_id, tenant_id, worker_id,
            from_status_code, to_status_code, execution_status_code, duration_ms, reason_code, reason_message,
            detail_format_id, detail
        )
        VALUES (
            160 /* EventCode.SettingUpdated */, @now, COALESCE(@namespace_id, 1), @p_actor_code, @p_actor_key,
            NULL, NULL, NULL, NULL, @definition_id, NULL, NULL,
            NULL, NULL, NULL, NULL, NULL, @p_reason_message,
            1 /* JobPayloadFormat.Json */, CONVERT(VARBINARY(MAX), '{"name":"' + @p_name + '"}')
        );

        COMMIT TRANSACTION;
        SELECT
            CAST(1 /* AdminControlAction.Applied */ AS SMALLINT) AS action,
            @version AS version;
    END TRY
    BEGIN CATCH
        IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
        THROW;
    END CATCH;
END;
GO
