
INSERT INTO {{schema}}.migrations (name)
VALUES ('M20261115090220_answer_the_checked_dedup_job')
ON CONFLICT (name) DO NOTHING;
