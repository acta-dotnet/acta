# How to make every occurrence its own job

## Remember

- A job on a schedule runs across every deploy, for as long as the application lives. The name in `[Job("import-files")]` is stored with every job. Treat it as permanent once jobs exist under it: a job stored under a name no handler carries stays Ready, and nothing runs it.
