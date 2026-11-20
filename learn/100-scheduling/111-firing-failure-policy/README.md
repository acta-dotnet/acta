# How to decide what a failed firing does to its schedule

## Remember

- A job on a schedule runs across every deploy, for as long as the application lives. The names in `[Job("sync-prices-pausing")]` and `[Job("sync-prices-continuing")]` are stored with every job. Treat them as permanent once jobs exist under them: a job stored under a name no handler carries stays Ready, and nothing runs it.
