# The whole program: choose a payload format for a job

The how-to is [/learn/501-payload-formats.md](/learn/501-payload-formats.md). This file holds every source file of `learn/500-payloads/501-payload-formats` as it is compiled, and the notes kept beside the code.

## Not in the packages

`UseLocalDatabase` and `LearnLab` are helpers of the Acta repository (`support/`), used so every program runs on any of the three databases and prints its rows. Do not copy them into an application: register the database with `UseSqlite`, `UsePostgres` or `UseSqlServer`, as in [how to run your first job](/learn/001-hello-acta.md). The rows a lab prints come from SQL views, which are the operator's contract; application code reads the same facts through `IJobs` and `IActaOperations`.

## The whole program

### Program.cs

```csharp
// How to choose a payload format for a job
// A job names the format its payload is stored in. This program runs custom formats side by side and compares their
// serialization time, enqueue time, readability, and stored bytes.
using Acta;
using Acta.Labs;
using Acta.Learn.PayloadFormats;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddSingleton(new LearnLab(builder.Configuration, args));

// learn: 2. Register a serializer for each format
// A custom format is an `IJobPayloadSerializer` in the application's container.
builder.Services.AddSingleton<IJobPayloadSerializer, ScalarV1Serializer>();
builder.Services.AddSingleton<IJobPayloadSerializer, JsonGzipSerializer>();

// learn: end

builder.Services.UseActa(j =>
{
    j.UseLocalDatabase(builder.Configuration);
    j.Run<PayloadFormatsJobs>("payload-formats");
});
builder.Services.AddHostedService<Primer>();

await builder.Build().RunAsync();
```

### Echo.cs

```csharp
namespace Acta.Learn.PayloadFormats;

/// <summary>
/// Echo a Guid via <c>scalar-v1</c>: 16 bytes on the wire in each direction.
/// </summary>
public sealed class EchoScalarHandler
{
    public const int TargetCount = 250;
    private static long _count;

    [Job("echo-scalar", Format = PayloadFormats.ScalarV1)]
    public Task<Guid> Handle(Guid input, CancellationToken ct)
    {
        var n = Interlocked.Increment(ref _count);
        if (n == TargetCount)
        {
            Console.WriteLine($"  scalar-v1   echo-scalar    [{n}/{TargetCount}]");
        }
        return Task.FromResult(input);
    }
}
```

### EchoGzip.cs

```csharp
namespace Acta.Learn.PayloadFormats;

/// <summary>
/// Guid in a thin record, encoded with <c>json-gzip</c>; at this size gzip framing makes it the largest wire format.
/// </summary>
public sealed record EchoGzip(Guid Value);

public sealed class EchoGzipHandler
{
    public const int TargetCount = 250;
    private static long _count;

    // learn: 3. Name the format on the job
    [Job("echo-gzip", Format = PayloadFormats.JsonGzip)]
    // learn: end
    public Task<EchoGzip> Handle(EchoGzip input, CancellationToken ct)
    {
        var n = Interlocked.Increment(ref _count);
        if (n == TargetCount)
        {
            Console.WriteLine($"  json-gzip   echo-gzip      [{n}/{TargetCount}]");
        }
        return Task.FromResult(input);
    }
}
```

### EchoJson.cs

```csharp
namespace Acta.Learn.PayloadFormats;

/// <summary>A normal JSON baseline: human-readable in <c>jobs_view</c>, with no custom serializer.</summary>
public sealed record EchoJson(Guid Value);

public sealed class EchoJsonHandler
{
    public const int TargetCount = 250;
    private static long _count;

    [Job("echo-json")]
    public Task<EchoJson> Handle(EchoJson input, CancellationToken ct)
    {
        var n = Interlocked.Increment(ref _count);
        if (n == TargetCount)
        {
            Console.WriteLine($"  json         echo-json      [{n}/{TargetCount}]");
        }
        return Task.FromResult(input);
    }
}
```

### PayloadFormats.cs

