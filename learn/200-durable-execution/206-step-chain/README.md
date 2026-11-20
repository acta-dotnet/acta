# How to feed one step's result into the next

## Remember

- `"fetch"`, `"extract-text"` and `"summarize"` are the keys to each step's stored result. Rename one and it runs again for every document that already ran it.
- A step's stored result must stay readable by the new code: `FetchedDocument` is what `"fetch"` wrote for the jobs already stored.
- The name in `[Job("index-document")]` and the namespace in `Run<StepChainJobs>("step-chain")` are stored with every job. Treat both as permanent once jobs exist under them: a job stored under a name no handler carries stays Ready, and nothing runs it.
- `IndexDocument` is stored with each job as its input. Add a field as optional or with a default. A renamed, removed or retyped field needs a new job name such as `index-document-v2`, and the old handler stays registered until its jobs have finished: [Change a job's input without breaking the jobs already stored](../../500-payloads/502-job-versions/).
