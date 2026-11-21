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

        private YurisScriptList? _yurisScriptList;
        private Dictionary<string, YurisScriptListEntry>? _yurisScriptEntries;

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
                    new YurisScript()
                };
        }

        public FolderScriptCollection(
            string folderPath,
            string? extension,
            string? format = null,
            YurisScriptList? yurisScriptList = null)
        {
            if (!Directory.Exists(folderPath))
                throw new DirectoryNotFoundException($"{folderPath} does not exist");

            FolderPath = folderPath;
            Extension = extension ?? string.Empty;
            Format = format;

            if (yurisScriptList != null)
                SetYurisScriptIndex(yurisScriptList);
        }

        public void SetYurisScriptIndex(YurisScriptList scriptList)
        {
            if (!Extension.Equals(".ybn", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("A YSTL index can only be used with a .ybn script collection.");

            var entries = new Dictionary<string, YurisScriptListEntry>(
                OperatingSystem.IsWindows()
                    ? StringComparer.OrdinalIgnoreCase
                    : StringComparer.Ordinal);

            foreach (YurisScriptListEntry entry in scriptList.Entries)
            {
                string key = NormalizeScriptName(entry.ScriptPath);
                if (key.Length == 0)
                    throw new InvalidDataException("YSTL contains an empty script path.");

                if (!entries.TryAdd(key, entry))
                    throw new InvalidDataException($"YSTL contains a duplicate script path: {entry.ScriptPath}");
            }

            _yurisScriptList = scriptList;
            _yurisScriptEntries = entries;
        }

        /// <summary>
        /// Creates another folder collection with the same file format and
        /// YSTL mapping. Used by insertion so patched files retain their
        /// physical ystXXXXX.ybn names.
        /// </summary>
        public FolderScriptCollection CreateSibling(string folderPath)
        {
            var sibling = new FolderScriptCollection(folderPath, Extension, Format, _yurisScriptList);

            // The index is required by the game to resolve logical script names.
            // It is not part of the script-entry enumeration, so copy it explicitly.
            if (_yurisScriptList != null)
            {
                string sourceIndex = Path.Combine(FolderPath, "yst_list.ybn");
                string destinationIndex = Path.Combine(folderPath, "yst_list.ybn");

                if (File.Exists(sourceIndex))
                {
                    Directory.CreateDirectory(folderPath);
                    File.Copy(sourceIndex, destinationIndex, true);
                }
            }

            return sibling;
        }

        /// <summary>
        /// Resolves a logical script path from YSTL to its compiled file.
        /// Without an index, this is equivalent to Path.Combine(FolderPath, scriptName).
        /// </summary>
        public string GetFilePath(string scriptName)
        {
            string relativePath = scriptName;

            if (_yurisScriptEntries != null)
            {
                string key = NormalizeScriptName(scriptName);
                if (!_yurisScriptEntries.TryGetValue(key, out YurisScriptListEntry? entry))
                    throw new FileNotFoundException($"Script '{scriptName}' is not listed in yst_list.ybn.");

                relativePath = entry.GetCompiledFileName();
            }

            return Path.Combine(
                FolderPath,
                relativePath.Replace('/', Path.DirectorySeparatorChar)
                            .Replace('\\', Path.DirectorySeparatorChar));
        }

        private static string NormalizeScriptName(string scriptName)
        {
            if (string.IsNullOrWhiteSpace(scriptName))
                throw new InvalidDataException("YSTL contains an empty script path.");

            string normalized = scriptName.Replace('\\', '/');
            if (normalized.StartsWith("/", StringComparison.Ordinal) ||
                (normalized.Length >= 2 && char.IsLetter(normalized[0]) && normalized[1] == ':'))
                throw new InvalidDataException($"YSTL script path must be relative: {scriptName}");

            string[] segments = normalized.Split('/');
            if (segments.Any(segment => segment == ".."))
                throw new InvalidDataException($"YSTL script path escapes the script folder: {scriptName}");

            return string.Join("/", segments.Where(segment => segment.Length > 0 && segment != "."));
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

        public IEnumerable<string> Scripts
        {
            get
            {
                if (_yurisScriptList != null)
                    return _yurisScriptList.Entries.Select(entry => entry.ScriptPath);

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
