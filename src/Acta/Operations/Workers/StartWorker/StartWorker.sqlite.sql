/* The worker row and its event are written only when this version may run in the namespace: one no deploy has
   recorded takes any version, a recorded one takes the versions in deployments. No row comes back for a name that is
   absent, and worker_id is NULL for a version that may not run. */
INSERT INTO {{schema}}.workers
(namespace_id, worker_ref, worker_status_code, deployment_version, host_name, engine_version, dotnet_version, process_id, max_concurrent_executors, last_seen_at_utc)
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
    {{now}}
FROM {{schema}}.namespaces ns
WHERE
    ns.namespace_name = @p_namespace_name
    AND (
        ns.deployment_version IS NULL
        OR @p_requires_deployment = 0
        OR EXISTS (
            SELECT 1 FROM {{schema}}.deployments d
            WHERE d.namespace_id = ns.namespace_id AND d.deployment_version = @p_deployment_version
        )
    );

INSERT INTO {{schema}}.events (
    event_code,
    namespace_id,
    worker_id,
    entity_code,
    entity_id)
SELECT
    120 /* EventCode.WorkerStarted */,
    w.namespace_id,
    w.worker_id,
    130 /* EntityCode.Worker */,
    w.worker_id
FROM {{schema}}.workers w
WHERE w.worker_ref = @p_worker_ref;

SELECT
    ns.namespace_id,
    w.worker_id,
    ns.deployment_version AS current_deployment_version
FROM {{schema}}.namespaces ns
LEFT JOIN {{schema}}.workers w ON w.worker_ref = @p_worker_ref
WHERE ns.namespace_name = @p_namespace_name;
