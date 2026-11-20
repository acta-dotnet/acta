# How to chain jobs into a pipeline

## Remember

- A parent and its children are each stored under their own job name. The names in `[Job("ingest-document")]`, `[Job("render-pdf")]` and `[Job("archive-file")]`, and the namespace in `Run<ChainedJobsManifest>("chained-jobs")`, are stored with every job. Treat them as permanent once jobs exist under them: a job stored under a name no handler carries stays Ready, and nothing runs it.
- `IngestDocument` is stored with each job as its input. Add a field as optional or with a default. A renamed, removed or retyped field needs a new job name such as `ingest-document-v2`, and the old handler stays registered until its jobs have finished: [Change a job's input without breaking the jobs already stored](../../500-payloads/502-job-versions/).
