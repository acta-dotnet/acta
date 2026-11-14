
INSERT INTO {{schema}}.migrations (name)
VALUES ('M20261114214847_keep_first_signal_value')
ON CONFLICT (name) DO NOTHING;
