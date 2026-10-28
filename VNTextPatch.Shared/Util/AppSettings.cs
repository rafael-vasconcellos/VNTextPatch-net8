// AppSettings.cs
using System.IO;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Configuration;
using System.Xml.Linq;


namespace VNTextPatch.Shared.Util
{
    public static class AppSettings
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
        
        public static void CreateFile(string dirName)
        {
            if (Exists()) return;
            var document = new XDocument(
                new XDeclaration("1.0", "utf-8", null),
                new XElement("settings",
                    new XComment(" When using an API key to access Google spreadsheets "),
                    new XElement("GoogleApiKey"),

                    new XComment(
                        " Instead of using an API key, you can also use a service account.\n" +
                        "       This way, you don't have to make your spreadsheet public. "
                    ),
                    new XComment(
                        " To use a service account, download its private key\n" +
                        "       and store it in a file called \"google-service-account.json\". "
                    ),

                    new XElement(
                        "MonospaceCharactersPerLine",
                        DefaultAppSettings.MonospaceCharactersPerLine
                    ),

                    new XElement(
                        "ProportionalFontName",
                        DefaultAppSettings.FontName
                    ),
                    new XElement(
                        "ProportionalFontSize",
                        DefaultAppSettings.FontSize
                    ),
                    new XElement(
                        "ProportionalFontBold",
                        DefaultAppSettings.FontBold.ToString().ToLowerInvariant()
                    ),
                    new XElement(
                        "ProportionalLineWidth",
                        DefaultAppSettings.ProportionalLineWidth
                    ),
                    new XElement(
                        "SecondaryProportionalLineWidth",
                        DefaultAppSettings.SecondaryProportionalLineWidth
                    )
                )
            );

            var directory = Path.GetFullPath(dirName);
            if (directory is not null)
            {
                Directory.CreateDirectory(directory);
                document.Save(Path.Combine(directory, "settings.xml"));
            }
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
