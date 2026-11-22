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

        /// <summary>
        /// Creates the appropriate folder collection for a directory.
        /// Specialized collections are selected here so callers do not need
        /// to know about engine-specific collection implementations.
        /// </summary>
        public static FolderScriptCollection Create(
            string folderPath, string? extension, string? format = null)
        {
            if (YurisFolderScriptCollection.IsApplicable(folderPath, format) 
                && Path.GetRelativePath(Directory.GetCurrentDirectory(), folderPath)
                    .EndsWith("yuris", StringComparison.OrdinalIgnoreCase) )
                return new YurisFolderScriptCollection(folderPath, format);

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
                int folderPathLength = FolderPath.Length;
                if (!Name.EndsWith("\\"))
                    folderPathLength++;

                return Directory.EnumerateFiles(Name, "*" + Extension, SearchOption.AllDirectories)
                                .Select(f => f.Substring(folderPathLength));
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
    }
}
