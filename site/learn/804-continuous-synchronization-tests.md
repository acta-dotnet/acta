# How to test a recurring synchronization job

The claims of the continuous-synchronization lab as tests: a schedule forced due and one firing driven at a time, one job identity across firings, the watermark kept on the job, a lost attempt reclaimed with the next execution continuing at the watermark, and a replayed, a failed, and a backlogged batch. Run it with `dotnet test`.

- Section: Test without sleeps or polling, step 4
- Program: `concepts/800-testing/804-continuous-synchronization-tests` in https://github.com/acta-dotnet/acta (one compiled project)
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
```

## 3. Lose an attempt, and let recovery reclaim it

The lease lapses and recovery reclaims the firing: the job is Ready again, the attempt charged.

The lines that matter:

```csharp
await _host.ExpireExecutionLeaseAsync(_slot, Ct);
Assert.Equal(1, (await _host.RunRecoveryOnceAsync(Namespace, Ct)).ReclaimedJobs);
Assert.Equal(JobStatusCode.Ready, (await JobAsync()).Status);
```

In `ContinuousSynchronizationTests.cs` (excerpt):

```csharp
var lost = _host.RunOnceAsync(_slot, Ct);
await _crash.Reached.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
Assert.Equal(40, await WatermarkAsync());
var firing = await FiringIdAsync();

await _host.ExpireExecutionLeaseAsync(_slot, Ct);
Assert.Equal(1, (await _host.RunRecoveryOnceAsync(Namespace, Ct)).ReclaimedJobs);
Assert.Equal(JobStatusCode.Ready, (await JobAsync()).Status);

// The dead attempt ends and its late outcome is fenced out. The next execution re-enters from the top, reads
// the watermark the lost one wrote, and applies the batch it owed: in the same drive, which re-claims the
// reclaimed row, or in a fresh one when that drive claimed nothing more.
_crash.Release.TrySetResult();
var outcome = await lost;
```

## 4. Run it

From a clone of the repository, with the .NET 10 SDK:

```bash
dotnet test concepts/800-testing/804-continuous-synchronization-tests
```

Runs as a test. This program is a test project: it passes or fails with the test suite.

## Next

- [How to enqueue from a process that runs no worker](/learn/701-enqueue-only-reference.md)

## More

- [The whole program](/learn/804-continuous-synchronization-tests.program.md): every file as compiled, 320 lines.
