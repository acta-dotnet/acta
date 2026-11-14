using Xunit;

namespace Acta.Tests.Architecture;

/// <summary>Every exception type Acta ships derives from <see cref="ActaException"/>, so one catch takes them all.</summary>
public sealed class ExceptionBaseTests
{
    [Fact]
    public void Every_exception_type_a_shipped_assembly_defines_derives_from_ActaException()
    {
        var exceptions = ShippedCode.Types.Where(typeof(Exception).IsAssignableFrom).ToList();

        Assert.Contains(typeof(JobControlException), exceptions);
        Assert.Empty(exceptions.Where(type => !typeof(ActaException).IsAssignableFrom(type)).Select(ShippedCode.Key));
    }
}
