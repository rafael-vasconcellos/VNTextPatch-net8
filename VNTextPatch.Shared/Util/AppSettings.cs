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
            return GetFilePath() ?? Directory.GetCurrentDirectory();
        }

        public static string? GetFilePath()
        {
            try
            {
                return Directory
                    .EnumerateFiles(Directory.GetCurrentDirectory(), "settings.xml", SearchOption.AllDirectories)
                    .Select(Path.GetDirectoryName)
                    .FirstOrDefault() ?? null;

            } catch
            {
                return null;
            }
        }

        public static bool Exists()
        {
            return !string.IsNullOrEmpty(GetFilePath());
        }

        public static void CreateFile(string filePath)
        {
            //
        }

    }

    internal class DefaultAppSettings
    {
        public static string FontName = "Franklin Gothic Book";
        public static int FontSize = 40;
        public static bool FontBold = false;
        public static int ProportionalLineWidth = 1000;
        public static int SecondaryProportionalLineWidth = 670;
        public static int MonospaceCharactersPerLine = 60;
    }
}
