INSERT INTO {{schema}}.namespaces (name, owner_team, description, catalog_hash, status_code)
VALUES (@p_name, @p_owner_team, @p_description, @p_catalog_hash, @p_status_code)
ON CONFLICT (name) DO UPDATE SET
    owner_team      = excluded.owner_team,
    description     = excluded.description,
    catalog_hash    = excluded.catalog_hash,
    modified_at_utc = {{now}},
    version         = {{schema}}.namespaces.version + 1
  WHERE {{schema}}.namespaces.catalog_hash IS NOT excluded.catalog_hash;

INSERT INTO {{schema}}.workers
    (namespace_id, status_code, deployment_version, host, engine_version, dotnet_version, process_id, max_concurrency, last_seen_at_utc)
SELECT ns.id, 10 /* WorkerStatusCode.Active */, @p_deployment_version, @p_host, @p_engine_version, @p_dotnet_version, @p_process_id, @p_max_concurrency, {{now}}
  FROM {{schema}}.namespaces ns
 WHERE ns.name = @p_name;

INSERT INTO {{schema}}.events (
    event_code, namespace_id, actor_code, actor_key, job_id, execution_number,
    lineage_root_id, definition_id, worker_id, from_status_code, to_status_code,
    execution_status_code, duration_ms, reason_code, reason_message)
SELECT 120 /* JobEventCode.WorkerStarted */, w.namespace_id, 70 /* JobActorCode.Worker */, CAST(w.id AS TEXT),
       NULL, NULL, NULL, NULL, w.id, NULL, NULL, NULL, NULL, NULL, NULL
  FROM {{schema}}.workers w
 WHERE w.id = (SELECT max(id) FROM {{schema}}.workers);

SELECT w.namespace_id AS namespace_id, w.id AS worker_id
  FROM {{schema}}.workers w
 WHERE w.id = (SELECT max(id) FROM {{schema}}.workers);
