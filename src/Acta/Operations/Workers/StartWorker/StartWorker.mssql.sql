SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @entry_trancount INT = @@TRANCOUNT;
BEGIN TRY
    IF @entry_trancount = 0
        BEGIN TRANSACTION;

    DECLARE @now DATETIME2(7) = SYSUTCDATETIME();
    DECLARE @ns_id INT;
    DECLARE @current_deployment_version VARCHAR(128);
    DECLARE @worker_id INT;

    SELECT @ns_id = namespace_id, @current_deployment_version = deployment_version
    FROM {{schema}}.namespaces
    WHERE namespace_name = @p_namespace_name;

    -- The worker row and its event are written only when this version may run in the namespace: one no deploy has
    -- recorded takes any version, a recorded one takes the versions in deployments. No row comes back for a name that
    -- is absent, and worker_id is NULL for a version that may not run.
    IF @ns_id IS NOT NULL
        AND (
            @current_deployment_version IS NULL
            OR @p_requires_deployment = 0
            OR EXISTS (
                SELECT 1 FROM {{schema}}.deployments d
                WHERE d.namespace_id = @ns_id AND d.deployment_version = @p_deployment_version
            )
        )
    BEGIN
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
    END;

    SELECT
        @ns_id AS namespace_id,
        @worker_id AS worker_id,
        @current_deployment_version AS current_deployment_version
    WHERE @ns_id IS NOT NULL;

    IF @entry_trancount = 0
        COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @entry_trancount = 0 AND XACT_STATE() <> 0
        ROLLBACK TRANSACTION;
    THROW;
END CATCH;
