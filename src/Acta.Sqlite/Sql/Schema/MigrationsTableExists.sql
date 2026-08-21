-- Read-only probe for the bootstrap migration-history preflight: is the history ledger there at all?
-- Answering with a count rather than letting the SELECT fail keeps "never provisioned" separable from
-- "provisioned but unreadable", which are different operator actions.
SELECT COUNT(*)
FROM {{schema}}.sqlite_master
WHERE type = 'table'
  AND name = 'migrations';
