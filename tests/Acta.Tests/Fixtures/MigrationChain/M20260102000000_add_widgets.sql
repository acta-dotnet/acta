-- Fabricated delta for the migration-chain tests: independent of the baseline's tables so the resume
-- scenario can apply it alone, guarded per statement like a real migration, self-stamping last.
CREATE TABLE IF NOT EXISTS {{schema}}.widgets (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    gadget_name TEXT NOT NULL
) STRICT;

INSERT INTO {{schema}}.migrations (name)
VALUES ('M20260102000000_add_widgets')
ON CONFLICT (name) DO NOTHING;
