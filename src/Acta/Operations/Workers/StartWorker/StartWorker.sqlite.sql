/* Update first, insert only when the name is absent. An upsert cannot be used here: SQLite assigns
   the AUTOINCREMENT rowid and advances sqlite_sequence before it detects the conflict, so every
   worker start burned a namespaces.namespace_id. A zero-row insert allocates nothing. */
UPDATE {{schema}}.namespaces SET
    owner_team = @p_owner_team,
    description = @p_description,
    modified_at_utc = {{now}},
    version = version + 1
WHERE
    namespace_name = @p_namespace_name
    AND (owner_team IS NOT @p_owner_team OR description IS NOT @p_description);

INSERT INTO {{schema}}.namespaces (namespace_name, owner_team, description, namespace_status_code)
SELECT @p_namespace_name, @p_owner_team, @p_description, @p_namespace_status_code
WHERE NOT EXISTS (
    SELECT 1 FROM {{schema}}.namespaces
    WHERE namespace_name = @p_namespace_name
);

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
WHERE ns.namespace_name = @p_namespace_name;

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
    w.namespace_id AS namespace_id,
    w.worker_id,
    ns.catalog_hash
FROM {{schema}}.workers w
INNER JOIN {{schema}}.namespaces ns ON ns.namespace_id = w.namespace_id
WHERE w.worker_ref = @p_worker_ref;
