/* Keyset page of Quarantined rows ordered by outbox_id - the one portable unique order across the three
   source encodings (a timestamp cursor would need per-provider instant rendering for no operator gain on
   a table that is near-empty by design). The operator surface orders a fetched page for display. */
SELECT
    outbox_id, job_namespace, job_name, deduplication_key, correlation_key, tenant_key,
    failure_count, last_error, created_at_utc
FROM {{table_ref}}
WHERE
    status_code = 90 /* OutboxStatusCode.Quarantined */
    AND (@p_after_outbox_id IS NULL OR outbox_id > @p_after_outbox_id COLLATE NOCASE)
/* NOCASE on both order and cursor: producers bind raw Guids (upper-case TEXT) while Acta staging writes
   lower-case, and a case-sensitive keyset would interleave the two encodings incorrectly. */
ORDER BY outbox_id COLLATE NOCASE ASC
LIMIT @p_page_size;
