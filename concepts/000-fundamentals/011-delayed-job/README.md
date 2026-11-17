# How to run a job once, later

## Remember

- `ScheduleAsync(input, DateTimeOffset)` runs the job at an instant instead of after a delay. Work that recurs is a `[Schedule]`, not a job that schedules itself.
