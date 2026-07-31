using System.Diagnostics;
using Acta.Labs;

namespace Acta.Concepts.PayloadFormats;

/// <summary>
/// Bulk-enqueues 250 echo jobs per format (1000 total) in four round-trips via
/// <see cref="IJobs.EnqueueBatchAsync"/>. Every job round-trips a single Guid; only the wire
/// format differs.
/// </summary>
internal sealed class Primer(
    IJobs jobs,
    IActaOperations operations,
    IJobPayloadSerializerRegistry serializers,
    ConceptLab lab,
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
        var msgpack = serializers.Resolve(PayloadFormats.MsgpackFormat.Id);
        var gzip = serializers.Resolve(PayloadFormats.JsonGzipFormat.Id);

        Console.WriteLine($"Primer: bulk-enqueueing 4 x {RowsPerFormat} echo jobs (1 round-trip per format)...");

        await EnqueueBatchAsync("echo-json", runId, _ => json.Serialize(new EchoJson(Guid.NewGuid())), ct);
        await EnqueueBatchAsync("echo-scalar", runId, _ => scalar.Serialize(Guid.NewGuid()), ct);
        await EnqueueBatchAsync("echo-msgpack", runId, _ => msgpack.Serialize(new EchoMsgpack(Guid.NewGuid())), ct);
        await EnqueueBatchAsync("echo-gzip", runId, _ => gzip.Serialize(new EchoGzip(Guid.NewGuid())), ct);

        using var completionTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        completionTimeout.CancelAfter(TimeSpan.FromMinutes(2));
        while (true)
        {
            var done = await operations.ListJobsAsync(
                new ListJobsQuery(
                    JobNamespace: Namespace,
                    Status: JobStatusCode.Done,
                    CorrelationKey: runId,
                    PageSize: 1,
                    IncludeTotal: true
                ),
                completionTimeout.Token
            );
            if (done.TotalCount == RowsPerFormat * 4)
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
                WHERE namespace = @jobNamespace AND correlation_key = @runId AND job_name = 'echo-json'
            )
            """,
            new { jobNamespace = Namespace, runId },
            ct
        );
        await lab.ShowAsync(
            "Operator readability in the curated jobs view",
            """
            SELECT job_name, input_format, CASE WHEN input_text IS NULL THEN 'no' ELSE 'yes' END AS input_is_readable, COUNT(*) AS rows
            FROM jobs_view
            WHERE namespace = @jobNamespace AND correlation_key = @runId AND job_name LIKE 'echo-%'
            GROUP BY job_name, input_format,
                     CASE WHEN input_text IS NULL THEN 'no' ELSE 'yes' END
            ORDER BY job_name
            """,
            new { jobNamespace = Namespace, runId },
            ct
        );
        await lab.ShowAsync(
            "Actual stored bytes (the lab expands the byte-length expression per provider)",
            """
            SELECT d.name AS job_name, COUNT(*) AS rows, AVG({{bytes:j.input}}) AS avg_input_bytes, AVG({{bytes:r.result}}) AS avg_result_bytes
            FROM {{schema}}.jobs AS j
            JOIN {{schema}}.definitions AS d ON d.id = j.definition_id
            JOIN {{schema}}.namespaces AS n ON n.id = j.namespace_id
            LEFT JOIN {{schema}}.results AS r ON r.job_id = j.id
            WHERE n.name = @jobNamespace AND j.correlation_key = @runId AND d.name LIKE 'echo-%'
            GROUP BY d.name
            ORDER BY d.name
            """,
            new { jobNamespace = Namespace, runId },
            ct
        );
        lifetime.StopApplication();
    }

    private async Task EnqueueBatchAsync(string jobName, string runId, Func<int, JobPayload> buildPayload, CancellationToken ct)
    {
        var serializeTimer = Stopwatch.StartNew();
        var requests = new JobEnqueueRequest[RowsPerFormat];
        for (var i = 0; i < RowsPerFormat; i++)
        {
            requests[i] = new JobEnqueueRequest(Namespace, jobName, buildPayload(i), CorrelationKey: runId);
        }
        serializeTimer.Stop();

        var enqueueTimer = Stopwatch.StartNew();
        var outcomes = await jobs.EnqueueBatchAsync(requests, ct);
        enqueueTimer.Stop();

        Console.WriteLine(
            $"  {jobName, -14} serialize={serializeTimer.ElapsedMilliseconds, 4} ms  enqueue={enqueueTimer.ElapsedMilliseconds, 4} ms  rows={outcomes.Count}"
        );
    }
}