```csharp
namespace Acta.Learn.PayloadFormats;

/// <summary>
/// Format names and ids for the two custom serializers. The kebab-case names are <c>const string</c>
/// because <c>[Job(Format = ...)]</c> needs a compile-time constant, and hand-written because the
/// source generator reads them as ordinary source while building descriptors.
/// </summary>
public static class PayloadFormats
{
    // learn: 1. Give each format a name and an id
    // The name is what a job writes in its attribute, so it is a constant.
    public const string ScalarV1 = "scalar-v1";
    public const string JsonGzip = "json-gzip";

    public static readonly JobPayloadFormat ScalarV1Format = JobPayloadFormat.Custom(128, ScalarV1);
    public static readonly JobPayloadFormat JsonGzipFormat = JobPayloadFormat.Custom(130, JsonGzip);
    // learn: end
}
```

### Primer.cs

```csharp
using System.Diagnostics;
using Acta.Labs;

namespace Acta.Learn.PayloadFormats;

/// <summary>
/// Bulk-enqueues 250 echo jobs per format (750 total) in three round-trips via
/// <see cref="IJobs.EnqueueJobsAsync"/>. Every job round-trips a single Guid; only the wire
/// format differs.
/// </summary>
internal sealed class Primer(
    IJobs jobs,
    IActaOperations operations,
    IJobPayloadSerializerRegistry serializers,
    LearnLab lab,
    IHostApplicationLifetime lifetime
) : BackgroundService
{
    private const string Namespace = "payload-formats";
    private const int RowsPerFormat = 250;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var runId = $"payload-formats-{Guid.CreateVersion7():N}";
        var json = serializers.Resolve(JobPayloadFormat.Json.Id);
        var scalar = serializers.Resolve(PayloadFormats.ScalarV1Format.Id);
        var gzip = serializers.Resolve(PayloadFormats.JsonGzipFormat.Id);

        Console.WriteLine($"Primer: bulk-enqueueing 3 x {RowsPerFormat} echo jobs (1 round-trip per format)...");

        await EnqueueJobsAsync("echo-json", runId, _ => json.Serialize(new EchoJson(Guid.NewGuid())), ct);
        await EnqueueJobsAsync("echo-scalar", runId, _ => scalar.Serialize(Guid.NewGuid()), ct);
        await EnqueueJobsAsync("echo-gzip", runId, _ => gzip.Serialize(new EchoGzip(Guid.NewGuid())), ct);

        using var completionTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        completionTimeout.CancelAfter(TimeSpan.FromMinutes(2));
        while (true)
        {
            var done = await operations.Ledger.ListJobsAsync(
                new ListJobsQuery(
                    JobNamespace: Namespace,
                    Status: JobStatusCode.Succeeded,
                    CorrelationKey: runId,
                    PageSize: 1,
                    IncludeTotal: true
                ),
                completionTimeout.Token
            );
            if (done.TotalCount == RowsPerFormat * 3)
            {
                break;
            }
            await Task.Delay(100, completionTimeout.Token);
        }

        await lab.ShowAllAsync(
            "Explore one complete built-in JSON record before comparing formats",
            """
            SELECT *
            FROM jobs_view
            WHERE job_id = (
                SELECT MIN(job_id)
                FROM jobs_view
                WHERE namespace_name = @jobNamespace AND correlation_key = @runId AND definition_name = 'echo-json'
            )
            """,
            new { jobNamespace = Namespace, runId },
            ct
        );
        await lab.ShowAsync(
            "Operator readability in the curated jobs view",
            """
            SELECT definition_name, input_format, CASE WHEN input_text IS NULL THEN 'no' ELSE 'yes' END AS input_is_readable, COUNT(*) AS rows
            FROM jobs_view
            WHERE namespace_name = @jobNamespace AND correlation_key = @runId AND definition_name LIKE 'echo-%'
            GROUP BY definition_name, input_format,
                     CASE WHEN input_text IS NULL THEN 'no' ELSE 'yes' END
            ORDER BY definition_name
            """,
            new { jobNamespace = Namespace, runId },
            ct
        );
        await lab.ShowAsync(
            "Actual stored bytes (the lab expands the byte-length expression per provider)",
            """
            SELECT d.definition_name AS job_name, COUNT(*) AS rows, AVG({{bytes:j.input}}) AS avg_input_bytes, AVG({{bytes:r.result}}) AS avg_result_bytes
            FROM {{schema}}.jobs AS j
            JOIN {{schema}}.definitions AS d ON d.definition_id = j.definition_id
            JOIN {{schema}}.namespaces AS n ON n.namespace_id = j.namespace_id
            JOIN {{schema}}.jobs_state AS r ON r.job_id = j.job_id
            WHERE n.namespace_name = @jobNamespace AND j.correlation_key = @runId AND d.definition_name LIKE 'echo-%'
            GROUP BY d.definition_name
            ORDER BY d.definition_name
            """,
            new { jobNamespace = Namespace, runId },
            ct
        );
        lifetime.StopApplication();
    }

    private async Task EnqueueJobsAsync(string jobName, string runId, Func<int, JobPayload> buildPayload, CancellationToken ct)
    {
        var serializeTimer = Stopwatch.StartNew();
        var requests = new EnqueueJobRequest[RowsPerFormat];
        for (var i = 0; i < RowsPerFormat; i++)
        {
            requests[i] = new EnqueueJobRequest(Namespace, jobName, buildPayload(i), CorrelationKey: runId);
        }
        serializeTimer.Stop();

        var enqueueTimer = Stopwatch.StartNew();
        var outcomes = (await jobs.EnqueueJobsAsync(requests, ct)).Jobs;
        enqueueTimer.Stop();

        Console.WriteLine(
            $"  {jobName, -14} serialize={serializeTimer.ElapsedMilliseconds, 4} ms  enqueue={enqueueTimer.ElapsedMilliseconds, 4} ms  rows={outcomes.Count}"
        );
    }
}
```

