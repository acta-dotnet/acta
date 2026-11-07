/* One statement: the namespace is read, and the worker row and its event are written only when this version may run
   there. A namespace no deploy has recorded takes any version; a recorded one takes the versions in deployments. No
   row comes back for a name that is absent, and worker_id is NULL for a version that may not run. */
WITH ns AS (
    SELECT n.namespace_id, n.deployment_version
    FROM {{schema}}.namespaces n
    WHERE n.namespace_name = @p_namespace_name
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
    WHERE
        ns.deployment_version IS NULL
        OR NOT @p_requires_deployment
        OR EXISTS (
            SELECT 1 FROM {{schema}}.deployments d
            WHERE d.namespace_id = ns.namespace_id AND d.deployment_version = @p_deployment_version
        )
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
SELECT ns.namespace_id, w.worker_id, ns.deployment_version AS current_deployment_version
FROM ns
LEFT JOIN w ON w.namespace_id = ns.namespace_id;
