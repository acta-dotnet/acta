# How to run a job on the last business day of each month

## Remember

- A job on a schedule runs across every deploy, for as long as the application lives. The name in `[Job("settle")]` and the namespace in `Run<BusinessDayScheduleJobs>("settlement")` are stored with every job. Treat both as permanent once jobs exist under them: a job stored under a name no handler carries stays Ready, and nothing runs it.
