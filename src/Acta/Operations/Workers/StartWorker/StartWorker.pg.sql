/* Update first, insert only when the name is absent. An upsert cannot be used here: PostgreSQL
   evaluates the identity default before it detects the conflict, so every worker start burned a
   namespaces.namespace_id even when the namespace already existed. A zero-row insert calls nextval never. */
WITH ns_update AS (
    UPDATE {{schema}}.namespaces SET
        owner_team = @p_owner_team,
        description = @p_description,
        modified_at_utc = now(),
        version = version + 1
    WHERE
        namespace_name = @p_namespace_name
        AND (owner_team IS DISTINCT FROM @p_owner_team OR description IS DISTINCT FROM @p_description)
    RETURNING namespace_id
),
ns_insert AS (
    INSERT INTO {{schema}}.namespaces (
        namespace_name,
        owner_team,
        description,
        namespace_status_code,
        created_at_utc,
        modified_at_utc,
        version)
    SELECT
        @p_namespace_name,
        @p_owner_team,
        @p_description,
        @p_namespace_status_code,
        now(),
        now(),
        0
    WHERE NOT EXISTS (
        SELECT 1 FROM {{schema}}.namespaces
        WHERE namespace_name = @p_namespace_name
    )
    ON CONFLICT (namespace_name) DO NOTHING
    RETURNING namespace_id
),
ns AS (
    SELECT namespace_id, CAST(NULL AS VARCHAR(128)) AS catalog_hash FROM ns_insert
    UNION ALL
    SELECT namespace_id, catalog_hash FROM {{schema}}.namespaces
    WHERE
        namespace_name = @p_namespace_name
        AND NOT EXISTS (SELECT 1 FROM ns_insert)
),
w AS (
    INSERT INTO {{schema}}.workers (
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
        version)
    SELECT
        ns.namespace_id,
        @p_worker_ref,
        10 /* WorkerStatusCode.Active */,
        @p_deployment_version,
        @p_host_name,
        @p_engine_version,
        @p_dotnet_version,
        @p_process_id,
        @p_max_concurrent_executors,
        now(),
        now(),
        now(),
        0
    FROM ns
    RETURNING worker_id, namespace_id
),
evt AS (
    INSERT INTO {{schema}}.events (
        event_code,
        created_at_utc,
        namespace_id,
        worker_id,
        entity_code,
        entity_id)
    SELECT
        120 /* EventCode.WorkerStarted */,
        now(),
        w.namespace_id,
        w.worker_id,
        130 /* EntityCode.Worker */,
        w.worker_id
    FROM w
    RETURNING 1
)
SELECT w.namespace_id AS namespace_id, w.worker_id, ns.catalog_hash FROM w INNER JOIN ns ON ns.namespace_id = w.namespace_id;
