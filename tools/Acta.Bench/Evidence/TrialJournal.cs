using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Acta.Bench;

// Buffered controller-side observation, included in the generator/resource budget.
// Cooperative interruption flushes it; a forceful kill may leave a truncated last line.
internal sealed class TrialJournal : IDisposable
{
    private readonly object _gate = new();
    private readonly StreamWriter _writer;
    private long _lastFlush = Stopwatch.GetTimestamp();

    public TrialJournal(string path) =>
        _writer = new StreamWriter(path, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), 65536);

    public void Append<T>(T value, JsonTypeInfo<T> type)
    {
        using var bytes = new MemoryStream();
        using (var json = new Utf8JsonWriter(bytes))
        {
            JsonSerializer.Serialize(json, value, type);
        }
        var line = Encoding.UTF8.GetString(bytes.GetBuffer(), 0, checked((int)bytes.Length));
        lock (_gate)
        {
            _writer.WriteLine(line);
            var now = Stopwatch.GetTimestamp();
            if (now - _lastFlush >= Stopwatch.Frequency)
            {
                _writer.Flush();
                _lastFlush = now;
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _writer.Dispose();
        }
    }
}
