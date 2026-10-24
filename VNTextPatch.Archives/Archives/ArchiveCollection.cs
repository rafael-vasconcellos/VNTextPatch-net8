namespace VNTextPatch.Archives
{
    public class ArchiveCollection
    {
        private static readonly IArchive[] Archives =
        {
            new KirikiriXp3(),
            new YurisYPF(),
            new GodotPck(),
            new GodotExe(),
            new Wolf()
        };

        public static IArchive GetArchive(string extension)
        {
            var archive = Archives.FirstOrDefault(f => !string.IsNullOrEmpty(f.Extension) && f.Extension.Equals(extension, StringComparison.InvariantCultureIgnoreCase));
            if (archive == null) throw new Exception("Unsuported archive file.");
            return archive;
        }
    }
}