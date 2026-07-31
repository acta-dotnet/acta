using System.Text;
using Acta;

namespace TestJobs;

public sealed record VariableLifecycleResult(
    bool ExistsBefore,
    bool ExistsAfterSet,
    string? AbsentValue,
    string DefaultValue,
    string RequiredValue,
    string RequiredAbsentMessage,
    int InsertedRetryCount,
    int ExistingRetryCount,
    int FactoryCalls,
    bool DeleteFirst,
    bool DeleteSecond,
    string? DeletedValue,
    int DefaultIntAbsent,
    bool RequiredAbsentValueTypeThrew
);

public sealed record VariablePersistenceResult(string EmptyText, int EmptyBytesLength);

public sealed record VariableVersioningResult(
    string OverwrittenValue,
    int GetOrSetInsertedValue,
    int GetOrSetExistingValue,
    int FactoryCalls
);

public sealed record VariableValidationResult(
    bool InvalidNamesRejected,
    bool ReservedSetRejected,
    bool ReservedDeleteRejected,
    bool NonePayloadRejected,
    bool UnregisteredPayloadRejected,
    bool JsonNullPayloadRejected,
    int EmptyBytesLength,
    bool NullSetRejected,
    bool NullFactoryRejected
);

public sealed record VariableRaceResult(int StoredValue, int DistinctObservedValues, int[] ObservedValues, int FactoryCalls);

public sealed record VariableComplexObject(string Data);

public sealed record VariableSomeRecord(string Data, int Number);

public sealed record VariableRoundTripResult(
    bool PrimitiveValues,
    bool StringValues,
    bool ObjectValues,
    string OverwrittenValue,
    int LargeValueLength
);

public sealed record VariableCorruptReadResult(bool Rejected, bool FactoryRan);

public static class JobVariableProbes
{
    [Job("job-variable-lifecycle")]
    public static async Task<VariableLifecycleResult> Lifecycle(JobContext ctx, CancellationToken ct)
    {
        var absent = await ctx.GetVariableOrDefaultAsync<string>("fetch.status", ct);
        var existsBefore = await ctx.ExistsVariableAsync("fetch.status", ct);
        var defaultValue = await ctx.GetVariableOrDefaultAsync("missing.value", "fallback", ct);
        // Value-type default: an absent int must return the supplied default, not default(int) == 0.
        var defaultIntAbsent = await ctx.GetVariableOrDefaultAsync("missing.count", -1, ct);

        var requiredAbsentValueTypeThrew = false;
        try
        {
            await ctx.GetRequiredVariableAsync<int>("never.set.count", ct);
        }
        catch (InvalidOperationException)
        {
            requiredAbsentValueTypeThrew = true;
        }

        var requiredAbsentMessage = "";
        try
        {
            await ctx.GetRequiredVariableAsync<string>("never.set", ct);
        }
        catch (InvalidOperationException ex)
        {
            requiredAbsentMessage = ex.Message;
        }

        await ctx.SetVariableAsync("fetch.status", "done", ct);
        var existsAfterSet = await ctx.ExistsVariableAsync("fetch.status", ct);
        var required = await ctx.GetRequiredVariableAsync<string>("fetch.status", ct);

        var (inserted, existing, factoryCalls) = await GetOrSetRetryCountTwiceAsync(ctx, ct);

        await ctx.SetVariableAsync("cleanup.target", "remove-me", ct);
        var deleteFirst = await ctx.DeleteVariableAsync("cleanup.target", ct);
        var deleteSecond = await ctx.DeleteVariableAsync("cleanup.target", ct);
        var deleted = await ctx.GetVariableOrDefaultAsync<string>("cleanup.target", ct);

        return new VariableLifecycleResult(
            existsBefore,
            existsAfterSet,
            absent,
            defaultValue,
            required,
            requiredAbsentMessage,
            inserted,
            existing,
            factoryCalls,
            deleteFirst,
            deleteSecond,
            deleted,
            defaultIntAbsent,
            requiredAbsentValueTypeThrew
        );
    }

