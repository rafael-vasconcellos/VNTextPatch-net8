namespace VNTextPatch.Archives
{
    public interface IArchive : IDisposable
    {
        void Load(string filePath, string? encriptionKey);
        string? Extension { get; }
        Dictionary<string, object> ListFiles(bool recursive);
        void Extract(string outputPath, string? pathToExtract);
        void Pack(string pathToPack, string outputPath);
    }
}