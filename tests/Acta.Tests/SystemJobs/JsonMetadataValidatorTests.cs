using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Acta.Testing;
using Acta.Tests.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TestJobs;
using Xunit;

namespace Acta.Tests.SystemJobs;

/// <summary>
/// The reflection-off start check: every registered job whose JSON input or result has no metadata in Acta's own JSON
/// serializer is named in one refusal. The serializer here reads metadata from the resolver chain a reflection-off build
/// uses, with an application resolver that supplies none. The test process runs with reflection on, so the switch itself
/// is proven only to leave a start alone; the native publish gate runs the reflection-off side.
/// </summary>
public sealed class JsonMetadataValidatorTests
{
    private static readonly JsonSerializerOptions ReflectionFree = new(JsonSerializerDefaults.Web)
    {
        TypeInfoResolver = JsonJobPayloadSerializer.ReflectionFreeResolver(new NoMetadata()),
    };

    private sealed record Invoice(int Number);

    private sealed record Receipt(int Number);

    [Fact]
    public void A_job_whose_input_has_no_metadata_is_named_with_its_type()
    {
        var refusal = JsonMetadataValidator.Refusal([Catalog(Json("send-invoice", typeof(Invoice)))], Serializers());

        Assert.Contains($"job 'send-invoice' input '{typeof(Invoice).FullName}'", refusal);
    }

    [Fact]
    public void A_job_whose_result_has_no_metadata_is_named_with_its_type()
    {
        var refusal = JsonMetadataValidator.Refusal([Catalog(Json("tally", typeof(int), typeof(Receipt)))], Serializers());

        Assert.Contains($"job 'tally' result '{typeof(Receipt).FullName}'", refusal);
        Assert.DoesNotContain("job 'tally' input", refusal);
    }

    [Fact]
    public void Every_missing_type_is_listed_in_one_refusal_that_names_the_fix()
    {
        var refusal = JsonMetadataValidator.Refusal(
            [Catalog(Json("send-invoice", typeof(Invoice))), Catalog(Json("tally", typeof(int), typeof(Receipt)))],
            Serializers()
        );

        Assert.Equal(
            "Native AOT runs System.Text.Json without reflection, and the JSON serializer has no metadata for these job payload "
                + $"types, so the first use of each would throw: job 'send-invoice' input '{typeof(Invoice).FullName}'; job 'tally' "
                + $"result '{typeof(Receipt).FullName}'. Add each type to the JsonSerializerContext the host registers with "
                + "UseJsonPayloads; for a handler that takes several input parameters, declare its input record and give the "
                + "handler that one parameter. Step, variable and signal types are checked when first used.",
            refusal
        );
    }

    // Only a JSON type needs JSON metadata: neither a type the registry carries as none, bytes, or a custom format, nor a
    // type Acta resolves itself.
    [Fact]
    public void No_input_bytes_custom_format_and_scalar_jobs_pass()
    {
        var refusal = JsonMetadataValidator.Refusal(
            [
                Catalog(
                    Descriptor("sweep", typeof(NoInput), JobPayloadFormat.None, null, null),
                    Descriptor("upload", typeof(byte[]), JobPayloadFormat.Bytes, typeof(byte[]), JobPayloadFormat.Bytes),
                    Descriptor(
                        "pack",
                        typeof(Invoice),
                        JobPayloadFormat.Custom(200, "packed"),
                        typeof(Receipt),
                        JobPayloadFormat.Custom(200, "packed")
                    ),
                    Json("double", typeof(int), typeof(long))
                ),
            ],
            Serializers()
        );

        Assert.Null(refusal);
    }

    [Fact]
    public void A_json_serializer_the_host_registered_is_not_asked()
    {
        var serializers = new JobPayloadSerializerRegistry([new JsonJobPayloadSerializer(ReflectionFree), new HostJson()]);

        Assert.Null(JsonMetadataValidator.Refusal([Catalog(Json("send-invoice", typeof(Invoice)))], serializers));
    }

