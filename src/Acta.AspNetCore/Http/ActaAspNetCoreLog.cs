using Microsoft.Extensions.Logging;

namespace Acta.AspNetCore;

/// <summary>
/// Every log line Acta.AspNetCore writes, as source-generated <see cref="LoggerMessageAttribute"/> methods held to the
/// same field vocabulary as Acta's own <c>ActaLog</c> (<c>LogVocabularyTests</c>).
/// </summary>
internal static partial class ActaAspNetCoreLog
{
    [LoggerMessage(Level = LogLevel.Error, Message = "Unhandled Acta API exception.")]
    public static partial void UnhandledApiException(ILogger log, Exception exception);
}
