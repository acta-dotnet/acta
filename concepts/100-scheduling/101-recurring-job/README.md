# How to run a job on a repeating schedule

## Remember

- A schedule is written by a deploy: `jobs migrate` or `jobs deploy` in production, the development start on your own machine.
- A permanent job keeps its step results from one firing to the next. Put the firing in a step's name, `$"send.f{ctx.FiringId:N}"`, or the step that ran on the last firing answers this one from its record.
- The handler does its bounded work and returns. Never loop inside it: the schedule brings the next firing.
