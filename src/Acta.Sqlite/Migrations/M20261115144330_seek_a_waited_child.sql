
INSERT INTO {{schema}}.migrations (name)
VALUES ('M20261115144330_seek_a_waited_child')
ON CONFLICT (name) DO NOTHING;
