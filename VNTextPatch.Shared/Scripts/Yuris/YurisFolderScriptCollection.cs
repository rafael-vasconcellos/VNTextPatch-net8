using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using VNTextPatch.Shared;
using VNTextPatch.Shared.Scripts;

namespace VNTextPatch.Shared.Scripts.Yuris
{
    /// <summary>
    /// Folder collection for YU-RIS games. The YSTL index maps logical script
    /// paths to physical ystXXXXX.ybn files; callers only see the logical paths.
    /// </summary>
    public sealed class YurisFolderScriptCollection : FolderScriptCollection
    {
        private const string IndexFileName = "yst_list.ybn";

        private readonly YurisScriptList _scriptList;
        public YurisEncoding TextEncoding { get; }
        private readonly Dictionary<string, YurisScriptListEntry> _entries;

        public static bool IsApplicable(string folderPath, string? format = null)
        {
            if (!Directory.Exists(folderPath) ||
                !File.Exists(Path.Combine(folderPath, IndexFileName)))
                return false;

            // An explicit non-YU-RIS format selection takes precedence over
            // automatic format detection.
            return string.IsNullOrEmpty(format) ||
                   format.Equals("Yuris", StringComparison.OrdinalIgnoreCase);
        }

        public YurisFolderScriptCollection(string folderPath, string? format = null)
            : base(folderPath, ".ybn", format)
        {
            TextEncoding = new YurisEncoding(folderPath);
            _scriptList = new YurisScriptList();

            // Load the index through a plain collection: resolving the index
            // itself must not recurse through the YSTL logical-path mapping.
            var indexCollection = new FolderScriptCollection(folderPath, ".ybn", format);
            _scriptList.Load(new ScriptLocation(indexCollection, IndexFileName));

            _entries = new Dictionary<string, YurisScriptListEntry>(
                OperatingSystem.IsWindows()
                    ? StringComparer.OrdinalIgnoreCase
                    : StringComparer.Ordinal);

            foreach (YurisScriptListEntry entry in _scriptList.Entries)
            {
                string key = NormalizeScriptName(entry.ScriptPath);
                if (key.Length == 0)
                    throw new InvalidDataException("YSTL contains an empty script path.");

                if (!_entries.TryAdd(key, entry))
                    throw new InvalidDataException(
                        $"YSTL contains a duplicate script path: {entry.ScriptPath}");
            }
        }

        public override IEnumerable<string> Scripts
        {
            get
            {
                IEnumerable<string> indexedScripts =
                    _scriptList.Entries
                        .Where(entry => entry.HasCompiledFile)
                        .Select(entry => entry.ScriptPath);

                // YSTL lists compiled scenario scripts, not the engine's
                // auxiliary metadata files. Include those separately so that
                // extraction can inspect them and insertion can copy them
                // unchanged into the patched game directory.
                IEnumerable<string> metadataFiles =
                    Directory.EnumerateFiles(FolderPath, "*.ybn", SearchOption.TopDirectoryOnly)
                        .Where(IsAuxiliaryMetadataFile)
                        .Select(Path.GetFileName)
                        .Where(name => !string.IsNullOrEmpty(name))
                        .Select(name => name!);

                StringComparer comparer = OperatingSystem.IsWindows()
                    ? StringComparer.OrdinalIgnoreCase
                    : StringComparer.Ordinal;

                return indexedScripts.Concat(metadataFiles).Distinct(comparer);
            }
        }

        private static bool IsAuxiliaryMetadataFile(string filePath)
        {
            string fileName = Path.GetFileName(filePath);

            // The index is copied explicitly by CreateSibling. Compiled
            // scripts are represented by their logical YSTL paths.
            if (fileName.Equals(IndexFileName, StringComparison.OrdinalIgnoreCase) ||
                IsCompiledScriptFileName(fileName))
                return false;

            Span<byte> magic = stackalloc byte[4];
            using FileStream stream = File.OpenRead(filePath);
            if (stream.Read(magic) != magic.Length)
                return false;

            return magic.SequenceEqual("YSVR"u8) ||
                   magic.SequenceEqual("YSCM"u8) ||
                   magic.SequenceEqual("YSCF"u8) ||
                   magic.SequenceEqual("YSER"u8) ||
                   magic.SequenceEqual("YSLB"u8) ||
                   magic.SequenceEqual("YSTD"u8);
        }

        public override string GetFilePath(string scriptName)
        {
            // Support explicit selection of a physical compiled script as well
            // as the logical paths returned by Scripts.
            string fileName = Path.GetFileName(scriptName);
            if (fileName.Equals(IndexFileName, StringComparison.OrdinalIgnoreCase))
                return Path.Combine(FolderPath, IndexFileName);

            if (IsCompiledScriptFileName(fileName))
                return Path.Combine(FolderPath, fileName);

            string key = NormalizeScriptName(scriptName);
            if (_entries.TryGetValue(key, out YurisScriptListEntry? entry))
                return Path.Combine(FolderPath, entry.GetCompiledFileName());

            // Other YU-RIS resources (ysv.ybn, ysc.ybn, yscfg.ybn, yse.ybn,
            // ysl.ybn, yst.ybn, etc.) are stored by their own names and are
            // not entries in YSTL. Known resource names must also resolve when
            // the destination file has not been created yet (Inserter.Add).
            string directPath = base.GetFilePath(scriptName);
            if (IsKnownMetadataResourceFile(fileName) || File.Exists(directPath))
                return directPath;

            throw new FileNotFoundException(
                $"Script '{scriptName}' is neither listed in {IndexFileName} nor a known metadata resource.");
        }

