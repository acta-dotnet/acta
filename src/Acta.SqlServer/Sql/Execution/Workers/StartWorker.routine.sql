CREATE OR ALTER PROCEDURE {{schema}}.start_worker
    @p_name               VARCHAR(128),
    @p_owner_team         NVARCHAR(512),
    @p_description        NVARCHAR(512),
    @p_catalog_hash       VARCHAR(128),
    @p_status_code        TINYINT,
    @p_deployment_version VARCHAR(128),
    @p_host               VARCHAR(256),
    @p_engine_version       VARCHAR(128),
    @p_dotnet_version     VARCHAR(64),
    @p_process_id         INT,
    @p_max_concurrency    INT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @now DATETIME2(7) = SYSUTCDATETIME();
    DECLARE @ns_id SMALLINT;
    DECLARE @worker_id INT;

    BEGIN TRY
        BEGIN TRANSACTION;

        UPDATE {{schema}}.namespaces
           SET owner_team      = @p_owner_team,
               description     = @p_description,
               catalog_hash    = @p_catalog_hash,
               modified_at_utc = @now,
               version         = version + 1
         WHERE name = @p_name
           AND (catalog_hash <> @p_catalog_hash OR catalog_hash IS NULL);

        INSERT INTO {{schema}}.namespaces
            (name, owner_team, description, catalog_hash, status_code, created_at_utc, modified_at_utc, version)
        SELECT @p_name, @p_owner_team, @p_description, @p_catalog_hash, @p_status_code, @now, @now, 0
         WHERE NOT EXISTS (SELECT 1 FROM {{schema}}.namespaces WHERE name = @p_name);

        SELECT @ns_id = id FROM {{schema}}.namespaces WHERE name = @p_name;

        INSERT INTO {{schema}}.workers
            (namespace_id, status_code, deployment_version, host, engine_version, dotnet_version, process_id, max_concurrency, last_seen_at_utc, created_at_utc, modified_at_utc, version)
        VALUES
            (@ns_id, 10 /* WorkerStatusCode.Active */, @p_deployment_version, @p_host, @p_engine_version, @p_dotnet_version, @p_process_id, @p_max_concurrency, @now, @now, @now, 0);
        SET @worker_id = CAST(SCOPE_IDENTITY() AS INT);

        INSERT INTO {{schema}}.events (
            event_code, created_at_utc, namespace_id, actor_code, actor_key, job_id, execution_number,
            lineage_root_id, definition_id, worker_id, from_status_code, to_status_code,
            execution_status_code, duration_ms, reason_code, reason_message)
        VALUES (
            120 /* JobEventCode.WorkerStarted */, @now, @ns_id, 70 /* JobActorCode.Worker */, CAST(@worker_id AS VARCHAR(128)),
            NULL, NULL, NULL, NULL, @worker_id, NULL, NULL, NULL, NULL, NULL, NULL);

        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF XACT_STATE() <> 0
        BEGIN
            ROLLBACK TRANSACTION;
        END;

        THROW;
    END CATCH;

    SELECT @ns_id AS namespace_id, @worker_id AS worker_id;
END;
GO
