
INSERT INTO {{schema}}.migrations (name)
VALUES ('M20261115114815_keep_a_child_landing_whole')
ON CONFLICT (name) DO NOTHING;
