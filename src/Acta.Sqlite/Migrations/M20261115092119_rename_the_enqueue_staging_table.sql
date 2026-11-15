
INSERT INTO {{schema}}.migrations (name)
VALUES ('M20261115092119_rename_the_enqueue_staging_table')
ON CONFLICT (name) DO NOTHING;
