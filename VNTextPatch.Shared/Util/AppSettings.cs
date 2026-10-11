// AppSettings.cs
using System.IO;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Configuration;


namespace VNTextPatch.Shared.Util
{
    internal static class AppSettings
    {
        public static readonly IConfiguration Configuration = Build();

        [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "settings.xml doesn't use EncryptedXml/XSLT.")]
        [UnconditionalSuppressMessage("AOT", "IL3050", Justification = "settings.xml doesn't use EncryptedXml/XSLT.")]
        public static IConfiguration Build()
        {
            try
            {
                return new ConfigurationBuilder()
                    .SetBasePath(BasePath())
                    .AddXmlFile("settings.xml", optional: true, reloadOnChange: true)
                    .Build();

            }
            catch (Exception e)
            {
                Console.Write(e);
                throw; // Preserva a stack trace
            }

        }

        public static string BasePath()
        {
            string currentDirectory = Directory.GetCurrentDirectory();

            try
            {
                return Directory
                    .EnumerateFiles(currentDirectory, "settings.xml", SearchOption.AllDirectories)
                    .Select(Path.GetDirectoryName)
                    .FirstOrDefault() ?? currentDirectory;

            } catch
            {
                return currentDirectory;
            }
        }

    }
}
