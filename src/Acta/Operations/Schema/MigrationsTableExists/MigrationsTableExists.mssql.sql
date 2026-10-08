-- Read-only probe for the bootstrap migration-history preflight: is the history ledger there at all?
-- Answering with a count rather than letting the SELECT fail keeps "never provisioned" separable from
-- "provisioned but unreadable", which are different operator actions.
SELECT COUNT(*)
FROM sys.tables
WHERE SCHEMA_NAME(schema_id) = N'{{schema}}'
  AND name = 'migrations';
