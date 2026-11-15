-- Each name seeks the parent's key in its filtered index, the filter restated and both seek columns named: with the
-- index alone the optimizer may seek the parent and test the key as a residual, reading every child once per name.
SELECT j.job_id, j.job_ref, j.deduplication_key
FROM OPENJSON (@p_child_names) AS n
INNER JOIN {{schema}}.jobs AS j WITH (FORCESEEK (ux_jobs_deduplication_key_child (parent_job_id, deduplication_key)))
    ON
        j.parent_job_id = @p_parent_job_id
        AND j.deduplication_key = CAST(n.[value] COLLATE Latin1_General_100_BIN2_UTF8 AS VARCHAR(128))
        AND j.parent_job_id IS NOT NULL
        AND j.deduplication_key IS NOT NULL;
