using System.Collections.Immutable;
using Acta.Runtime.Kernel;
using Acta.Runtime.Modules.Execution.Api;
using Acta.Runtime.Modules.Execution.Definitions;
using Xunit;

namespace Acta.Tests.Runtime;

/// <summary>
/// Registration-time bounds in <see cref="DefinitionsService"/>: a code-declared ExecutionTimeout past
/// CancelAfter's ceiling, a non-ASCII or over-length RunbookUrl, or over-length display text fails
/// worker init with a named error, the same contract the operator override gate holds, instead of a
/// silent clamp at execution or a provider-specific write failure.
/// </summary>
public sealed class DefinitionRegistrationValidationTests
{
    private static readonly DateTime Gen = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static JobDescriptor Descriptor(string name) =>
        new(
            JobName: name,
            HandlerType: typeof(object),
            MethodName: "Run",
            InputType: typeof(object),
            OutputType: null,
            InputPayloadFormat: JobPayloadFormat.None,
            OutputPayloadFormat: null,
            InvocationKind: JobInvocationKind.Task,
            RequiresJobContextParameter: false,
            RequiresCancellationToken: false,
            Priority: JobPriorityCode.Normal,
            MaxAttempts: 1,
            AuditLevel: JobAuditLevelCode.Audit,
            AlertProfile: AlertProfileCode.None,
            Invoker: static async (_, _, _, _) =>
            {
                await Task.CompletedTask;
                return new JobHandlerInvocationResult(false, null);
            },
            DeserializeInput: static (_, _) => new object(),
            SerializeOutput: null
        );

    private static Task RegisterAsync(JobDescriptor descriptor) =>
        new DefinitionsService(new RejectingDefinitionStore()).RegisterAsync(
            1,
            Gen,
            [descriptor],
            [],
            TestContext.Current.CancellationToken
        );

    [Fact]
    public async Task An_execution_timeout_past_the_CancelAfter_ceiling_fails_registration()
    {
        var descriptor = Descriptor("slow") with { ExecutionTimeoutSeconds = JobDefinitionRegistration.MaxExecutionTimeoutSeconds + 1 };

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => RegisterAsync(descriptor));

        Assert.Contains("slow", ex.Message);
        Assert.Contains("ExecutionTimeout", ex.Message);
    }

    [Theory]
    [InlineData("https://rb/é")]
    [InlineData("https://rb/\U0001F600")]
    public async Task A_non_ascii_runbook_url_fails_registration(string url)
    {
        var ex = await Assert.ThrowsAsync<ArgumentException>(() => RegisterAsync(Descriptor("linked") with { RunbookUrl = url }));

        Assert.Contains("RunbookUrl", ex.Message);
    }

    [Fact]
    public async Task Over_length_text_fails_registration_instead_of_being_cut()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            RegisterAsync(Descriptor("url") with { RunbookUrl = "https://rb/" + new string('a', ActaTextLimits.DefinitionRunbookUrl) })
        );
        await Assert.ThrowsAsync<ArgumentException>(() =>
            RegisterAsync(Descriptor("name") with { DisplayName = new string('d', ActaTextLimits.DefinitionDisplayName + 1) })
        );
        await Assert.ThrowsAsync<ArgumentException>(() =>
            RegisterAsync(Descriptor("desc") with { Description = new string('x', ActaTextLimits.DefinitionDescription + 1) })
        );
    }

    [Fact]
    public async Task Values_at_the_bounds_pass_the_gate_and_reach_the_store()
    {
        var descriptor = Descriptor("edge") with
        {
            ExecutionTimeoutSeconds = JobDefinitionRegistration.MaxExecutionTimeoutSeconds,
            RunbookUrl = new string('r', ActaTextLimits.DefinitionRunbookUrl),
            DisplayName = new string('d', ActaTextLimits.DefinitionDisplayName),
            Description = new string('x', ActaTextLimits.DefinitionDescription),
        };

        // The store fake rejects every call, so reaching it is the proof that the row was built.
        await Assert.ThrowsAsync<NotSupportedException>(() => RegisterAsync(descriptor));
    }

    private sealed class RejectingDefinitionStore : IDefinitionStore
    {
        public Task<DefinitionPage> ListDefinitionsAsync(DefinitionPageRequest request, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<DefinitionOverrideOutcome> SetDefinitionOverridesAsync(SetDefinitionOverridesCommand command, CancellationToken ct) =>
            throw new NotSupportedException();

        public ValueTask<JobDefinitionDetail?> GetDefinitionAsync(int definitionId, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<StoredDefinitionContract>> GetDefinitionContractsAsync(int namespaceId, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<IReadOnlyDictionary<string, int>> RegisterDefinitionsAsync(RegisterDefinitionsCommand command, CancellationToken ct) =>
            throw new NotSupportedException();
    }
}