    [Job("job-variable-persistence")]
    public static async Task<VariablePersistenceResult> Persistence(JobContext ctx, CancellationToken ct)
    {
        await ctx.SetVariableAsync("fetch.status", "done", ct);

        await ctx.SetVariableAsync("payload.empty-text", JobPayload.Text(string.Empty), ct);
        var emptyText = await ctx.GetRequiredVariableAsync<string>("payload.empty-text", ct);

        await ctx.SetVariableAsync("payload.empty-bytes", JobPayload.Bytes([]), ct);
        var emptyBytes = await ctx.GetRequiredVariableAsync<byte[]>("payload.empty-bytes", ct);

        await ctx.SetProgressAsync("stage-two", ct);

        return new VariablePersistenceResult(emptyText, emptyBytes.Length);
    }

    [Job("job-variable-versioning")]
    public static async Task<VariableVersioningResult> Versioning(JobContext ctx, CancellationToken ct)
    {
        await ctx.SetVariableAsync("fetch.status", "v1", ct);
        await ctx.SetVariableAsync("fetch.status", "v2", ct);
        var overwritten = await ctx.GetRequiredVariableAsync<string>("fetch.status", ct);

        var (inserted, existing, factoryCalls) = await GetOrSetRetryCountTwiceAsync(ctx, ct);

        return new VariableVersioningResult(overwritten, inserted, existing, factoryCalls);
    }

    [Job("job-variable-validation")]
    public static async Task<VariableValidationResult> Validation(JobContext ctx, CancellationToken ct)
    {
        var invalidNames = new[] { "", "cursor_id", ".cursor", "cursor.", "cursor..id", new string('a', 129) };

        var invalidNamesRejected = true;
        foreach (var name in invalidNames)
        {
            invalidNamesRejected &= await ThrowsAsync<ArgumentException>(() => ctx.ExistsVariableAsync(name, ct));
        }

        var reservedSetRejected = await ThrowsAsync<ArgumentException>(() => ctx.SetVariableAsync("sys.progress", "bad", ct));
        var reservedDeleteRejected = await ThrowsAsync<ArgumentException>(() => ctx.DeleteVariableAsync("sys.progress", ct));
        var nonePayloadRejected = await ThrowsAsync<ArgumentException>(() => ctx.SetVariableAsync("payload.none", JobPayload.None, ct));
        var unregisteredPayloadRejected = await ThrowsAsync<InvalidOperationException>(() =>
            ctx.SetVariableAsync("payload.custom", JobPayload.CopyBytes(JobPayloadFormat.Custom(128, "test-custom"), [1]), ct)
        );
        var jsonNullPayloadRejected = await ThrowsAsync<ArgumentException>(() =>
            ctx.SetVariableAsync("payload.json-null", JobPayload.CopyBytes(JobPayloadFormat.Json, Encoding.UTF8.GetBytes("null")), ct)
        );
        var nullSetRejected = await ThrowsAsync<ArgumentNullException>(() => ctx.SetVariableAsync<string>("null.value", null!, ct));
        var nullFactoryRejected = await ThrowsAsync<InvalidOperationException>(() =>
            ctx.GetOrSetVariableAsync<string>("null.factory", () => null!, ct)
        );

        await ctx.SetVariableAsync("payload.empty-bytes", JobPayload.Bytes([]), ct);
        var emptyBytes = await ctx.GetRequiredVariableAsync<byte[]>("payload.empty-bytes", ct);

        return new VariableValidationResult(
            invalidNamesRejected,
            reservedSetRejected,
            reservedDeleteRejected,
            nonePayloadRejected,
            unregisteredPayloadRejected,
            jsonNullPayloadRejected,
            emptyBytes.Length,
            nullSetRejected,
            nullFactoryRejected
        );
    }

