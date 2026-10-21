namespace VNTextPatch.Archives
{
    public interface IArchive : IDisposable
    {
        static abstract IArchive Load(string filePath, string? encriptionKey);
        string? Extension { get; }
        Dictionary<string, object> ListFiles(bool recursive);
        void Extract(string outputPath, string? pathToExtract);
        static abstract void Pack(string pathToPack, string outputPath);
    }
}