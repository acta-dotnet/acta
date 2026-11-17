# How to compute a value once and reuse it

## Remember

- A variable write is durable by itself and needs no step around it. Read it from outside the job with `jobs.GetVariableOrDefaultAsync<T>(jobRef, name, ct)`.
