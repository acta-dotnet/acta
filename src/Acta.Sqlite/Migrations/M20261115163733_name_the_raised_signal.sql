
INSERT INTO {{schema}}.migrations (name)
VALUES ('M20261115163733_name_the_raised_signal')
ON CONFLICT (name) DO NOTHING;
