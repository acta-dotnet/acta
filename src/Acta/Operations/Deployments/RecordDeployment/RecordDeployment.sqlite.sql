INSERT INTO {{schema}}.deployments (namespace_id, deployment_version)
SELECT @p_namespace_id, @p_deployment_version
WHERE NOT EXISTS (
    SELECT 1 FROM {{schema}}.deployments
    WHERE namespace_id = @p_namespace_id AND deployment_version = @p_deployment_version
);

-- An unchanged namespace writes nothing, so a repeated deploy leaves its version and modified_at_utc.
UPDATE {{schema}}.namespaces SET
    deployment_version = @p_deployment_version,
    owner_team = @p_owner_team,
    description = @p_description,
    modified_at_utc = {{now}},
    version = version + 1
WHERE
    namespace_id = @p_namespace_id
    AND (
        deployment_version IS NOT @p_deployment_version
        OR owner_team IS NOT @p_owner_team
        OR description IS NOT @p_description
    );

-- The evidence of the deploy: one event per run, also for a run that changed nothing.
INSERT INTO {{schema}}.events (
    event_code,
    created_at_utc,
    namespace_id,
    entity_code,
    entity_id,
    event_message,
    details_format_id,
    details)
VALUES (
    23 /* EventCode.NamespaceDeployed */,
    {{now}},
    @p_namespace_id,
    80 /* EntityCode.Namespace */,
    @p_namespace_id,
    @p_event_message,
    @p_details_format_id,
    @p_details);
