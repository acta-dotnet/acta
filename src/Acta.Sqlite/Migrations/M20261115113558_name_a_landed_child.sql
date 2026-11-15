
INSERT INTO {{schema}}.migrations (name)
VALUES ('M20261115113558_name_a_landed_child')
ON CONFLICT (name) DO NOTHING;