        public override FolderScriptCollection CreateSibling(string folderPath)
        {
            Directory.CreateDirectory(folderPath);

            string sourceIndex = Path.GetFullPath(Path.Combine(FolderPath, IndexFileName));
            string destinationIndex = Path.GetFullPath(Path.Combine(folderPath, IndexFileName));

            StringComparison pathComparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;

            if (!sourceIndex.Equals(destinationIndex, pathComparison))
                File.Copy(sourceIndex, destinationIndex, true);

            return new YurisFolderScriptCollection(folderPath, Format);
        }

        /// <summary>
        /// Exports the compiled scenario scripts as a logical script project,
        /// following the directory layout used by RxYuris' YSTL MakeStructure:
        /// script/{logical path} contains the original YSTB bytes with the
        /// logical .yst name, while ysbin contains the index and command table.
        /// Existing destination files are preserved to avoid overwriting edits.
        /// </summary>
        public override void ExportProjectFiles(string destinationDirectory)
        {
            base.ExportProjectFiles(destinationDirectory);

            if (string.IsNullOrWhiteSpace(destinationDirectory))
                throw new ArgumentException(
                    "A project output directory is required.",
                    nameof(destinationDirectory));

            string projectRoot = Path.GetFullPath(destinationDirectory);
            string scriptRoot = Path.Combine(projectRoot, "script");
            string binaryRoot = Path.Combine(projectRoot, "ysbin");

            Directory.CreateDirectory(scriptRoot);
            Directory.CreateDirectory(binaryRoot);

            CopyIfMissing(
                Path.Combine(FolderPath, IndexFileName),
                Path.Combine(binaryRoot, IndexFileName));

            // YSCM command definitions are useful when inspecting or editing
            // command bytecode. Do not copy unrelated metadata such as YSVR.
            string commandListPath = Path.Combine(FolderPath, "ysc.ybn");
            if (File.Exists(commandListPath))
            {
                CopyIfMissing(
                    commandListPath,
                    Path.Combine(binaryRoot, "ysc.ybn"));
            }

            foreach (YurisScriptListEntry entry in _scriptList.Entries)
            {
                // Some index entries intentionally have no corresponding
                // compiled YSTB file (VariableCount < 0).
                if (!entry.HasCompiledFile)
                    continue;

                string relativePath = NormalizeScriptName(entry.ScriptPath);
                string sourcePath = GetFilePath(relativePath);
                string destinationPath = Path.GetFullPath(Path.Combine(
                    scriptRoot,
                    relativePath.Replace('/', Path.DirectorySeparatorChar)));

                string relativeToRoot = Path.GetRelativePath(scriptRoot, destinationPath);
                if (Path.IsPathRooted(relativeToRoot) ||
                    relativeToRoot == ".." ||
                    relativeToRoot.StartsWith(
                        ".." + Path.DirectorySeparatorChar,
                        StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        $"YSTL script path escapes the project output directory: {entry.ScriptPath}");
                }

                CopyIfMissing(sourcePath, destinationPath);
            }
        }

        private static void CopyIfMissing(string sourcePath, string destinationPath)
        {
            if (!File.Exists(sourcePath))
                throw new FileNotFoundException(
                    "A YU-RIS project file referenced by the script index was not found.",
                    sourcePath);

            string fullSourcePath = Path.GetFullPath(sourcePath);
            string fullDestinationPath = Path.GetFullPath(destinationPath);

            StringComparison comparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;

            if (fullSourcePath.Equals(fullDestinationPath, comparison) ||
                File.Exists(fullDestinationPath))
                return;

            string? directory = Path.GetDirectoryName(fullDestinationPath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            File.Copy(fullSourcePath, fullDestinationPath);
        }

        private static bool IsKnownMetadataResourceFile(string fileName)
        {
            return fileName.Equals("ysv.ybn", StringComparison.OrdinalIgnoreCase) ||
                   fileName.Equals("ysc.ybn", StringComparison.OrdinalIgnoreCase) ||
                   fileName.Equals("yscfg.ybn", StringComparison.OrdinalIgnoreCase) ||
                   fileName.Equals("yse.ybn", StringComparison.OrdinalIgnoreCase) ||
                   fileName.Equals("ysl.ybn", StringComparison.OrdinalIgnoreCase) ||
                   fileName.Equals("yst.ybn", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsCompiledScriptFileName(string fileName)
        {
            if (fileName.Length != 12 ||
                !fileName.StartsWith("yst", StringComparison.OrdinalIgnoreCase) ||
                !fileName.EndsWith(".ybn", StringComparison.OrdinalIgnoreCase))
                return false;

            for (int i = 3; i < 8; i++)
            {
                if (fileName[i] < '0' || fileName[i] > '9')
                    return false;
            }

            return true;
        }

        private static string NormalizeScriptName(string scriptName)
        {
            if (string.IsNullOrWhiteSpace(scriptName))
                throw new InvalidDataException("YSTL contains an empty script path.");

            string normalized = scriptName.Replace('\\', '/');
            if (normalized.StartsWith("/", StringComparison.Ordinal) ||
                (normalized.Length >= 2 &&
                 char.IsLetter(normalized[0]) &&
                 normalized[1] == ':'))
                throw new InvalidDataException(
                    $"YSTL script path must be relative: {scriptName}");

            string[] segments = normalized.Split('/');
            if (segments.Any(segment => segment == ".."))
                throw new InvalidDataException(
                    $"YSTL script path escapes the script folder: {scriptName}");

            return string.Join(
                "/",
                segments.Where(segment => segment.Length > 0 && segment != "."));
        }
    }
}
