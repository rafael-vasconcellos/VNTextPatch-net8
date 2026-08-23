using Microsoft.Extensions.Configuration;

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
                var name = AppSettings.Configuration[fontName] ?? "Franklin Gothic Book";
                var size = GetInt(fontSize, 40);
                var bold = GetBool(fontBold, false);
                var width = GetInt(
                    lineWidth,
                    lineWidth.Contains("Secondary") ? 670 : 1000
                );

#if WINDOWS
                return new WindowsTextMeasurer(name, size, bold, width);
#else
                return new SkiaTextMeasurer(name, 0, bold, width);
#endif
            }
            catch (Exception e)
            {
                Console.Write(e);
                throw; // Preserva a stack trace
            }

        }

        private static int GetInt(string key, int defaultValue)
        {
            return int.TryParse(AppSettings.Configuration[key], out var value)
                ? value
                : defaultValue;
        }

        private static bool GetBool(string key, bool defaultValue)
        {
            return bool.TryParse(AppSettings.Configuration[key], out var value)
                ? value
                : defaultValue;
        }

    }
}