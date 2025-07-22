using Microsoft.Extensions.Configuration;
using System.Runtime.InteropServices;
using VNTextPatch.Shared.Util;


namespace VNTextPatch.Shared.Util
{
    internal class ProportionalWordWrapper
    {
        public static readonly ITextMeasurer Default =
            Build("ProportionalFontName", "ProportionalFontSize", "ProportionalFontBold", "ProportionalLineWidth");

        public static readonly ITextMeasurer Secondary =
            Build("ProportionalFontName", "ProportionalFontSize", "ProportionalFontBold", "SecondaryProportionalLineWidth");

        public static ITextMeasurer Build(string fontName, string fontSize, string fontBold, string lineWidth)
        {
            try
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    return new WindowsTextMeasurer(
                        AppSettings.Configuration[fontName] ?? "Franklin Gothic Book",
                        AppSettings.Configuration.GetValue<int>(fontSize, 40),
                        AppSettings.Configuration.GetValue<bool>(fontBold, false),
                        AppSettings.Configuration.GetValue<int>(lineWidth, lineWidth.Contains("Secondary")? 670 : 1000)
                    );
                }
                else
                {
                    return new SkiaTextMeasurer(
                        AppSettings.Configuration[fontName] ?? "Franklin Gothic Book",
                        AppSettings.Configuration.GetValue<int>(fontSize, 40),
                        AppSettings.Configuration.GetValue<bool>(fontBold, false),
                        AppSettings.Configuration.GetValue<int>(lineWidth, lineWidth.Contains("Secondary")? 670 : 1000)
                    );
                }
            }
            catch (Exception e)
            {
                Console.Write(e);
                throw e;
            }

        }
    }
}