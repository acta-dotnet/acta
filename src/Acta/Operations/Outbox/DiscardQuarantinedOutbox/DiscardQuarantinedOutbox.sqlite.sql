/* Operator discard: the Quarantined row is deleted outright; the returned ids become ledger event
   evidence via the applying tick, so proof outlives the row. Only status 90 qualifies (an in-flight
   claimed row can never be deleted); a NULL id set discards every quarantined row. */
DELETE FROM {{table_ref}}
WHERE
    outbox_status_code = 90 /* OutboxStatusCode.Quarantined */
    AND (
        @p_job_refs IS NULL
        OR lower(replace(job_ref, '-', '')) IN (SELECT lower(replace(value, '-', '')) FROM json_each(@p_job_refs))
    )
RETURNING job_ref;
