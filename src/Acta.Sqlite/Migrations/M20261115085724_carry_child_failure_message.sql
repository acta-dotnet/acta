
INSERT INTO {{schema}}.migrations (name)
VALUES ('M20261115085724_carry_child_failure_message')
ON CONFLICT (name) DO NOTHING;
