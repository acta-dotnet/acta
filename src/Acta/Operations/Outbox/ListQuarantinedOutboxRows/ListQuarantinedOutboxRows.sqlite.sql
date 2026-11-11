/* Keyset page of Quarantined rows ordered by outbox_id - the one portable unique order across the three
   source encodings (a timestamp cursor would need per-provider instant rendering for no operator gain on
   a table that is near-empty by design). The operator surface orders a fetched page for display. */
SELECT
    outbox_id, namespace_name AS job_namespace, job_name, deduplication_key, correlation_key, tenant_name,
    failure_count, failure_message AS last_error, created_at_utc
FROM {{table_ref}}
WHERE
    outbox_status_code = 90 /* OutboxStatusCode.Quarantined */
    AND (@p_after_outbox_id IS NULL OR lower(replace(outbox_id, '-', '')) > lower(replace(@p_after_outbox_id, '-', '')))
/* The dashless lower-case form on both order and cursor: a producer may stage an id in either case, with or
   without its dashes, and the raw text would interleave those spellings. */
ORDER BY lower(replace(outbox_id, '-', '')) ASC
LIMIT @p_page_size;
