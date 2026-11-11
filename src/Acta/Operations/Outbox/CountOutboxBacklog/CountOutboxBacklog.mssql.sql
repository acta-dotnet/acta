-- A row a producer has not committed is not counted yet, as under read-committed snapshot.
SELECT COUNT(*)
FROM {{table_ref}} WITH (READCOMMITTEDLOCK, READPAST, FORCESEEK)
WHERE outbox_status_code = 10 /* OutboxStatusCode.Pending */;
