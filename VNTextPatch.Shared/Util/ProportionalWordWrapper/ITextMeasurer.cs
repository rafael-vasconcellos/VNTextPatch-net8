using VNTextPatch.Shared.Util;


namespace VNTextPatch.Shared.Util
{
    internal abstract class ITextMeasurer: WordWrapper
    {
        public abstract void Dispose();
    }
}