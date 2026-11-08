using Microsoft.Extensions.Logging;

namespace Acta.Tests.Runtime;

/// <summary>
/// Stands in for any diagnostic sink that fails: every line and every scope throws. A
/// <see cref="LoggerFactory"/> over it hands the failure back as an <see cref="AggregateException"/>, as a host's does.
/// </summary>
internal sealed class ThrowingLoggerProvider : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new ThrowingLogger();

    public void Dispose() { }

    internal sealed class ThrowingLogger : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => throw new InvalidOperationException("the log sink is down");

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        ) => throw new InvalidOperationException("the log sink is down");
    }
}
