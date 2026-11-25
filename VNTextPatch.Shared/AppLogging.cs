using Microsoft.Extensions.Logging;

namespace VNTextPatch.Shared
{
    public static class AppLogging
    {
        private static readonly ILoggerFactory LoggerFactory =
            Microsoft.Extensions.Logging.LoggerFactory.Create(builder => builder.AddConsole());

        public static ILogger<T> CreateLogger<T>() => LoggerFactory.CreateLogger<T>();
    }
}
