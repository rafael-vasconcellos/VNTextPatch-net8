// MonospaceWordWrapper.cs
using VNTextPatch.Shared.Util;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Configuration;


namespace VNTextPatch.Shared.Util
{
    internal class MonospaceWordWrapper : WordWrapper
    {
        public static readonly MonospaceWordWrapper Default = new MonospaceWordWrapper();

        [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "T is always an int; primitive type conversion, without reflection on members.")]
        private MonospaceWordWrapper()
            : this(AppSettings.Configuration.GetValue<int>("MonospaceCharactersPerLine", 60))
        {
        }

        public MonospaceWordWrapper(int charactersPerLine)
        {
            LineWidth = charactersPerLine;
        }

        protected override int GetTextWidth(string text, int offset, int length)
        {
            return length;
        }

        protected override int LineWidth
        {
            get;
        }
    }
}