### Serializers/JsonGzipSerializer.cs

```csharp
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Acta.Learn.PayloadFormats;

[JobPayloadFormatDeclaration(130, "json-gzip")]
public sealed class JsonGzipSerializer : IJobPayloadSerializer
{
    private static readonly JsonSerializerOptions DefaultJson = new(JsonSerializerDefaults.Web);

    private readonly JsonSerializerOptions _json;

    public JsonGzipSerializer()
        : this(DefaultJson) { }

    public JsonGzipSerializer(JsonSerializerOptions json)
    {
        _json = json;
    }

    public JobPayloadFormat Format => PayloadFormats.JsonGzipFormat;

    public JobPayload Serialize<T>(T value)
    {
        var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(value, _json);

        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
        {
            gzip.Write(jsonBytes);
        }
        return JobPayload.FromBytes(Format, output.ToArray());
    }

    public T Deserialize<T>(JobPayload payload)
    {
        if (payload.Format.Id != Format.Id)
        {
            throw new InvalidOperationException($"Expected payload format {Format}, got {payload.Format}.");
        }

        if (!MemoryMarshal.TryGetArray(payload.Data, out var segment) || segment.Array is null)
        {
            throw new InvalidOperationException("json-gzip expects array-backed payload memory.");
        }

        using var input = new MemoryStream(segment.Array, segment.Offset, segment.Count, writable: false, publiclyVisible: false);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);

        return JsonSerializer.Deserialize<T>(gzip, _json) ?? throw new JsonException($"Could not deserialize {typeof(T).FullName}.");
    }
}
```

### Serializers/ScalarV1Serializer.cs

