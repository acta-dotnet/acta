
INSERT INTO {{schema}}.migrations (name)
VALUES ('M20261115144345_seek_a_held_job')
ON CONFLICT (name) DO NOTHING;
