-- Fabricated two-table baseline for the migration-chain tests (sqlite dialect).
CREATE TABLE IF NOT EXISTS {{schema}}.gadgets (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    name TEXT NOT NULL
) STRICT;

INSERT INTO {{schema}}.migrations (version, name, installed_schema)
VALUES (1, 'init', '{{schema}}')
ON CONFLICT (version) DO NOTHING;
