using Acta.Verify;
using Xunit;

namespace Acta.Verify.Tests;

/// <summary>
/// Which command lines count as another run's leftover: one that dotnet itself runs from the run's DLL, by its bare
/// name or a path to it, and never a shell or wrapper whose text only quotes such a command.
/// </summary>
public sealed class CleanStartTests
{
    // No process on the box has this image, so only a command line below can match it.
    private static readonly string Name = "acta-leftover-" + Guid.NewGuid().ToString("N");

    [Theory]
    [InlineData("dotnet /x/{0}.dll worker")]
    [InlineData("/usr/share/dotnet/dotnet /x/{0}.dll worker")]
    [InlineData("\"C:\\Program Files\\dotnet\\dotnet.exe\" D:\\x\\{0}.dll worker")]
    public void A_dll_that_dotnet_itself_runs_is_a_leftover(string format)
    {
        var line = Line(format);

        Assert.Equal(line, Assert.Single(Running(line)).Value);
    }

    [Theory]
    [InlineData("bash -lc \"dotnet /x/{0}.dll chaos\"")]
    // The same shell as Linux reports it, its arguments joined with no quotes.
    [InlineData("bash -lc dotnet /x/{0}.dll chaos")]
    [InlineData("pwsh -NoProfile -Command \"dotnet D:\\x\\{0}.dll chaos\"")]
    // A tool whose name only begins as dotnet's does, with the run as its child.
    [InlineData("dotnet-trace collect -- dotnet /x/{0}.dll chaos")]
    public void A_shell_that_quotes_such_a_command_is_its_launcher_and_no_leftover(string format)
    {
        Assert.Empty(Running(Line(format)));
    }

    private static string Line(string format) => format.Replace("{0}", Name, StringComparison.Ordinal);

    /// <summary>What a box whose one other process has <paramref name="line"/> as its command line is found to run.</summary>
    private static Dictionary<int, string> Running(string line) =>
        CleanStart.Running([Name], new Dictionary<int, string> { [Environment.ProcessId + 1] = line });
}
