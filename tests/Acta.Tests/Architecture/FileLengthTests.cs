using Xunit;

namespace Acta.Tests.Architecture;

/// <summary>
/// Caps every C# file under <c>src/</c> at 700 lines; files that predate the cap are listed in the <c>length</c>
/// ratchet. The 150-line method cap is MA0051's, in the build.
/// </summary>
public sealed class FileLengthTests
{
    private const int MaxFileLines = 700;

    [Fact]
    public void Source_files_stay_within_the_line_cap()
    {
        var tooLong = ShippedCode
            .SourceFiles("*.cs")
            .Where(file => File.ReadLines(file.Path).Count() > MaxFileLines)
            .Select(file => (Path.GetFileName(file.Path), (string?)file.Relative));

        Ratchet.Check("length", tooLong, $"Split the file so it stays within {MaxFileLines} lines.");
    }
}
