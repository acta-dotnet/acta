
INSERT INTO {{schema}}.migrations (name)
VALUES ('M20261114235108_repeat_raise_after_finish')
ON CONFLICT (name) DO NOTHING;
