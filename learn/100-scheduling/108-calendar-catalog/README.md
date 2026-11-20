# How to drive a schedule from a published calendar

## Remember

- A job on a schedule runs across every deploy, for as long as the application lives. The name in `[Job("tick")]` and the namespace in `Run<CalendarCatalogJobs>("calendar-catalog")` are stored with every job. Treat both as permanent once jobs exist under them: a job stored under a name no handler carries stays Ready, and nothing runs it.
