DELETE FROM {{schema}}.leases
 WHERE lease_key = @p_lease_key AND version = @p_version
RETURNING version;
