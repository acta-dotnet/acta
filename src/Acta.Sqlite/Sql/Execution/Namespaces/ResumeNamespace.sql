DROP TABLE IF EXISTS temp._resume_namespace;

CREATE TEMP TABLE _resume_namespace AS
SELECT n.id, n.status_code AS from_status, n.version AS from_version
FROM {{schema}}.namespaces n
WHERE n.name = @p_namespace_name;

INSERT INTO {{schema}}.events (
    event_code,
    created_at_utc,
    namespace_id,
    actor_code,
    actor_key,
    job_id,
    job_ref,
    execution_number,
    lineage_root_id,
    definition_id,
    tenant_id,
    worker_id,
    from_status_code,
    to_status_code,
    execution_status_code,
    duration_ms,
    reason_code,
    reason_message)
SELECT
    21 /* JobEventCode.NamespaceResumed */,
    {{now}},
    s.id,
    @p_actor_code,
    @p_actor_key,
    NULL,
    NULL,
    NULL,
    NULL,
    NULL,
    NULL,
    NULL,
    NULL,
    NULL,
    NULL,
    NULL,
    NULL,
    @p_reason_message
FROM temp._resume_namespace s
WHERE s.from_status <> 10 /* JobNamespaceStatusCode.Active */;

UPDATE {{schema}}.namespaces
SET status_code = 10 /* JobNamespaceStatusCode.Active */, modified_at_utc = {{now}}, version = version + 1
WHERE
    name = @p_namespace_name
    AND status_code <> 10 /* JobNamespaceStatusCode.Active */;

SELECT
    CASE
        WHEN s.id IS NULL THEN 2 /* AdminControlAction.NotFound */
        WHEN s.from_status = 10 /* JobNamespaceStatusCode.Active */ THEN 3 /* AdminControlAction.AlreadyInState */
        ELSE 1 /* AdminControlAction.Applied */
    END AS action,
    CASE
        WHEN s.id IS NULL THEN NULL
        WHEN s.from_status = 10 /* JobNamespaceStatusCode.Active */ THEN s.from_version
        ELSE s.from_version + 1
    END AS version
FROM (SELECT 1) one
LEFT JOIN temp._resume_namespace s ON 1 = 1;
