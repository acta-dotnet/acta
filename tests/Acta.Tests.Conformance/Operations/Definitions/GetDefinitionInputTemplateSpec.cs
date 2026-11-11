using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Definitions;

/// <summary>Conformance for the input-template read: the in-process manifest answers, and a job this host did not register reads null.</summary>
[ConformanceSpec(
    "get-definition-input-template.manifest",
    "The input template comes from this host's generated manifest",
    Area = "Reads",
    Contract = "GetDefinitionInputTemplate returns a registered job's input type, format, and JSON skeleton from this host's manifest, and null for a job it did not register."
)]
[Deploys("add-numbers", "job-wait-signal")]
public sealed class GetDefinitionInputTemplateSpec : ActaRuntimeTestBase<ConformanceFixture, JobsOf<GetDefinitionInputTemplateSpec>>
{
    [Fact(DisplayName = "A Json job reports its input type, format, and a skeleton of its members")]
    public async Task Json_job_reports_its_template()
    {
        var template = await Operations.Definitions.GetInputTemplateAsync(
            TestNamespace,
            "add-numbers",
            TestContext.Current.CancellationToken
        );

        Assert.NotNull(template);
        Assert.Equal(typeof(AddNumbers).FullName, template.InputTypeName);
        Assert.Equal(JobPayloadFormat.Json, template.InputFormat);
        Assert.Equal("""{"left":0,"right":0}""", template.TemplateJson);
    }

    [Fact(DisplayName = "A job that takes no input reports the NoInput type and no skeleton")]
    public async Task No_input_job_has_no_template()
    {
        var template = await Operations.Definitions.GetInputTemplateAsync(
            TestNamespace,
            "job-wait-signal",
            TestContext.Current.CancellationToken
        );

        Assert.NotNull(template);
        Assert.Equal(typeof(NoInput).FullName, template.InputTypeName);
        Assert.Null(template.TemplateJson);
    }

    [Fact(DisplayName = "An unknown job or a namespace this host did not register reads null")]
    public async Task Unregistered_job_reads_null()
    {
        var ct = TestContext.Current.CancellationToken;
        Assert.Null(await Operations.Definitions.GetInputTemplateAsync(TestNamespace, "no-such-job", ct));
        Assert.Null(await Operations.Definitions.GetInputTemplateAsync(Unique("other-ns"), "add-numbers", ct));
    }

    [Fact(DisplayName = "A definition stored by another host but absent from this manifest reads null")]
    public async Task Stored_definition_alone_reads_null()
    {
        var ct = TestContext.Current.CancellationToken;
        var jobName = Unique("stored-only");
        await new ActaTestSeeder(Db).SeedJobDefinitionAsync(Runtime.RegisteredNamespaceIds[TestNamespace], name: jobName, ct: ct);

        Assert.Null(await Operations.Definitions.GetInputTemplateAsync(TestNamespace, jobName, ct));
    }
}
