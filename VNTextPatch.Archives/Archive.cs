//ArchiveCollection
namespace VNTextPatch.Archives
{
    public class Archive
    {
        private IArchive _archive;
        public Archive(string filePath, string? encriptionKey)
        {
            _archive = ArchiveCollection.GetArchive(Path.GetExtension(filePath));
            _archive.Load(filePath, encriptionKey);
        }
        public void UnpackLocal(string outputPath, string? pathToExtract)
        {
            _archive.Extract(outputPath, pathToExtract);
        }
        public void ListFiles(bool recursive = false)
        {
            var files = _archive.ListFiles(recursive);
            foreach (string file in files)
            {
                Console.WriteLine(file);
            }
        }
        public static void PackLocal(string pathToPack, string outputPath, string? encriptionKey = null)
        {
            var archive = ArchiveCollection.GetArchive(Path.GetExtension(outputPath));
            archive.Pack(pathToPack, outputPath, encriptionKey);
        }
    }
}