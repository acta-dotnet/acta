-- clock_timestamp(), because now() is the transaction's start: a read inside a transaction that waited on a lock
-- would answer the instant before the wait.
SELECT (clock_timestamp() AT TIME ZONE 'UTC');