```csharp
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Text;

namespace Acta.Learn.PayloadFormats;

/// <summary>
/// Serializer for the <c>scalar-v1</c> wire format. Supports primitive types only; see
/// <see cref="ScalarV1Encoding"/> for the wire spec.
/// </summary>
[JobPayloadFormatDeclaration(128, "scalar-v1")]
public sealed class ScalarV1Serializer : IJobPayloadSerializer
{
    public JobPayloadFormat Format => PayloadFormats.ScalarV1Format;

    public JobPayload Serialize<T>(T value) => JobPayload.FromBytes(Format, ScalarV1Encoding.Serialize(value));

    public T Deserialize<T>(JobPayload payload)
    {
        if (payload.Format.Id != Format.Id)
        {
            throw new InvalidOperationException($"Expected payload format {Format}, got {payload.Format}.");
        }
        return ScalarV1Encoding.Deserialize<T>(payload.Data.Span);
    }
}

/// <summary>
/// Wire spec for <c>scalar-v1</c>.
/// int/long: big-endian, 4/8 bytes.
/// bool: 1 byte; 0x00 false, 0x01 true.
/// Guid: 16 bytes, RFC 4122 network byte order.
/// DateTimeOffset: int64 Unix milliseconds, big-endian.
/// DateOnly: int32 days since 1970-01-01, big-endian.
/// string: UTF-8, no BOM.
/// </summary>
internal static class ScalarV1Encoding
{
    public static byte[] Serialize<T>(T value)
    {
        if (value is null)
        {
            throw new NotSupportedException("scalar-v1 does not support null values.");
        }

        if (typeof(T) == typeof(bool))
        {
            return [Unsafe.As<T, bool>(ref value) ? (byte)1 : (byte)0];
        }
        if (typeof(T) == typeof(int))
        {
            var bytes = new byte[4];
            BinaryPrimitives.WriteInt32BigEndian(bytes, Unsafe.As<T, int>(ref value));
            return bytes;
        }
        if (typeof(T) == typeof(long))
        {
            var bytes = new byte[8];
            BinaryPrimitives.WriteInt64BigEndian(bytes, Unsafe.As<T, long>(ref value));
            return bytes;
        }
        if (typeof(T) == typeof(Guid))
        {
            var bytes = new byte[16];
            if (!Unsafe.As<T, Guid>(ref value).TryWriteBytes(bytes, bigEndian: true, out _))
            {
                throw new InvalidOperationException("Failed to write Guid in network byte order.");
            }
            return bytes;
        }
        if (typeof(T) == typeof(DateTimeOffset))
        {
            var bytes = new byte[8];
            BinaryPrimitives.WriteInt64BigEndian(bytes, Unsafe.As<T, DateTimeOffset>(ref value).ToUnixTimeMilliseconds());
            return bytes;
        }
        if (typeof(T) == typeof(DateOnly))
        {
            var bytes = new byte[4];
            var epoch = new DateOnly(1970, 1, 1);
            BinaryPrimitives.WriteInt32BigEndian(bytes, Unsafe.As<T, DateOnly>(ref value).DayNumber - epoch.DayNumber);
            return bytes;
        }
        if (typeof(T) == typeof(string))
        {
            return Encoding.UTF8.GetBytes((string)(object)value);
        }

        throw new NotSupportedException($"scalar-v1 does not support values of type '{typeof(T).FullName}'.");
    }

    public static T Deserialize<T>(ReadOnlySpan<byte> bytes)
    {
        if (typeof(T) == typeof(bool))
        {
            EnsureLength(typeof(T), bytes.Length, 1);
            bool result = bytes[0] switch
            {
                0 => false,
                1 => true,
                _ => throw new FormatException($"Invalid scalar-v1 bool value 0x{bytes[0]:x2}."),
            };
            return Unsafe.As<bool, T>(ref result);
        }
        if (typeof(T) == typeof(int))
        {
            EnsureLength(typeof(T), bytes.Length, 4);
            int result = BinaryPrimitives.ReadInt32BigEndian(bytes);
            return Unsafe.As<int, T>(ref result);
        }
        if (typeof(T) == typeof(long))
        {
            EnsureLength(typeof(T), bytes.Length, 8);
            long result = BinaryPrimitives.ReadInt64BigEndian(bytes);
            return Unsafe.As<long, T>(ref result);
        }
        if (typeof(T) == typeof(Guid))
        {
            EnsureLength(typeof(T), bytes.Length, 16);
            Guid result = new(bytes[..16], bigEndian: true);
            return Unsafe.As<Guid, T>(ref result);
        }
        if (typeof(T) == typeof(DateTimeOffset))
        {
            EnsureLength(typeof(T), bytes.Length, 8);
            DateTimeOffset result = DateTimeOffset.FromUnixTimeMilliseconds(BinaryPrimitives.ReadInt64BigEndian(bytes));
            return Unsafe.As<DateTimeOffset, T>(ref result);
        }
        if (typeof(T) == typeof(DateOnly))
        {
            EnsureLength(typeof(T), bytes.Length, 4);
            var epoch = new DateOnly(1970, 1, 1);
            DateOnly result = epoch.AddDays(BinaryPrimitives.ReadInt32BigEndian(bytes));
            return Unsafe.As<DateOnly, T>(ref result);
        }
        if (typeof(T) == typeof(string))
        {
            string result = Encoding.UTF8.GetString(bytes);
            return (T)(object)result;
        }

        throw new NotSupportedException($"scalar-v1 does not support values of type '{typeof(T).FullName}'.");
    }

    private static void EnsureLength(Type type, int actual, int expected)
    {
        if (actual != expected)
        {
            throw new FormatException($"Invalid scalar-v1 {type.Name} length. Expected {expected} bytes, got {actual}.");
        }
    }
}
```

