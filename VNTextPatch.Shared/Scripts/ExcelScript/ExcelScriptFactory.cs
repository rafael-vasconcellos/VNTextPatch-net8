namespace VNTextPatch.Shared.Scripts
{
    public interface IScriptDisposableCollection : IScriptCollection, IDisposable {}

    public interface IEmptyExtractionCleanup
    {
        void CleanupEmptyExtraction();
    }

    public class ExcelScriptCollectionFactory
    {
        public static IScriptDisposableCollection Build(string filePath)
        {
            return new ExcelScriptCollection(filePath);
        }
    }
}
