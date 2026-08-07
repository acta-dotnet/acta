INSERT INTO {{schema}}.leases (lease_key, kind_code, job_id, expires_at_utc, version)
VALUES (
    @p_lease_key,
    10 /* LeaseKindCode.Lock */,
    @p_job_id,
    {{now}} + (@p_lease_ttl_seconds) * 1000,
    1
)
ON CONFLICT (lease_key) DO UPDATE
SET
    job_id = excluded.job_id,
    expires_at_utc = excluded.expires_at_utc,
    version = leases.version + 1
WHERE leases.expires_at_utc <= {{now}}
RETURNING version;