    [Job("job-variable-race")]
    public static async Task<VariableRaceResult> Race(JobContext ctx, CancellationToken ct)
    {
        var factoryCalls = 0;
        var tasks = Enumerable
            .Range(0, 16)
            .Select(i =>
                ctx.GetOrSetVariableAsync(
                    "race.winner",
                    async token =>
                    {
                        Interlocked.Increment(ref factoryCalls);
                        await Task.Delay(10, token);
                        return i;
                    },
                    ct
                )
            )
            .ToArray();

        var observed = await Task.WhenAll(tasks);
        var stored = await ctx.GetRequiredVariableAsync<int>("race.winner", ct);
        return new VariableRaceResult(stored, observed.Distinct().Count(), observed, factoryCalls);
    }

    private static readonly string[] value = new[] { "apple", "banana", "cherry" };

    [Job("job-variable-roundtrip")]
    public static async Task<VariableRoundTripResult> RoundTrip(JobContext ctx, CancellationToken ct)
    {
        await ctx.SetVariableAsync("primitive.true", true, ct);
        await ctx.SetVariableAsync("primitive.false", false, ct);
        await ctx.SetVariableAsync("primitive.int-max", int.MaxValue, ct);
        await ctx.SetVariableAsync("primitive.int-min", int.MinValue, ct);
        await ctx.SetVariableAsync("primitive.long-max", long.MaxValue, ct);
        await ctx.SetVariableAsync("primitive.long-min", long.MinValue, ct);
        await ctx.SetVariableAsync("primitive.decimal", 12345.67m, ct);
        await ctx.SetVariableAsync("primitive.double", 123.456d, ct);
        await ctx.SetVariableAsync("primitive.float", 123.456f, ct);
        await ctx.SetVariableAsync("primitive.datetime", new DateTime(2025, 1, 1, 12, 30, 0, DateTimeKind.Unspecified), ct);

        await ctx.SetVariableAsync("string.empty", string.Empty, ct);
        await ctx.SetVariableAsync("string.whitespace", "    ", ct);
        await ctx.SetVariableAsync("string.special", "!@#$%^&*()_+-={}[]:\";'<>?,./", ct);
        await ctx.SetVariableAsync("string.unicode", "你好世界", ct);
        await ctx.SetVariableAsync("string.emoji", "🔥❤️🚀 Individuals and interactions 福", ct);

        await ctx.SetVariableAsync("object.complex", new VariableComplexObject("complex test"), ct);
        await ctx.SetVariableAsync("object.record", new VariableSomeRecord("record test", 123), ct);
        await ctx.SetVariableAsync("object.list", new List<int> { 1, 2, 3, 4, 5 }, ct);
        await ctx.SetVariableAsync("object.array", value, ct);
        await ctx.SetVariableAsync("object.dictionary", new Dictionary<string, int> { { "key1", 100 }, { "key2", 200 } }, ct);

        await ctx.SetVariableAsync("overwrite.value", "initial", ct);
        await ctx.SetVariableAsync("overwrite.value", "updated", ct);

        await ctx.SetVariableAsync("large.value", new string('A', 10_000), ct);

        var primitiveValues =
            await ctx.GetRequiredVariableAsync<bool>("primitive.true", ct)
            && !await ctx.GetRequiredVariableAsync<bool>("primitive.false", ct)
            && await ctx.GetRequiredVariableAsync<int>("primitive.int-max", ct) == int.MaxValue
            && await ctx.GetRequiredVariableAsync<int>("primitive.int-min", ct) == int.MinValue
            && await ctx.GetRequiredVariableAsync<long>("primitive.long-max", ct) == long.MaxValue
            && await ctx.GetRequiredVariableAsync<long>("primitive.long-min", ct) == long.MinValue
            && await ctx.GetRequiredVariableAsync<decimal>("primitive.decimal", ct) == 12345.67m
            && await ctx.GetRequiredVariableAsync<double>("primitive.double", ct) == 123.456d
            && await ctx.GetRequiredVariableAsync<float>("primitive.float", ct) == 123.456f
            && await ctx.GetRequiredVariableAsync<DateTime>("primitive.datetime", ct)
                == new DateTime(2025, 1, 1, 12, 30, 0, DateTimeKind.Unspecified);

        var stringValues =
            await ctx.GetRequiredVariableAsync<string>("string.empty", ct) == string.Empty
            && await ctx.GetRequiredVariableAsync<string>("string.whitespace", ct) == "    "
            && await ctx.GetRequiredVariableAsync<string>("string.special", ct) == "!@#$%^&*()_+-={}[]:\";'<>?,./"
            && await ctx.GetRequiredVariableAsync<string>("string.unicode", ct) == "你好世界"
            && await ctx.GetRequiredVariableAsync<string>("string.emoji", ct) == "🔥❤️🚀 Individuals and interactions 福";

        var list = await ctx.GetRequiredVariableAsync<List<int>>("object.list", ct);
        var array = await ctx.GetRequiredVariableAsync<string[]>("object.array", ct);
        var dictionary = await ctx.GetRequiredVariableAsync<Dictionary<string, int>>("object.dictionary", ct);
        var objectValues =
            await ctx.GetRequiredVariableAsync<VariableComplexObject>("object.complex", ct) == new VariableComplexObject("complex test")
            && await ctx.GetRequiredVariableAsync<VariableSomeRecord>("object.record", ct) == new VariableSomeRecord("record test", 123)
            && list.SequenceEqual([1, 2, 3, 4, 5])
            && array.SequenceEqual(["apple", "banana", "cherry"])
            && dictionary.Count == 2
            && dictionary["key1"] == 100
            && dictionary["key2"] == 200;

        var overwritten = await ctx.GetRequiredVariableAsync<string>("overwrite.value", ct);
        var large = await ctx.GetRequiredVariableAsync<string>("large.value", ct);

        return new VariableRoundTripResult(primitiveValues, stringValues, objectValues, overwritten, large.Length);
    }

