/* Compare-and-swap on the version the delivery read handed out: an attempt whose row moved while the
   send was in flight - resolved by an operator, or settled by a competing worker - writes nothing and
   returns no row, and the caller treats the empty result as "the newer state stands". */
UPDATE {{schema}}.alerts
SET
    delivery_status_code = @p_delivery_status_code,
    retry_count = @p_retry_count,
    retry_after_utc = @p_retry_after_utc,
    modified_at_utc = now(),
    version = version + 1
WHERE
    id = @p_id
    AND version = @p_version
RETURNING id;
