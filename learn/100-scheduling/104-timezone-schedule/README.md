# How to run a job every day at a set hour in your time zone

## Remember

- A schedule is written by a deploy: `jobs migrate` or `jobs deploy` in production, the development start on your own machine. Once a namespace has been deployed that way, a new or changed schedule takes `jobs deploy`.
- A permanent job keeps its step results from one firing to the next. Put the firing in a step's name, `$"send.f{ctx.FiringId:N}"`, or the step that ran on Monday answers from its record on Tuesday.
- A run that comes due while no worker is up is skipped by default. [Decide what happens to runs a schedule missed](../106-schedule-misfire/) shows the other choices.
- On the night the clocks change, a cron that names an hour they skip fires at the moment they jump, and one that names an hour they repeat fires once, in its first pass. An hour outside 01:00 to 03:00 local time meets neither in most zones.
- A time zone id is an IANA or a Windows id. One that cannot be resolved fails the deploy, so a typo never ships a schedule that fires at the wrong hour.
- An interval such as `"PT15S"` or `"10s"` is a fixed gap between firings and takes no time zone.
- The handler does its bounded work and returns. Never loop or sleep inside it to wait for the next day: the schedule brings the next firing.
- When each night is its own piece of business, with its own retries and result, [make every occurrence its own job](../110-per-occurrence-delivery/).
- `Cron.Hourly`, `Cron.DailyAt9`, `Cron.EveryMonday`, and `Cron.Every15Minutes` name common crons, so the usual cases need no cron string.
- The line the release check reads comes from `demo-tick`, the interval schedule. The 08:00 schedule would take a day to show.
- A job on a schedule runs across every deploy, for as long as the application lives. The name in `[Job("daily-digest")]` and the namespace in `Run<TimezoneScheduleJobs>("timezone-schedule")` are stored with every job. Treat both as permanent once jobs exist under them: a job stored under a name no handler carries stays Ready, and nothing runs it.

## Check yourself

**The schedule is `"0 8 * * *"` with `TimeZoneId = "Europe/Berlin"`. In July Berlin is two hours ahead of UTC. When does the job fire, in UTC?**

- 06:00
- 07:00
- 08:00

<details><summary>Answer</summary>

06:00. The cron is read on the Berlin wall clock, so 08:00 there is 06:00 UTC in summer and 07:00 UTC in winter. The local hour stays the same all year.

</details>
