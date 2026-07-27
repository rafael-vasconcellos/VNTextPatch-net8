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
                var size = AppSettings.Configuration.GetValue<int>(fontSize, 0);
                var bold = AppSettings.Configuration.GetValue<bool>(fontBold, false);
                var width = AppSettings.Configuration.GetValue<int>(lineWidth, lineWidth.Contains("Secondary") ? 670 : 1000);

#if WINDOWS
                return new WindowsTextMeasurer(name, size, bold, width);
#else
                return new SkiaTextMeasurer(name, size, bold, width);
#endif
            }
            catch (Exception e)
            {
                Console.Write(e);
                throw; // Preserva a stack trace
            }

        }
    }
}