    [Job("job-variable-corrupt-reader")]
    public static async Task<VariableCorruptReadResult> CorruptReader(JobContext ctx, CancellationToken ct)
    {
        var factoryRan = false;
        var rejected = await ThrowsAnyAsync(() =>
            ctx.GetOrSetVariableAsync(
                "corrupt.value",
                () =>
                {
                    factoryRan = true;
                    return new VariableComplexObject("fallback");
                },
                ct
            )
        );
        return new VariableCorruptReadResult(rejected, factoryRan);
    }

    // First call inserts "retry-count" = 7; the second must return the stored 7 and never run its
    // factory (so factoryCalls stays 1). Shared by the lifecycle (first-wins) and versioning
    // (no version bump) probes.
    private static async Task<(int Inserted, int Existing, int FactoryCalls)> GetOrSetRetryCountTwiceAsync(
        JobContext ctx,
        CancellationToken ct
    )
    {
        var factoryCalls = 0;
        var inserted = await ctx.GetOrSetVariableAsync(
            "retry-count",
            () =>
            {
                factoryCalls++;
                return 7;
            },
            ct
        );
        var existing = await ctx.GetOrSetVariableAsync(
            "retry-count",
            () =>
            {
                factoryCalls++;
                return 99;
            },
            ct
        );
        return (inserted, existing, factoryCalls);
    }

    private static async Task<bool> ThrowsAsync<TException>(Func<Task> action)
        where TException : Exception
    {
        try
        {
            await action();
            return false;
        }
        catch (TException)
        {
            return true;
        }
    }

    private static async Task<bool> ThrowsAnyAsync(Func<Task> action)
    {
        try
        {
            await action();
            return false;
        }
        catch
        {
            return true;
        }
    }
}
