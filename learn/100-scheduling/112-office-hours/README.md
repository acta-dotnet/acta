# How to keep a schedule inside office hours

## Remember

- A job on a schedule runs across every deploy, for as long as the application lives. The name in `[Job("sync-crm")]` and the namespace in `Run<OfficeHoursJobs>("crm")` are stored with every job. Treat both as permanent once jobs exist under them: a job stored under a name no handler carries stays Ready, and nothing runs it.
