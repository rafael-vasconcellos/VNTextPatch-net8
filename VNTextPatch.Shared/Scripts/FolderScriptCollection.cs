using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using VNTextPatch.Shared.Scripts.AdvHd;
using VNTextPatch.Shared.Scripts.ArcGameEngine;
using VNTextPatch.Shared.Scripts.Artemis;
using VNTextPatch.Shared.Scripts.Ethornell;
using VNTextPatch.Shared.Scripts.Kirikiri;
using VNTextPatch.Shared.Scripts.Majiro;
using VNTextPatch.Shared.Scripts.Mware;
using VNTextPatch.Shared.Scripts.Propeller;
using VNTextPatch.Shared.Scripts.RealLive;
using VNTextPatch.Shared.Scripts.ShSystem;
using VNTextPatch.Shared.Scripts.Silkys;
using VNTextPatch.Shared.Scripts.Softpal;
using VNTextPatch.Shared.Scripts.SystemNnn;
using VNTextPatch.Shared.Scripts.TmrHiroAdvSystem;
using VNTextPatch.Shared.Scripts.Yuris;

namespace VNTextPatch.Shared.Scripts
{
    public class FolderScriptCollection : IScriptCollection
    {
        private static readonly IScript[] TemporaryScripts;

        static FolderScriptCollection()
        {
            TemporaryScripts =
                new IScript[]
                {
                    new AdvHdScript(),
                    new ArtemisAsbScript(),
                    new ArtemisAstScript(),
                    new ArtemisTxtScript(),
                    new AgeScript(),
                    new CatSystemScript(),
                    new CSystemScript(),
                    new EthornellScript(),
                    new JsonScript(),
                    new JsonlScript(),
                    new KaguyaScript(),
                    new KirikiriKsScript(),
                    //new KirikiriScnScript(),
                    new KirikiriSocScript(),
                    new KirikiriTjsScript(),
                    new MajiroScript(),
                    new MusicaScript(),
                    new MwareScript(),
                    new PropellerScript(),
                    new QlieScript(),
                    new RealLiveScript(),
                    new RenpyScript(),
                    new ShSystemScript(),
                    new SilkysMapScript(),
                    new SilkysMesScript(),
                    new SoftpalScript(),
                    new SystemNnnDevScript(),
                    new SystemNnnReleaseScript(),
                    new TmrHiroAdvSystemCodeScript(),
                    new TmrHiroAdvSystemTextScript(),
                    new WhaleScript(),
                    new YurisScript(),
                    new YurisCompilerDefinitionScript()
                };
        }

        public FolderScriptCollection(string folderPath, string? extension, string? format = null)
        {
            if (!Directory.Exists(folderPath))
                throw new DirectoryNotFoundException($"{folderPath} does not exist");

            FolderPath = folderPath;
            Extension = extension ?? string.Empty;
            Format = format;
        }

        public static FolderScriptCollection Create(
            string folderPath, string? format = null)
        {
            string? firstFilePath = Directory.EnumerateFiles(folderPath, "*", SearchOption.AllDirectories)
                .FirstOrDefault(f => IsSupportedExtension(Path.GetExtension(f)));
            string extension = firstFilePath != null? 
                Path.GetExtension(firstFilePath).ToLower() :
                "." + GlobalVariables.OutputFormat;

            return Create(folderPath, extension, format);
        }

        /// <summary>
        /// Creates the appropriate folder collection for a directory.
        /// Specialized collections are selected here so callers do not need
        /// to know about engine-specific collection implementations.
        /// </summary>
        public static FolderScriptCollection Create(
            string folderPath, string? extension, string? format = null)
        {
            /* if (YurisFolderScriptCollection.IsApplicable(folderPath, format) 
                && Path.GetRelativePath(Directory.GetCurrentDirectory(), folderPath)
                    .EndsWith("yuris", StringComparison.OrdinalIgnoreCase) )
                return new YurisFolderScriptCollection(folderPath, format); */

            return new FolderScriptCollection(folderPath, extension, format);
        }

        /// <summary>
        /// Creates a folder collection for a single file path. YU-RIS detection
        /// is limited to .ybn files so unrelated files in the same game folder
        /// are not treated as compiled YU-RIS scripts.
        /// </summary>
        public static FolderScriptCollection CreateForFile(
            string filePath, string? format = null)
        {
            string folderPath = Path.GetDirectoryName(filePath) ??
                throw new ArgumentException($"Could not determine folder for {filePath}.", nameof(filePath));
            string extension = Path.GetExtension(filePath);

            if (extension.Equals(".ybn", StringComparison.OrdinalIgnoreCase) &&
                YurisFolderScriptCollection.IsApplicable(folderPath, format))
                return new YurisFolderScriptCollection(folderPath, format);

            return new FolderScriptCollection(folderPath, extension, format);
        }

        /// <summary>
        /// Creates a sibling collection for an output directory. Specialized
        /// collections override this to preserve their own indexing metadata.
        /// </summary>
        public virtual FolderScriptCollection CreateSibling(string folderPath)
        {
            Directory.CreateDirectory(folderPath);
            return new FolderScriptCollection(folderPath, Extension, Format);
        }

        /// <summary>
        /// Copies files that are not recognized as script formats into the
        /// project directory, preserving their relative paths. Existing files
        /// are not overwritten so repeated extraction keeps manual edits.
        /// </summary>
        protected virtual void ExportProjectFiles(string destinationDirectory)
        {
            if (string.IsNullOrWhiteSpace(destinationDirectory))
                throw new ArgumentException(
                    "A project output directory is required.", nameof(destinationDirectory));

            string sourceRoot = Path.GetFullPath(FolderPath);
            string destinationRoot = Path.GetFullPath(destinationDirectory);

            StringComparison pathComparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;

            if (sourceRoot.Equals(destinationRoot, pathComparison)) return;

            // Snapshot before creating/copying anything. This also prevents
            // newly copied files from being considered as source files when
            // the project output is nested inside the input directory.
            string[] sourceFiles = Directory
                .EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories)
                .ToArray();