    // The same registrations the refusal names start cleanly while reflection is on.
    [Fact]
    public void With_reflection_on_a_host_whose_types_have_no_metadata_starts()
    {
        using var services = new ScriptedSession(static call => throw new NotSupportedException(call.Stem)).Services(acta =>
        {
            acta.Reference<TestJobsManifest>("billing");
            acta.Services.AddSingleton<IJobPayloadSerializer>(new JsonJobPayloadSerializer(ReflectionFree));
        });

        Assert.True(JsonSerializer.IsReflectionEnabledByDefault);
        Assert.NotNull(
            JsonMetadataValidator.Refusal(
                [
                    new CatalogDeclaration(
                        "billing",
                        [new ManifestRegistration(typeof(TestJobsManifest), () => TestJobsManifest.Descriptors)]
                    ),
                ],
                services.GetRequiredService<IJobPayloadSerializerRegistry>()
            )
        );
        var check = Assert.Single(services.GetServices<IValidateOptions<JobsOptions>>().OfType<JsonMetadataValidator>());
        Assert.True(check.Validate(Options.DefaultName, new JobsOptions()).Skipped);
        Assert.NotNull(services.GetRequiredService<IOptions<JobsOptions>>().Value);
    }

    // The check rides JobsOptions validation, which UseActa runs on start. A test host runs every start validation before
    // anything starts, as a generic host does, so a host with no worker is refused too. Nothing reads these options, so
    // only that start validation can refuse them.
    [Fact]
    public async Task A_test_host_with_no_worker_runs_the_start_validation()
    {
        var ex = await Assert.ThrowsAsync<OptionsValidationException>(() =>
            ActaTestHost.StartAsync(
                (j, _) => j.UseSqliteEphemeral().Reference<TestJobsManifest>("billing"),
                new ActaTestHostOptions
                {
                    ConfigureServices = s => s.AddOptions<UnreadOptions>().Validate(_ => false, "refused at start").ValidateOnStart(),
                },
                TestContext.Current.CancellationToken
            )
        );

        Assert.Equal("refused at start", ex.Message);
    }

    private sealed class UnreadOptions;

    private static JobPayloadSerializerRegistry Serializers() =>
        new([new JsonJobPayloadSerializer(ReflectionFree), new BytesJobPayloadSerializer()]);

    private static CatalogDeclaration Catalog(params JobDescriptor[] descriptors) =>
        new("billing", [new ManifestRegistration(typeof(object), () => new JobDescriptorManifest([.. descriptors]))]);

    private static JobDescriptor Json(string jobName, Type inputType, Type? resultType = null) =>
        Descriptor(jobName, inputType, JobPayloadFormat.Json, resultType, resultType is null ? null : JobPayloadFormat.Json);

    private static JobDescriptor Descriptor(
        string jobName,
        Type inputType,
        JobPayloadFormat inputFormat,
        Type? resultType,
        JobPayloadFormat? resultFormat
    ) =>
        new(
            JobName: jobName,
            HandlerType: typeof(object),
            MethodName: "Handle",
            InputType: inputType,
            ResultType: resultType,
            InputPayloadFormat: inputFormat,
            ResultPayloadFormat: resultFormat,
            Priority: JobPriorityCode.Bulk,
            MaxAttempts: 1,
            EventRecording: default,
            AlertProfile: AlertProfileCode.None,
            FiringFailurePolicy: FiringFailurePolicyCode.Continue,
            ScheduledDelivery: ScheduledDeliveryCode.Permanent,
            Invoker: null!,
            DeserializeInput: null!,
            SerializeResult: null
        );

    private sealed class NoMetadata : IJsonTypeInfoResolver
    {
        public JsonTypeInfo? GetTypeInfo(Type type, JsonSerializerOptions options) => null;
    }

    private sealed class HostJson : IJobPayloadSerializer
    {
        public JobPayloadFormat Format => JobPayloadFormat.Json;

        public JobPayload Serialize<T>(T value) => throw new NotSupportedException();

        public T Deserialize<T>(JobPayload payload) => throw new NotSupportedException();
    }
}
