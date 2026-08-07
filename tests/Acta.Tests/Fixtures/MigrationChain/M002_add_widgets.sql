-- Fabricated delta for the migration-chain tests: independent of M001's tables so the resume
-- scenario can apply it alone, guarded per statement like a real migration, self-stamping last.
CREATE TABLE IF NOT EXISTS {{schema}}.widgets (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    gadget_name TEXT NOT NULL
) STRICT;

INSERT INTO {{schema}}.migrations (version, name, installed_schema)
VALUES (2, 'add_widgets', '{{schema}}')
ON CONFLICT (version) DO NOTHING;
