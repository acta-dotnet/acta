using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Acta;

namespace TestJobs;

public sealed record NullResultInput(int Ignored);

public sealed record NullResultOutput(int Value);

/// <summary>Input of the maybe-double probe.</summary>
public sealed record MaybeDoubleInput(int Value);

/// <summary>Input of the null-then-value probe: the value its later executions return.</summary>
public sealed record NullThenValueInput(int Value);

/// <summary>What a parent read from a child's typed outcome, flattened for the result store.</summary>
public sealed record ChildValueSeen(bool Succeeded, bool HasValue, int? Value, string? Refusal)
{
    public static ChildValueSeen Of(JobOutcome<NullResultOutput> outcome)
    {
        string? refusal = null;
        try
        {
            outcome.ValueOrThrow();
        }
        catch (JobResultMissingException missing)
        {
            refusal = missing.Message;
        }
        return new ChildValueSeen(outcome.Succeeded, outcome.HasValue, outcome.Value?.Value, refusal);
    }
}

/// <summary>A non-null value whose JSON is null, as a custom converter can write one.</summary>
[JsonConverter(typeof(Converter))]
public sealed record JsonNullValue(int Ignored)
{
    public sealed class Converter : JsonConverter<JsonNullValue>
    {
        public override JsonNullValue? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            throw new JsonException("a JsonNullValue is never read back");

        public override void Write(Utf8JsonWriter writer, JsonNullValue value, JsonSerializerOptions options) => writer.WriteNullValue();
    }
}

public sealed class UnserializableOutput(long jobId)
{
    public long Value => jobId < 0 ? jobId : throw new InvalidOperationException("this value cannot be read");
}

/// <summary>
/// Probes for "null stores nothing": handlers declared with an optional result that return null, which succeed with no
/// result, and parents that read such a child's outcome.
/// </summary>
public static class NullResultHandler
{
    [Job("null-result")]
    public static NullResultOutput? Run(NullResultInput input) => null;

    /// <summary>Returns its value doubled, and null, which stores no result, for zero.</summary>
    [Job("maybe-double")]
    public static Task<int?> DoubleOrNull(MaybeDoubleInput input, CancellationToken ct) =>
        Task.FromResult<int?>(input.Value == 0 ? null : input.Value * 2);

    /// <summary>Returns null from its first execution and its input's value from any later one.</summary>
    [Job("null-then-value")]
    public static Task<NullResultOutput?> NullThenValue(NullThenValueInput input, JobContext ctx, CancellationToken ct) =>
        Task.FromResult(ctx.ExecutionNumber == 1 ? null : new NullResultOutput(input.Value));

    /// <summary>Returns a non-null value whose JSON is null, which stores nothing.</summary>
    [Job("json-null-result")]
    public static Task<JsonNullValue> JsonNull(JobContext ctx, CancellationToken ct) => Task.FromResult(new JsonNullValue(1));

    /// <summary>Declares no result.</summary>
    [Job("void-child")]
    public static Task VoidChild(JobContext ctx, CancellationToken ct) => Task.CompletedTask;

    /// <summary>Returns a value whose serialization throws.</summary>
    [Job("unserializable-result")]
    public static Task<UnserializableOutput> RunUnserializable(JobContext ctx, CancellationToken ct) =>
        Task.FromResult(new UnserializableOutput(ctx.JobId));

    /// <summary>Calls a child that returns null and reports what its outcome answers; a retry runs at once.</summary>
    [Job("job-parent-of-null-child", Backoff = "0s")]
    public static async Task<ChildValueSeen> ParentOfNullChild(JobContext ctx, CancellationToken ct) =>
        ChildValueSeen.Of(await ctx.CallChildAsync("lookup", TestJobsManifest.NullResult(new NullResultInput(1)), ct));

    /// <summary>Calls a child that returns null and then, run again, a value, and reports what its outcome answers.</summary>
    [Job("job-parent-of-null-then-value-child", Backoff = "0s")]
    public static async Task<ChildValueSeen> ParentOfNullThenValueChild(JobContext ctx, CancellationToken ct) =>
        ChildValueSeen.Of(await ctx.CallChildAsync("lookup", TestJobsManifest.NullThenValue(new NullThenValueInput(5)), ct));

    /// <summary>
    /// The same call with one attempt, so a single charged failure would end this job, and reports what the outcome
    /// answers.
    /// </summary>
    [Job("job-parent-once-of-null-then-value-child", MaxAttempts = 1)]
    public static async Task<ChildValueSeen> ParentOnceOfNullThenValueChild(JobContext ctx, CancellationToken ct) =>
        ChildValueSeen.Of(await ctx.CallChildAsync("lookup", TestJobsManifest.NullThenValue(new NullThenValueInput(5)), ct));

    /// <summary>Calls a child that returns null and requires its result, which ends this job at once.</summary>
    [Job("job-parent-requires-null-child")]
    public static async Task<NullResultOutput> ParentRequiresNullChild(JobContext ctx, CancellationToken ct) =>
        (await ctx.CallChildAsync("lookup", TestJobsManifest.NullResult(new NullResultInput(1)), ct)).ValueOrThrow();

    /// <summary>
    /// Starts a child that declares no result under a name, then calls a job with a result under the same name, which
    /// finds the first child: its success has no value of the called job's result type.
    /// </summary>
    [Job("job-parent-name-clash", MaxAttempts = 1)]
    public static async Task<bool> ParentNameClash(JobContext ctx, CancellationToken ct)
    {
        await ctx.EnqueueChildAsync("lookup", ctx.JobNamespace, "void-child", JobPayload.None, ct: ct);
        return (await ctx.CallChildAsync("lookup", TestJobsManifest.NullResult(new NullResultInput(1)), ct)).HasValue;
    }

    /// <summary>Maps three children, the middle one returning null, and reports each item's status and result or none.</summary>
    [Job("job-parent-map-maybe")]
    public static async Task<string> ParentMapMaybe(JobContext ctx, CancellationToken ct)
    {
        var doubled = await ctx.MapAsync(
            "double",
            (int[])[1, 0, 3],
            n => n,
            n => TestJobsManifest.MaybeDouble(new MaybeDoubleInput(n)),
            ct
        );
        return string.Join(
            ",",
            doubled.Items.Select(item =>
                $"{item.Key}={item.Outcome.Status}:{(item.Outcome.TryGetValue(out var value) ? value.ToString(CultureInfo.InvariantCulture) : "none")}"
            )
        );
    }

    /// <summary>
    /// Waits on two signals, one raised with a value and one without, and reports what each typed outcome answers.
    /// </summary>
    [Job("job-signal-value-seen")]
    public static async Task<string> SignalValueSeen(JobContext ctx, CancellationToken ct)
    {
        var until = DateTimeOffset.UtcNow.AddMinutes(30);
        return string.Join(
            ",",
            Seen(await ctx.WaitSignalAsync<int>("with-value", TimeSpan.FromMinutes(30), ct)),
            Seen(await ctx.WaitSignalAsync<int>("without-value", TimeSpan.FromMinutes(30), ct)),
            Seen(await ctx.WaitSignalAsync<int>("until-with-value", until, ct)),
            Seen(await ctx.WaitSignalAsync<int>("until-without-value", until, ct))
        );

        static string Seen(SignalWaitOutcome<int> outcome) =>
            $"{outcome.HasValue}:{(outcome.TryGetValue(out var value) ? value.ToString(CultureInfo.InvariantCulture) : "none")}";
    }
}
