using System.IO;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;


namespace VNTextPatch.Shared
{
    public static class AppLogging
    {
        private static readonly ILoggerFactory LoggerFactory =
            Microsoft.Extensions.Logging.LoggerFactory.Create(builder =>
            {
                builder.AddConsole(options => options.FormatterName = PlainConsoleFormatter.FormatterName);
                builder.Services.TryAddEnumerable(
                    ServiceDescriptor.Singleton<ConsoleFormatter, PlainConsoleFormatter>()
                );
            });

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

            (string level, string color) = logEntry.LogLevel switch
            {
                LogLevel.Trace => ("trce", "\u001b[37m"),
                LogLevel.Debug => ("dbug", "\u001b[37m"),
                LogLevel.Information => ("info", "\u001b[32m"),
                LogLevel.Warning => ("warn", "\u001b[33m"),
                LogLevel.Error => ("fail", "\u001b[30;41m"),
                LogLevel.Critical => ("crit", "\u001b[37;41m"),
                _ => ("none", "\u001b[39m")
            };

            textWriter.Write(color);
            textWriter.Write(level);
            if (logEntry.LogLevel is LogLevel.Information) textWriter.Write("\u001b[39m\u001b[49m");
            textWriter.Write(": ");
            textWriter.WriteLine(message);

            if (logEntry.Exception is not null)
                textWriter.WriteLine(logEntry.Exception);
            textWriter.Write("\u001b[39m\u001b[49m");
        }
    }

}
