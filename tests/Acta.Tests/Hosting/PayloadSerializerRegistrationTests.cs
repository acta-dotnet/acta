using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Hosting;

/// <summary>
/// Builder contract for <c>IActaBuilder.AddPayloadSerializer</c>: the serializer joins the payload registry under its
/// format id, beside the built-in ones.
/// </summary>
public sealed class PayloadSerializerRegistrationTests
{
    [Fact]
    public void A_registered_serializer_resolves_by_its_format_id()
    {
        var services = new ServiceCollection();
        services.UseActa(builder => builder.UseSqlite(static _ => { }).AddPayloadSerializer<CustomSerializer>());
        using var provider = services.BuildServiceProvider();

        var registry = provider.GetRequiredService<IJobPayloadSerializerRegistry>();

        Assert.IsType<CustomSerializer>(registry.Resolve(CustomSerializer.FormatId));
        Assert.True(registry.IsRegistered(JobPayloadFormat.Json.Id));
    }

    private sealed class CustomSerializer : IJobPayloadSerializer
    {
        public const byte FormatId = 200;

        public JobPayloadFormat Format { get; } = JobPayloadFormat.Custom(FormatId, "test-format");

        public JobPayload Serialize<T>(T value) => throw new NotSupportedException();

        public T Deserialize<T>(JobPayload payload) => throw new NotSupportedException();
    }
}
