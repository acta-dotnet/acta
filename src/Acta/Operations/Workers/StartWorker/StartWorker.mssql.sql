SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @entry_trancount INT = @@TRANCOUNT;
BEGIN TRY
    IF @entry_trancount = 0
        BEGIN TRANSACTION;

    DECLARE @now DATETIME2(7) = SYSUTCDATETIME();
    DECLARE @ns_id INT;
    DECLARE @worker_id INT;
    DECLARE @catalog_hash VARCHAR(128);

    -- An unchanged owner and description write nothing: EXCEPT compares NULL-safely, and the binary collation sees a case edit.
    UPDATE n
    SET
        owner_team = @p_owner_team,
        description = @p_description,
        modified_at_utc = @now,
        version = n.version + 1
    FROM {{schema}}.namespaces AS n
    WHERE
        n.namespace_name = @p_namespace_name
        AND EXISTS (
            SELECT n.owner_team COLLATE Latin1_General_100_BIN2, n.description COLLATE Latin1_General_100_BIN2
            EXCEPT
            SELECT @p_owner_team, @p_description
        );

    INSERT INTO {{schema}}.namespaces
    (namespace_name, owner_team, description, namespace_status_code, created_at_utc, modified_at_utc, version)
    SELECT
        @p_namespace_name,
        @p_owner_team,
        @p_description,
        @p_namespace_status_code,
        @now,
        @now,
        0
    WHERE NOT EXISTS (
        SELECT 1 FROM {{schema}}.namespaces
        WHERE namespace_name = @p_namespace_name
    );

    SELECT @ns_id = namespace_id, @catalog_hash = catalog_hash FROM {{schema}}.namespaces
    WHERE namespace_name = @p_namespace_name;

    INSERT INTO {{schema}}.workers
    (
        namespace_id,
        worker_ref,
        worker_status_code,
        deployment_version,
        host_name,
        engine_version,
        dotnet_version,
        process_id,
        max_concurrent_executors,
        last_seen_at_utc,
        created_at_utc,
        modified_at_utc,
        version
    )
    VALUES
    (
        @ns_id,
        @p_worker_ref,
        10 /* WorkerStatusCode.Active */,
        @p_deployment_version,
        @p_host_name,
        @p_engine_version,
        @p_dotnet_version,
        @p_process_id,
        @p_max_concurrent_executors,
        @now,
        @now,
        @now,
        0
    );
    SET @worker_id = CAST(SCOPE_IDENTITY() AS INT);

    INSERT INTO {{schema}}.events (event_code, created_at_utc, namespace_id, worker_id, entity_code, entity_id)
    VALUES (120 /* EventCode.WorkerStarted */, @now, @ns_id, @worker_id, 130 /* EntityCode.Worker */, @worker_id);

    SELECT
        @ns_id AS namespace_id,
        @worker_id AS worker_id,
        @catalog_hash AS catalog_hash;

    IF @entry_trancount = 0
        COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @entry_trancount = 0 AND XACT_STATE() <> 0
        ROLLBACK TRANSACTION;
    THROW;
END CATCH;