## Notes

### The problem

One mandatory JSON representation is convenient, but it can waste bytes and CPU for dense or tiny
payloads. Choosing only the smallest encoding, however, can make operations and contract evolution much
harder.

### Common approaches

| Format | Strength | Cost |
| --- | --- | --- |
| JSON | Inspectable, interoperable, easy to evolve | Larger and often slower |
| Binary codec (MessagePack, protobuf) | Compact and fast | A package to depend on, and less SQL/operator readability |
| Gzip JSON | Preserves the JSON model with fewer bytes | CPU cost and no direct readability |
| Custom scalar | Minimal representation | Tight contract and migration burden |

### Why this design

Acta makes the codec name/identifier part of the durable job definition. Custom serializers are
registered explicitly and source-validated. The runtime can therefore round-trip several formats
without pretending that opaque bytes have no contract.

### Trade-offs

Every long-lived format needs compatibility, deployment-order, and migration discipline. Compact custom
formats reduce operator readability, and compression may cost more CPU than the saved storage is worth.

### Run the experiment

```bash
dotnet run --project learn/500-payloads/501-payload-formats
```

The program runs a built-in JSON baseline plus two custom formats, gzip JSON and a custom scalar, times
serialization separately from the batch-enqueue round trip, waits for completion, and executes the printed
byte-size comparison. Both custom serializers are plain .NET with no package: a binary codec plugs into the
same `IJobPayloadSerializer` seam. Every run gets a fresh correlation key, so completion waits and
measurements exclude historical rows.

### Rows to inspect

Run with `--all-columns` to execute the visible `SELECT *` Explore query first. The default Notice
queries select the fields that prove the lesson, and the text below explains their meaning.

`jobs_view` shows the durable format and whether input is human-readable. The second query deliberately
descends to `jobs`/`jobs_state` to measure physical bytes; `{{bytes:...}}` and `{{schema}}` are expanded by
the lab for SQLite, PostgreSQL, or SQL Server. Base tables are internal, not an application query API.

### Break it

Remove one serializer registration and observe startup/contract failure rather than silent decoding with
the wrong codec. Change the payload shape without a compatibility plan and consider how old rows decode.

### When not to use

Stay with JSON when volume is modest, inspection matters, or ecosystem interoperability dominates. Use
blob/object references rather than any inline codec for genuinely large payloads. Benchmark realistic
data before choosing compression.

### Source trail

- [The related Engineering Lab](https://github.com/acta-dotnet/acta/blob/main/docs/engineering-labs.md)
- [`Primer.cs`](https://github.com/acta-dotnet/acta/blob/main/learn/500-payloads/501-payload-formats/Primer.cs)
- [`ScalarV1Serializer.cs`](https://github.com/acta-dotnet/acta/blob/main/learn/500-payloads/501-payload-formats/Serializers/ScalarV1Serializer.cs)
- [`JobPayloadSerializerRegistry.cs`](https://github.com/acta-dotnet/acta/blob/main/src/Acta/Payloads/JobPayloadSerializerRegistry.cs)
- [`ActaManifestGenerator.cs`](https://github.com/acta-dotnet/acta/blob/main/src/Acta.Generators/Jobs/ActaManifestGenerator.cs)
