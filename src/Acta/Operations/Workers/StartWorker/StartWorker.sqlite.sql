/* Update first, insert only when the name is absent. An upsert cannot be used here: SQLite assigns
   the AUTOINCREMENT rowid and advances sqlite_sequence before it detects the conflict, so every
   worker start burned a namespaces.id. A zero-row insert allocates nothing. */
UPDATE {{schema}}.namespaces SET
    owner_team = @p_owner_team,
    description = @p_description,
    catalog_hash = @p_catalog_hash,
    modified_at_utc = {{now}},
    version = version + 1
WHERE
    name = @p_name
    AND catalog_hash IS NOT @p_catalog_hash;

INSERT INTO {{schema}}.namespaces (name, owner_team, description, catalog_hash, status_code)
SELECT @p_name, @p_owner_team, @p_description, @p_catalog_hash, @p_status_code
WHERE NOT EXISTS (
    SELECT 1 FROM {{schema}}.namespaces
    WHERE name = @p_name
);

INSERT INTO {{schema}}.workers
(namespace_id, worker_ref, status_code, deployment_version, host, engine_version, dotnet_version, process_id, max_concurrency, last_seen_at_utc)
SELECT
    ns.id,
    @p_worker_ref,
    10 /* WorkerStatusCode.Active */,
    @p_deployment_version,
    @p_host,
    @p_engine_version,
    @p_dotnet_version,
    @p_process_id,
    @p_max_concurrency,
    {{now}}
FROM {{schema}}.namespaces ns
WHERE ns.name = @p_name;

INSERT INTO {{schema}}.events (
    event_code, namespace_id, actor_code, actor_key, job_id, execution_number,
    lineage_root_id, definition_id, worker_id, from_status_code, to_status_code,
    execution_status_code, duration_ms, reason_code, reason_message
)
SELECT
    120 /* EventCode.WorkerStarted */,
    w.namespace_id,
    70 /* ActorCode.Worker */,
    w.worker_ref,
    NULL,
    NULL,
    NULL,
    NULL,
    w.id,
    NULL,
    NULL,
    NULL,
    NULL,
    NULL,
    NULL
FROM {{schema}}.workers w
WHERE w.worker_ref = @p_worker_ref;

SELECT
    w.namespace_id AS namespace_id,
    w.id AS worker_id
FROM {{schema}}.workers w
WHERE w.worker_ref = @p_worker_ref;
