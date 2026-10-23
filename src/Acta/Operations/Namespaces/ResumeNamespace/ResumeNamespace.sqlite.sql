DROP TABLE IF EXISTS temp._resume_namespace;

CREATE TEMP TABLE _resume_namespace AS
SELECT n.namespace_id, n.namespace_status_code AS from_status, n.version AS from_version
FROM {{schema}}.namespaces n
WHERE n.namespace_name = @p_namespace_name;

INSERT INTO {{schema}}.events (
    event_code,
    created_at_utc,
    namespace_id,
    entity_code,
    entity_id,
    event_message,
    details_format_id,
    details)
SELECT
    21 /* EventCode.NamespaceResumed */,
    {{now}},
    s.namespace_id,
    80 /* EntityCode.Namespace */,
    s.namespace_id,
    @p_event_message,
    @p_details_format_id,
    @p_details
FROM temp._resume_namespace s
WHERE s.from_status <> 10 /* NamespaceStatusCode.Active */;

UPDATE {{schema}}.namespaces
SET namespace_status_code = 10 /* NamespaceStatusCode.Active */, modified_at_utc = {{now}}, version = version + 1
WHERE
    namespace_name = @p_namespace_name
    AND namespace_status_code <> 10 /* NamespaceStatusCode.Active */;

SELECT
    CASE
        WHEN s.namespace_id IS NULL THEN 2 /* ControlAction.NotFound */
        WHEN s.from_status = 10 /* NamespaceStatusCode.Active */ THEN 6 /* ControlAction.AlreadyInState */
        ELSE 1 /* ControlAction.Applied */
    END AS action,
    CASE
        WHEN s.namespace_id IS NULL THEN NULL
        WHEN s.from_status = 10 /* NamespaceStatusCode.Active */ THEN s.from_version
        ELSE s.from_version + 1
    END AS version
FROM (SELECT 1) one
LEFT JOIN temp._resume_namespace s ON 1 = 1;
