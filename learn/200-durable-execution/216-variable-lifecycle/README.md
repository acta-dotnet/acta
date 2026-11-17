# How to check, delete, and default a job's variables

## Remember

- A variable write is durable by itself and needs no step around it. Read it from outside the job with `jobs.GetVariableOrDefaultAsync<T>(jobRef, name, ct)`.
