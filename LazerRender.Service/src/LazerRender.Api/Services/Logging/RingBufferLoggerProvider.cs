using LazerRender.Contracts;
using Microsoft.Extensions.Logging;

namespace LazerRender.Api.Services.Logging;

/// <summary>
/// Feeds the service ring buffer from <c>ILogger</c> (Phase 8.2's "ILoggerProvider feeding it").
/// Registered as an <see cref="ILoggerProvider"/> in DI, so it is additive with the default console
/// logging: the same call is written to stdout <em>and</em> retained in memory for the admin console.
///
/// Every message is redacted and length-capped before it is stored, so the buffer is safe to render in
/// a browser. The category is folded into the message (there is no separate category field in
/// <see cref="LogRecord"/>) so an operator can still tell which component logged.
/// </summary>
public sealed class RingBufferLoggerProvider : ILoggerProvider
{
    /// <summary>Upper bound on a single service log record retained in memory.</summary>
    private const int MaxMessageLength = 4000;

    private readonly RingLogBuffer buffer;
    private readonly LogRedactor redactor;

    public RingBufferLoggerProvider(ServiceLogRingBuffer buffer, LogRedactor redactor)
    {
        this.buffer = buffer;
        this.redactor = redactor;
    }

    public ILogger CreateLogger(string categoryName) => new RingBufferLogger(categoryName, buffer, redactor);

    public void Dispose()
    {
    }

    private sealed class RingBufferLogger : ILogger
    {
        private readonly string categoryName;
        private readonly RingLogBuffer buffer;
        private readonly LogRedactor redactor;

        public RingBufferLogger(string categoryName, RingLogBuffer buffer, LogRedactor redactor)
        {
            this.categoryName = categoryName;
            this.buffer = buffer;
            this.redactor = redactor;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel)
            => ToSeverity(logLevel) is { } severity && severity >= buffer.MinimumSeverity;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (ToSeverity(logLevel) is not { } severity || severity < buffer.MinimumSeverity)
                return;

            string message = formatter(state, exception);

            if (exception is not null)
                message = $"{message} {exception.GetType().Name}: {exception.Message}";

            message = redactor.Redact(message);

            if (message.Length > MaxMessageLength)
                message = string.Concat(message.AsSpan(0, MaxMessageLength), "…[truncated]");

            string labelled = string.IsNullOrEmpty(categoryName) ? message : $"[{categoryName}] {message}";

            buffer.Write(LogSource.Service, severity, labelled);
        }

        private static LogSeverity? ToSeverity(LogLevel level) => level switch
        {
            LogLevel.Trace or LogLevel.Debug => LogSeverity.Debug,
            LogLevel.Information => LogSeverity.Information,
            LogLevel.Warning => LogSeverity.Warning,
            LogLevel.Error or LogLevel.Critical => LogSeverity.Error,
            _ => null, // LogLevel.None
        };
    }
}