-- Fabricated two-table baseline for the migration-chain tests (sqlite dialect).
CREATE TABLE IF NOT EXISTS {{schema}}.gadgets (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    name TEXT NOT NULL
) STRICT;

INSERT INTO {{schema}}.migrations (name)
VALUES ('M20260101000000_init')
ON CONFLICT (name) DO NOTHING;
