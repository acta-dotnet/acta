-- Fabricated two-table baseline for the migration-chain tests (sqlite dialect). The version-0
-- stamp literal must equal SchemaMigrationRunner.RequiredBaselineStamp; the chain test asserts
-- that parity, so a `schema reset` stamp bump fails with an actionable message here.
CREATE TABLE IF NOT EXISTS {{schema}}.gadgets (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    name TEXT NOT NULL
) STRICT;

INSERT INTO {{schema}}.migrations (version, name, installed_schema)
VALUES (0, 'baseline-1.0.1', '{{schema}}')
ON CONFLICT (version) DO NOTHING;
INSERT INTO {{schema}}.migrations (version, name, installed_schema)
VALUES (1, 'init', '{{schema}}')
ON CONFLICT (version) DO NOTHING;
