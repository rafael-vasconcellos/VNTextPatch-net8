using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using VNTextPatch.Shared.Patching.Yuris;
using VNTextPatch.Shared.Scripts;
using VNTextPatch.Shared.Util;

namespace VNTextPatch.Shared.Scripts.Yuris
{
    /// <summary>
    /// Selects the text encoding used by YU-RIS resources in one game directory
    /// and coordinates patching supported YU-RIS executables in the output game.
    /// Original game data is always read as Shift-JIS. When the game directory
    /// contains a recognized YU-RIS executable, translated strings are written
    /// as GBK; otherwise the normal Shift-JIS tunnel encoding is used.
    /// </summary>
    public sealed class YurisEncoding
    {
        private static readonly Lazy<Encoding> GbkEncoding = new(CreateGbkEncoding);

        private readonly string[] _supportedExecutables = [];
        private readonly object _patchLock = new();
        private readonly HashSet<string> _patchedOutputDirectories = new HashSet<string>(
            OperatingSystem.IsWindows()
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal
        );

        /// <summary>
        /// Default profile for parsers used directly on byte arrays, without a
        /// game directory. It preserves the usual Shift-JIS tunnel behavior.
        /// </summary>
        public static YurisEncoding Default { get; } = new();

        /// <summary>
        /// Directory used to detect the target game's executable, or null when
        /// this instance is the default profile.
        /// </summary>
        public string? GameDirectory { get; }

        /// <summary>
        /// Original YU-RIS resources are encoded in Shift-JIS, regardless of
        /// the selected output encoding.
        /// </summary>
        public Encoding ReadEncoding => StringUtil.SjisEncoding;

        /// <summary>
        /// Encoding used when serializing translated text into YU-RIS resources.
        /// </summary>
        public Encoding WriteEncoding { get; }

        public bool UsesGbk { get; }

        private YurisEncoding()
        {
            WriteEncoding = StringUtil.SjisTunnelEncoding;
        }

        public YurisEncoding(string gameDirectory)
        {
            if (string.IsNullOrWhiteSpace(gameDirectory))
                throw new ArgumentException("A game directory is required.", nameof(gameDirectory));

            string fullPath = Path.GetFullPath(gameDirectory);
            if (!Directory.Exists(fullPath))
                throw new DirectoryNotFoundException($"YU-RIS game directory was not found: {fullPath}");

            GameDirectory = fullPath;
            _supportedExecutables = Directory
                .EnumerateFiles(fullPath, "*", SearchOption.TopDirectoryOnly)
                .Where(path => Path.GetExtension(path).Equals(
                    ".exe", StringComparison.OrdinalIgnoreCase))
                .Where(YurisExecutablePatcher.IsSupportedExecutable)
                .ToArray();

            UsesGbk = _supportedExecutables.Length > 0;
            WriteEncoding = UsesGbk
                ? GbkEncoding.Value
                : StringUtil.SjisTunnelEncoding;
        }

        /// <summary>
        /// Gets the encoding profile associated with the input collection. The
        /// specialized YU-RIS collection caches the decision for the whole game.
        /// </summary>
        public static YurisEncoding FromLocation(ScriptLocation location)
        {
            if (location.Collection is YurisFolderScriptCollection yurisCollection)
                return yurisCollection.TextEncoding;

            if (location.Collection is FolderScriptCollection folderCollection)
                return new YurisEncoding(folderCollection.FolderPath);

            string? directory = Path.GetDirectoryName(location.ToFilePath());
            if (string.IsNullOrEmpty(directory))
                throw new InvalidOperationException(
                    "Could not determine the game directory for the YU-RIS resource.");

            return new YurisEncoding(directory);
        }

        /// <summary>
        /// Ensures that every supported YU-RIS executable from the input game
        /// directory is present in the output directory with its character
        /// range table patched for GBK. Repeated calls for the same output
        /// directory are no-ops.
        /// </summary>
        public void EnsureExecutablePatched(ScriptLocation outputLocation)
        {
            if (!UsesGbk)
                return;

            if (string.IsNullOrEmpty(GameDirectory))
                throw new InvalidOperationException(
                    "This YU-RIS encoding profile is not associated with a game directory.");

            string outputDirectory = GetCollectionDirectory(outputLocation);
            string fullOutputDirectory = Path.GetFullPath(outputDirectory);
            StringComparison pathComparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;

            if (fullOutputDirectory.Equals(GameDirectory, pathComparison))
            {
                throw new InvalidOperationException(
                    "YU-RIS executable patching requires a separate output directory; " +
                    "the original game executable will not be modified in place.");
            }

            lock (_patchLock)
            {
                if (_patchedOutputDirectories.Contains(fullOutputDirectory))
                    return;

                Directory.CreateDirectory(fullOutputDirectory);

                foreach (string sourceExecutable in _supportedExecutables)
                {
                    string outputExecutable = Path.Combine(
                        fullOutputDirectory,
                        Path.GetFileName(sourceExecutable));

                    if (!File.Exists(outputExecutable))
                    {
                        YurisExecutablePatcher.Patch(sourceExecutable, outputExecutable);
                        continue;
                    }

                    // Preserve an existing output executable (including user
                    // modifications) and patch that copy rather than replacing
                    // it with the original input executable.
                    if (!YurisExecutablePatcher.IsSupportedExecutable(outputExecutable))
                    {
                        throw new InvalidDataException(
                            $"The output executable '{outputExecutable}' is not a supported YU-RIS executable. " +
                            "It was left untouched.");
                    }

                    string temporaryPath = outputExecutable + "." +
                        Guid.NewGuid().ToString("N") + ".tmp";

                    try
                    {
                        YurisExecutablePatchResult result =
                            YurisExecutablePatcher.Patch(outputExecutable, temporaryPath);

                        if (result.Status == YurisExecutablePatchStatus.Patched)
                            File.Move(temporaryPath, outputExecutable, true);
                    }
                    finally
                    {
                        if (File.Exists(temporaryPath))
                            File.Delete(temporaryPath);
                    }
                }

                _patchedOutputDirectories.Add(fullOutputDirectory);
            }
        }

        private static string GetCollectionDirectory(ScriptLocation location)
        {
            if (location.Collection is FolderScriptCollection folderCollection)
                return folderCollection.FolderPath;

            string? directory = Path.GetDirectoryName(location.ToFilePath());
            if (string.IsNullOrEmpty(directory))
                throw new InvalidOperationException(
                    "Could not determine the output directory for the YU-RIS executable.");

            return directory;
        }

        /// <summary>
        /// Tests whether a byte begins a multibyte character in the selected
        /// output encoding. This is used when scanning serialized control codes.
        /// </summary>
        public bool IsWriteLeadByte(byte value)
        {
            return UsesGbk
                ? YurisCharacterRangeTable.IsGbkLeadByte(value)
                : YurisCharacterRangeTable.IsShiftJisLeadByte(value);
        }

        private static Encoding CreateGbkEncoding()
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

            // Fail rather than silently replace characters that GBK cannot encode.
            return Encoding.GetEncoding(
                936,
                EncoderFallback.ExceptionFallback,
                DecoderFallback.ExceptionFallback);
        }
    }
}
