CREATE OR REPLACE FUNCTION {{schema}}.release_lock(
    p_lease_key VARCHAR,
    p_version   INT
)
RETURNS TABLE(version INT)
LANGUAGE sql
AS $$
    DELETE FROM {{schema}}.leases
     WHERE lease_key = p_lease_key AND version = p_version
    RETURNING version;
$$;
