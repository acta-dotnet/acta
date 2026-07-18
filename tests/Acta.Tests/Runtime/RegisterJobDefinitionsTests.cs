using System.Collections.Immutable;
using Acta.Features.Definitions;
using Acta.Payloads;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Runtime;

/// <summary>
/// <see cref="RegisterJobDefinitions"/>'s C#-side row building: the only remaining Backoff gate for
/// hand-authored <see cref="IActaManifest"/> descriptors, which bypass the source generator's
/// compile-time check. The exception fires before any DB call, so <c>db</c>/<c>dialect</c> are unused.
/// </summary>
public class RegisterJobDefinitionsTests
{
    private static JobDescriptor Desc(string name, string? backoff) =>
        new(
            JobName: name,
            HandlerType: typeof(object),
            MethodName: "M",
            InputType: typeof(int),
            OutputType: null,
            InputPayloadFormat: JobPayloadFormat.Json,
            OutputPayloadFormat: null,
            InvocationKind: default,
            RequiresJobContextParameter: false,
            RequiresCancellationToken: false,
            Priority: default,
            MaxAttempts: 1,
            AuditLevel: default,
            AlertProfile: default,
            Invoker: null!,
            DeserializeInput: null!,
            SerializeOutput: null
        )
        {
            Backoff = backoff,
        };

    [Fact]
    public async Task Invalid_backoff_expression_fails_registration_fast()
    {
        var descriptors = ImmutableArray.Create(Desc("bad-job", "garbage"));

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            new DefinitionsService(null!).RegisterAsync(
                namespaceId: 1,
                DateTime.UtcNow,
                descriptors,
                stored: [],
                ct: TestContext.Current.CancellationToken
            )
        );

        Assert.Contains("bad-job", ex.Message);
        Assert.Contains("garbage", ex.Message);
    }

    [Fact]
    public async Task Backoff_over_max_length_fails_registration_fast()
    {
        var tooLong = "1s.." + new string('9', 100) + "s";
        var descriptors = ImmutableArray.Create(Desc("bad-length", tooLong));

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            new DefinitionsService(null!).RegisterAsync(
                namespaceId: 1,
                DateTime.UtcNow,
                descriptors,
                stored: [],
                ct: TestContext.Current.CancellationToken
            )
        );

        Assert.Contains("bad-length", ex.Message);
    }
}
