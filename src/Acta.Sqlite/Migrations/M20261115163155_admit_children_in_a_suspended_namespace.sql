
INSERT INTO {{schema}}.migrations (name)
VALUES ('M20261115163155_admit_children_in_a_suspended_namespace')
ON CONFLICT (name) DO NOTHING;
