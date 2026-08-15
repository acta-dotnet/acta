CREATE OR REPLACE FUNCTION {{schema}}.start_worker(
    p_name VARCHAR,
    p_owner_team VARCHAR,
    p_description VARCHAR,
    p_catalog_hash VARCHAR,
    p_status_code SMALLINT,
    p_deployment_version VARCHAR,
    p_host VARCHAR,
    p_engine_version VARCHAR,
    p_dotnet_version VARCHAR,
    p_process_id INT,
    p_max_concurrency INT,
    p_worker_ref UUID
)
RETURNS TABLE (namespace_id SMALLINT, worker_id INT)
LANGUAGE sql
AS $$
    WITH ns_upsert AS (
        INSERT INTO {{schema}}.namespaces (
            name,
            owner_team,
            description,
            catalog_hash,
            status_code,
            created_at_utc,
            modified_at_utc,
            version)
        VALUES (
            p_name,
            p_owner_team,
            p_description,
            p_catalog_hash,
            p_status_code,
            now(),
            now(),
            0)
        ON CONFLICT (name) DO UPDATE SET
            owner_team = EXCLUDED.owner_team,
            description = EXCLUDED.description,
            catalog_hash = EXCLUDED.catalog_hash,
            modified_at_utc = now(),
            version = {{schema}}.namespaces.version + 1
        WHERE {{schema}}.namespaces.catalog_hash IS DISTINCT FROM EXCLUDED.catalog_hash
        RETURNING id
    ),
    ns AS (
        SELECT id FROM ns_upsert
        UNION ALL
        SELECT id FROM {{schema}}.namespaces
        WHERE
            name = p_name
            AND NOT EXISTS (SELECT 1 FROM ns_upsert)
    ),
    w AS (
        INSERT INTO {{schema}}.workers (
            namespace_id,
            worker_ref,
            status_code,
            deployment_version,
            host,
            engine_version,
            dotnet_version,
            process_id,
            max_concurrency,
            last_seen_at_utc,
            created_at_utc,
            modified_at_utc,
            version)
        SELECT
            ns.id,
            p_worker_ref,
            10 /* WorkerStatusCode.Active */,
            p_deployment_version,
            p_host,
            p_engine_version,
            p_dotnet_version,
            p_process_id,
            p_max_concurrency,
            now(),
            now(),
            now(),
            0
        FROM ns
        RETURNING id, namespace_id, worker_ref
    ),
    evt AS (
        INSERT INTO {{schema}}.events (
            event_code,
            created_at_utc,
            namespace_id,
            actor_code,
            actor_key,
            job_id,
            execution_number,
            lineage_root_id,
            definition_id,
            worker_id,
            from_status_code,
            to_status_code,
            execution_status_code,
            duration_ms,
            reason_code,
            reason_message)
        SELECT
            120 /* EventCode.WorkerStarted */,
            now(),
            w.namespace_id,
            70 /* ActorCode.Worker */,
            w.worker_ref::TEXT,
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
        FROM w
        RETURNING 1
    )
    SELECT w.namespace_id AS namespace_id, w.id AS worker_id FROM w;
$$;
