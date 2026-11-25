using System.IO;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;

namespace VNTextPatch.Shared
{
    public static class AppLogging
    {
        private static readonly ILoggerFactory LoggerFactory =
            Microsoft.Extensions.Logging.LoggerFactory.Create(builder =>
                builder.AddConsole(options => options.FormatterName = PlainConsoleFormatter.FormatterName)
                       .AddConsoleFormatter<PlainConsoleFormatter, ConsoleFormatterOptions>());

        public static ILogger<T> CreateLogger<T>() => LoggerFactory.CreateLogger<T>();

        public static ILogger Default { get; } = LoggerFactory.CreateLogger("VNTextPatch");
    }

    public sealed class PlainConsoleFormatter : ConsoleFormatter
    {
        public const string FormatterName = "MessageOnly";
        public PlainConsoleFormatter() : base(FormatterName) { }

        public override void Write<TState>(
            in LogEntry<TState> logEntry,
            IExternalScopeProvider? scopeProvider,
            TextWriter textWriter)
        {
            string? message = logEntry.Formatter?.Invoke(logEntry.State, logEntry.Exception);
            if (message is null && logEntry.Exception is null)
                return;

            string level = logEntry.LogLevel switch
            {
                LogLevel.Trace => "trce",
                LogLevel.Debug => "dbug",
                LogLevel.Information => "info",
                LogLevel.Warning => "warn",
                LogLevel.Error => "fail",
                LogLevel.Critical => "crit",
                _ => "none"
            };

            textWriter.Write(level);
            textWriter.Write(": ");
            textWriter.WriteLine(message);

            if (logEntry.Exception is not null)
                textWriter.WriteLine(logEntry.Exception);
        }
    }

}
