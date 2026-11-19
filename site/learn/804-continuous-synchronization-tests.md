# How to test a recurring synchronization job

Tests for the permanent synchronization job of [109-continuous-synchronization](/learn/109-continuous-synchronization.md): a schedule forced due and one firing driven at a time, and a lost attempt reclaimed with the next execution continuing at the watermark. Run it with `dotnet test learn/800-testing/804-continuous-synchronization-tests`.

- Section: Test without sleeps or polling, step 4
- Program: `learn/800-testing/804-continuous-synchronization-tests` in https://github.com/acta-dotnet/acta (one compiled project)
- Status: Runs as a test. This program is a test project: it passes or fails with the test suite.
- Rules: [/llms.txt](/llms.txt) is the contract every handler follows, starting with the handler running again from its first line; read it before you copy code from here.
- Release: the one /llms.txt names at its top.
- Page for people: /learn/804-continuous-synchronization-tests
- New to Acta: it is durable execution for .NET, a library with its state in the app's SQL database; the first run is in /llms.txt.

## 1. Make the schedule due and admit one firing

The test waits for no clock: it makes the schedule due, and one admission pass fires the permanent job.

The lines that matter:

```csharp
await _host.ForceScheduleDueAsync(Schedule, Ct);
Assert.Equal(1, (await _host.AdmitOnceAsync(Namespace, Ct)).Admitted);
```

In `ContinuousSynchronizationTests.cs` (excerpt):

```csharp
private async Task AdmitAsync()
{
    await _host.ForceScheduleDueAsync(Schedule, Ct);
    Assert.Equal(1, (await _host.AdmitOnceAsync(Namespace, Ct)).Admitted);
}
```

## 2. Drive the firing as one run

The lines that matter:

```csharp
await AdmitAsync();
return await _host.RunOnceAsync(_slot, Ct);
```

In `ContinuousSynchronizationTests.cs` (excerpt):

```csharp
private async Task<ActaRunStatus> FireAsync()
{
    await AdmitAsync();
    return await _host.RunOnceAsync(_slot, Ct);
}

private async Task<GetJobResponse> JobAsync() => (await _host.Jobs.GetAsync(_slot, Ct))!;

/// <summary>The watermark as the operator sees it, 0 before the first batch: the job's one variable.</summary>
private async Task<long> WatermarkAsync() => await _host.Jobs.GetVariableOrDefaultAsync<long>(_slot, "watermark", Ct);
```

## 3. Lose an attempt, and let recovery reclaim it

The lease lapses and recovery reclaims the firing: the job is Ready again, the attempt charged.

`ContinuousSynchronizationTests.cs` (excerpt):

```csharp
await _host.ExpireExecutionLeaseAsync(_slot, Ct);
Assert.Equal(1, (await _host.RunRecoveryOnceAsync(Namespace, Ct)).ReclaimedJobs);
Assert.Equal(JobStatusCode.Ready, (await JobAsync()).Status);
```

## 4. Run it

From a clone of the repository, with the .NET 10 SDK:

```bash
dotnet test learn/800-testing/804-continuous-synchronization-tests
```

Runs as a test. This program is a test project: it passes or fails with the test suite.

## Next

- [How to enqueue from a process that runs no worker](/learn/701-enqueue-only-reference.md)

## More

- [The whole program](/learn/804-continuous-synchronization-tests.program.md): every file as compiled, 316 lines.
