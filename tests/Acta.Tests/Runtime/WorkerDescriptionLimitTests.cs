using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Runtime;

/// <summary>A worker's declared namespace description is held to the width the column and the namespace update allow.</summary>
public sealed class WorkerDescriptionLimitTests
{
    [Fact]
    public void A_description_at_the_stored_width_is_accepted_and_one_past_it_refused()
    {
        var builder = new ActaBuilder(new ServiceCollection());

        builder.Run("wide", w => w.Description = new string('x', TextLimits.NamespaceDescription));
        var refused = Assert.Throws<ArgumentException>(() =>
            builder.Run("too-wide", w => w.Description = new string('x', TextLimits.NamespaceDescription + 1))
        );

        Assert.Equal(TextLimits.NamespaceDescription, builder.Workers.Single().Description!.Length);
        Assert.Contains("<= 512", refused.Message, StringComparison.Ordinal);
    }
}
