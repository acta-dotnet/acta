using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace Acta.Bench;

public sealed record BenchWork(
    string Key,
    int Steps = 0,
    int Children = 0,
    int Depth = 0,
    int WorkMs = 0,
    int Failures = 0,
    bool Poison = false,
    string? Signal = null,
    int SleepSeconds = 0,
    string? Payload = null,
    int CpuRounds = 0,
    string ChildNamespace = "bench",
    int ChildWindow = 100,
    bool PartialFailure = false,
    string? Boundary = null,
    string? EffectMode = null,
    bool AtMostOnce = false,
    bool Witness = false,
    int SignalTimeoutSeconds = 0,
    string? AfterChildrenSignal = null
);

public sealed class BenchWorkHandler(OracleClient oracle)
{
    [Job("resilient-work", MaxAttempts = 100, Backoff = "1s..1s")]
    public Task<string> RunResilient(BenchWork input, JobContext ctx, CancellationToken ct) => Run(input, ctx, ct);

    [Job("retained-work", MaxAttempts = 4, Backoff = "1s..1s")]
    public Task<string> RunRetained(BenchWork input, JobContext ctx, CancellationToken ct) => Run(input, ctx, ct);

    [Job("work", MaxAttempts = 4, Backoff = "1s..1s", EventRecording = EventRecordingCode.Audit)]
    public async Task<string> Run(BenchWork input, JobContext ctx, CancellationToken ct)
    {
        if (input.Witness)
        {
            await ctx.NoteAsync("begin " + input.Key, ct);
        }
        if (input.Boundary == "claim" && ctx.ExecutionNumber == 1)
        {
            // The held body ignores its attempt token, so an attempt whose lease lapsed at the barrier still returns a
            // result for the engine to complete, and the receipt records that it got that far.
            await oracle.CallAsync("barrier", input.Key, "claim", false, 0, CancellationToken.None);
            await oracle.CallAsync("receipt", input.Key, "claim-returned", false, 0, CancellationToken.None);
        }
        if (input.Poison || ctx.ExecutionNumber <= input.Failures)
        {
            throw new InvalidOperationException("Declared deterministic workload failure.");
        }
        if (input.SleepSeconds > 0)
        {
            await ctx.SleepAsync("sleep", TimeSpan.FromSeconds(input.SleepSeconds), ct: ct);
        }
        if (input.Signal is not null)
        {
            if (input.SignalTimeoutSeconds > 0)
            {
                var signalled = await ctx.TryWaitSignalAsync(input.Signal, TimeSpan.FromSeconds(input.SignalTimeoutSeconds), ct);
                return signalled.TimedOut ? "expired" : "signalled";
            }
            await ctx.WaitSignalAsync(input.Signal, ct);
        }
        if (input.WorkMs > 0 && input.EffectMode is null)
        {
            await Task.Delay(input.WorkMs, ct);
        }
        long sum = 0;
        if (input.EffectMode is not null)
        {
            await ctx.RunStepAsync(
                "effect",
                async token =>
                {
                    await oracle.CallAsync("effect", input.Key, null, input.EffectMode == "idempotent", input.WorkMs, token);
                    if (input.Boundary == "effect" && ctx.ExecutionNumber == 1)
                    {
                        await oracle.CallAsync("barrier", input.Key, "effect", false, 0, token);
                    }
                },
                configure: input.AtMostOnce ? o => o.AtMostOnce() : null,
                ct: ct
            );
            if (input.Boundary == "completion" && ctx.ExecutionNumber == 1)
            {
                await oracle.CallAsync("barrier", input.Key, "completion", false, 0, ct);
            }
        }
        for (var i = 1; i <= input.Steps; i++)
        {
            var step = i;
            sum += await ctx.RunStepAsync(
                $"step-{i}",
                async token =>
                {
                    if (input.Boundary == "checkpoint")
                    {
                        await ctx.NoteAsync($"checkpoint-body {step}", token);
                    }
                    return step * 7L;
                },
                ct: ct
            );
            if (i == 2 && input.Boundary == "checkpoint" && ctx.ExecutionNumber == 1)
            {
                await oracle.CallAsync("barrier", input.Key, "checkpoint", false, 0, ct);
            }
        }
        if (input.Children > 0)
        {
            for (var first = 0; first < input.Children; first += input.ChildWindow)
            {
                List<long> children = [];
                for (var i = first; i < Math.Min(first + input.ChildWindow, input.Children); i++)
                {
                    var child = new BenchWork(
                        $"{input.Key}/child-{i}",
                        Children: input.Depth > 1 ? 3 : 0,
                        Depth: Math.Max(0, input.Depth - 1),
                        Poison: input.PartialFailure && i == 0,
                        ChildNamespace: input.ChildNamespace,
                        Boundary: input.Boundary == "join-after" && i == input.Children - 1 ? "claim" : null
                    );
                    var handle = await ctx.StartChildAsync(
                        $"child-{i}",
                        input.ChildNamespace,
                        "work",
                        JobPayload.Json(child, WorkJson.Default.BenchWork),
                        ct: ct
                    );
                    children.Add(handle.JobId);
                    if (i == 3 && input.Boundary == "children" && ctx.ExecutionNumber == 1)
                    {
                        await oracle.CallAsync("barrier", input.Key, "children", false, 0, ct);
                    }
                }
                if (input.Boundary == "join-before" && ctx.ExecutionNumber == 1)
                {
                    await oracle.CallAsync("barrier", input.Key, "join-before", false, 0, ct);
                }
                var outcomes = await ctx.WaitChildrenAsync(children, ct);
                foreach (var outcome in outcomes)
                {
                    if (outcome.Succeeded)
                    {
                        sum++;
                    }
                }
            }
        }
        var bytes = Encoding.UTF8.GetBytes(input.Payload ?? input.Key);
        if (input.AfterChildrenSignal is not null)
        {
            await ctx.WaitSignalAsync(input.AfterChildrenSignal, ct);
        }
        for (var i = 0; i < input.CpuRounds; i++)
        {
            bytes = SHA256.HashData(bytes);
        }
        if (input.Witness)
        {
            await ctx.NoteAsync("end " + input.Key, ct);
        }
        return input.Steps > 0 || input.Children > 0
            ? sum.ToString(CultureInfo.InvariantCulture)
            : Convert.ToHexStringLower(SHA256.HashData(bytes));
    }
}

public sealed class LateWorkHandler(OracleClient oracle)
{
    [Job("late-work", MaxAttempts = 1, ExecutionTimeout = "1s")]
    public async Task<string> Run(BenchWork input, JobContext ctx, CancellationToken ct)
    {
        await oracle.CallAsync("effect", input.Key, null, false, 2500, CancellationToken.None);
        await ctx.SetVariableAsync("late-write", true, CancellationToken.None);
        return "late";
    }
}

[JsonSourceGenerationOptions]
[JsonSerializable(typeof(BenchWork))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(long))]
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(FixtureValueV1))]
[JsonSerializable(typeof(FixtureValueV2))]
[JsonSerializable(typeof(ListEventsItem[]))]
public partial class WorkJson : JsonSerializerContext;
