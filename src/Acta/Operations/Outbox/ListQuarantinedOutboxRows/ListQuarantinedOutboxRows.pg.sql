/* Keyset page of Quarantined rows ordered by job_ref - the one portable unique order across the three
   source encodings (a timestamp cursor would need per-provider instant rendering for no operator gain on
   a table that is near-empty by design). The operator surface orders a fetched page for display. */
SELECT
    job_ref, namespace_name AS job_namespace, job_name, idempotency_key, correlation_key, tenant_name,
    failure_count, failure_message AS last_error, created_at_utc
FROM {{table_ref}}
WHERE
    outbox_status_code = 90 /* OutboxStatusCode.Quarantined */
    AND (@p_after_job_ref IS NULL OR job_ref > @p_after_job_ref)
ORDER BY job_ref ASC
LIMIT @p_page_size;