            bool destinationIsInsideSource =
                IsSameOrWithinDirectory(destinationRoot, sourceRoot, pathComparison);

            Directory.CreateDirectory(destinationRoot);

            foreach (string sourceFile in sourceFiles)
            {
                string fullSourcePath = Path.GetFullPath(sourceFile);

                if (destinationIsInsideSource &&
                    IsSameOrWithinDirectory(fullSourcePath, destinationRoot, pathComparison))
                    continue;

                if (IsSupportedExtension(Path.GetExtension(fullSourcePath))) continue;

                string relativePath = Path.GetRelativePath(sourceRoot, fullSourcePath);
                if (Path.IsPathRooted(relativePath) ||
                    relativePath == ".." ||
                    relativePath.StartsWith(
                        ".." + Path.DirectorySeparatorChar,
                        pathComparison))
                    throw new InvalidDataException(
                        $"Source file escapes the collection directory: {sourceFile}");

                string destinationFile = Path.GetFullPath(
                    Path.Combine(destinationRoot, relativePath));

                if (!IsSameOrWithinDirectory(
                    destinationFile, destinationRoot, pathComparison))
                {
                    throw new InvalidDataException(
                        $"Project file path escapes the destination directory: {relativePath}");
                }

                CopyIfMissing(fullSourcePath, destinationFile);
            }
        }

        private static bool IsSameOrWithinDirectory(
            string path,
            string directory,
            StringComparison comparison)
        {
            string fullPath = Path.GetFullPath(path);
            string fullDirectory = Path.GetFullPath(directory);

            if (fullPath.Equals(fullDirectory, comparison))
                return true;

            string directoryPrefix = Path.EndsInDirectorySeparator(fullDirectory)
                ? fullDirectory
                : fullDirectory + Path.DirectorySeparatorChar;

            return fullPath.StartsWith(directoryPrefix, comparison);
        }

        private static void CopyIfMissing(string sourcePath, string destinationPath)
        {
            if (File.Exists(destinationPath))
                return;

            string? destinationDirectory = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrEmpty(destinationDirectory))
                Directory.CreateDirectory(destinationDirectory);

            File.Copy(sourcePath, destinationPath);
        }

        /// <summary>
        /// Resolves a script name to its physical file path.
        /// </summary>
        public virtual string GetFilePath(string scriptName)
        {
            return Path.Combine(
                FolderPath,
                scriptName.Replace('/', Path.DirectorySeparatorChar)
                          .Replace('\\', Path.DirectorySeparatorChar));
        }

        public IScript GetTemporaryScript()
        {
            IScript? script;
            if (Format != null)
            {
                string typeName = Format + "Script";
                script = TemporaryScripts.FirstOrDefault(f => f.GetType().Name.Equals(typeName, StringComparison.InvariantCultureIgnoreCase));
                if (script == null)
                    throw new NotSupportedException($"Format {Format} is not supported");
            }
            else
            {
                script = TemporaryScripts.FirstOrDefault(f => !string.IsNullOrEmpty(f.Extension) && f.Extension.Equals(Extension, StringComparison.InvariantCultureIgnoreCase));
                if (script == null)
                    throw new NotSupportedException($"Extension {Extension} is not supported");
            }
            return script;
        }

        public virtual IEnumerable<string> Scripts
        {
            get
            {
                return Directory.EnumerateFiles(FolderPath, "*" + Extension, SearchOption.AllDirectories)
                    .Select(f => Path.GetRelativePath(Name, f))
                    .Where(f => IsSupportedExtension(Path.GetExtension(f)));
            }
        }

        public bool Exists(string scriptName)
        {
            try
            {
                return File.Exists(GetFilePath(scriptName));
            }
            catch (FileNotFoundException)
            {
                return false;
            }
            catch (InvalidDataException)
            {
                return false;
            }
        }

        public void Add(string scriptName)
        {
            string filePath = GetFilePath(scriptName);
            var directoryName = Path.GetDirectoryName(filePath) ??
                throw new Exception($"Couldn't find directory path from {filePath}");
            Directory.CreateDirectory(directoryName);
            File.Create(filePath).Close();
        }

        public void Add(string scriptName, ScriptLocation copyFrom)
        {
            string sourceFilePath = copyFrom.ToFilePath();
            string destFilePath = GetFilePath(scriptName);
            var destDirectoryName = Path.GetDirectoryName(destFilePath) ??
                throw new Exception($"Couldn't find directory path from {destFilePath}");
            Directory.CreateDirectory(destDirectoryName);
            File.Copy(sourceFilePath, destFilePath, true);
        }

        public override string ToString()
        {
            return FolderPath;
        }

        public string Name
        {
            get { return FolderPath; }
        }

        public string FolderPath
        {
            get;
        }

        public string Extension
        {
            get;
        }

        public string? Format
        {
            get;
        }

        private static bool IsSupportedFile(string path)
        {
            var extension = Path.GetExtension(path);
            return IsSupportedExtension(extension);
        }

        private static bool IsSupportedExtension(string extension)
        {
            var script = TemporaryScripts.FirstOrDefault(f => !string.IsNullOrEmpty(f.Extension) && f.Extension.Equals(extension, StringComparison.InvariantCultureIgnoreCase));
            if (script == null) return false;
            return true;
        }
    }
}
