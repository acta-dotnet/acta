-- Fabricated two-table baseline for the migration-chain tests (sqlite dialect). This provider is not
-- emitted, so its version-0 stamp is a fixed literal rather than a content hash; the hooks in
-- SchemaMigrationChainTests declare the same value, and a test asserts the two agree.
CREATE TABLE IF NOT EXISTS {{schema}}.gadgets (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    name TEXT NOT NULL
) STRICT;

INSERT INTO {{schema}}.migrations (version, name, installed_schema)
VALUES (0, 'baseline-fixture', '{{schema}}')
ON CONFLICT (version) DO NOTHING;
INSERT INTO {{schema}}.migrations (version, name, installed_schema)
VALUES (1, 'init', '{{schema}}')
ON CONFLICT (version) DO NOTHING;
