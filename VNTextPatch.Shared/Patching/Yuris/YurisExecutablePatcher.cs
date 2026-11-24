using System;
using System.Buffers.Binary;
using System.IO;

namespace VNTextPatch.Shared.Patching.Yuris
{
    public enum YurisExecutablePatchStatus
    {
        Patched,
        AlreadyPatched
    }

    public readonly record struct YurisExecutablePatchResult(
        YurisExecutablePatchStatus Status,
        long TableOffset,
        string InputPath,
        string OutputPath);

    /// <summary>
    /// Replaces the YU-RIS Shift-JIS lead-byte classification table in a
    /// Windows PE executable with the corresponding GBK table.
    /// The input executable is never modified; the patched copy is written
    /// to the requested output path.
    /// </summary>
    public static class YurisExecutablePatcher
    {
        // YU-RIS code that fills the LOGFONT and calls CreateFontIndirectA. The engine creates its font
        // with lfCharSet = SHIFTJIS_CHARSET (0x80), so GDI decodes GBK bytes as CP932 when drawing text.
        // The 7-byte "mov ebx,[ecx*4+X]" is replaced with "mov ebx,edx" + "mov byte ptr [ebx+17],86"
        // + "nop" (lfCharSet = GB2312_CHARSET); edx already holds the same LOGFONT pointer at that point.
        private const int FontPatchOffset = 19;
        private const int FontTableOffsetA = 3;
        private const int FontTableOffsetB = 22;

        private static readonly byte?[] OriginalFontPattern = ParsePattern(
            "8B 14 95 ?? ?? ?? ?? 8B 44 24 20 89 02 8B 0D ?? ?? ?? ?? " +
            "8B 1C 8D ?? ?? ?? ?? C7 43 04 00 00 00 00");

        private static readonly byte?[] PatchedFontPattern = ParsePattern(
            "8B 14 95 ?? ?? ?? ?? 8B 44 24 20 89 02 8B 0D ?? ?? ?? ?? " +
            "8B DA C6 43 17 86 90 C7 43 04 00 00 00 00");

        private static readonly byte[] FontCharsetPatch = [0x8B, 0xDA, 0xC6, 0x43, 0x17, 0x86, 0x90];

        /// <summary>
        /// Returns true when the file is a PE executable containing exactly one
        /// recognized YU-RIS character-range table (Shift-JIS or GBK).
        /// Unrelated executables and unsupported YU-RIS variants return false.
        /// </summary>
        public static bool IsSupportedExecutable(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return false;

            if (!File.Exists(path))
                return false;

            byte[] data = File.ReadAllBytes(path);

            try
            {
                ValidatePortableExecutable(data, path);

                int shiftJisOffset = FindUnique(
                    data, YurisCharacterRangeTable.ShiftJis, "Shift-JIS");
                int gbkOffset = FindUnique(
                    data, YurisCharacterRangeTable.Gbk, "GBK");

                bool tableSupported = (shiftJisOffset >= 0 && gbkOffset < 0) ||
                                      (shiftJisOffset < 0 && gbkOffset >= 0);

                return tableSupported && FindFontPatch(data, out _) >= 0;
            }
            catch (InvalidDataException)
            {
                // A malformed, ambiguous, or hybrid executable is not a
                // reliable signal that this is a supported YU-RIS game.
                return false;
            }
        }

        public static YurisExecutablePatchResult Patch(
            string inputPath,
            string outputPath)
        {
            if (string.IsNullOrWhiteSpace(inputPath))
                throw new ArgumentException("An input executable path is required.", nameof(inputPath));

            if (string.IsNullOrWhiteSpace(outputPath))
                throw new ArgumentException("An output executable path is required.", nameof(outputPath));

            string sourcePath = Path.GetFullPath(inputPath);
            string destinationPath = Path.GetFullPath(outputPath);

            StringComparison pathComparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;

            if (sourcePath.Equals(destinationPath, pathComparison))
                throw new ArgumentException(
                    "The output path must be different from the input path. The original executable is never modified.",
                    nameof(outputPath));

            if (!File.Exists(sourcePath))
                throw new FileNotFoundException("The input executable was not found.", sourcePath);

            byte[] data = File.ReadAllBytes(sourcePath);
            ValidatePortableExecutable(data, sourcePath);

            ReadOnlySpan<byte> shiftJisTable = YurisCharacterRangeTable.ShiftJis;
            ReadOnlySpan<byte> gbkTable = YurisCharacterRangeTable.Gbk;

            int shiftJisOffset = FindUnique(data, shiftJisTable, "Shift-JIS");
            int gbkOffset = FindUnique(data, gbkTable, "GBK");

            YurisExecutablePatchStatus status;
            int tableOffset;

            if (shiftJisOffset >= 0 && gbkOffset < 0)
            {
                gbkTable.CopyTo(data.AsSpan(shiftJisOffset, YurisCharacterRangeTable.Length));
                status = YurisExecutablePatchStatus.Patched;
                tableOffset = shiftJisOffset;
            }
            else if (shiftJisOffset < 0 && gbkOffset >= 0)
            {
                status = YurisExecutablePatchStatus.AlreadyPatched;
                tableOffset = gbkOffset;
            }
            else if (shiftJisOffset >= 0 && gbkOffset >= 0)
            {
                throw new InvalidDataException(
                    "The executable contains both the Shift-JIS and GBK range tables. " +
                    "It may be a hybrid or unsupported executable; no output was written.");
            }
            else
            {
                throw new NotSupportedException(
                    "The YU-RIS Shift-JIS character range table was not found. " +
                    "This executable may use an unsupported engine version or a different table layout.");
            }

            int fontOffset = FindFontPatch(data, out bool fontAlreadyPatched);
            if (fontOffset < 0)
            {
                throw new NotSupportedException(
                    "The YU-RIS font-creation code was not found. " +
                    "This executable may use an unsupported engine version; no output was written.");
            }

            if (!fontAlreadyPatched)
            {
                FontCharsetPatch.CopyTo(data.AsSpan(fontOffset + FontPatchOffset, FontCharsetPatch.Length));
                status = YurisExecutablePatchStatus.Patched;
            }

            WriteAtomically(destinationPath, data);

            return new YurisExecutablePatchResult(
                status,
                tableOffset,
                sourcePath,
                destinationPath);
        }

        private static int FindUnique(byte[] data, ReadOnlySpan<byte> pattern, string tableName)
        {
            int matchOffset = -1;
            int searchOffset = 0;

            while (searchOffset <= data.Length - pattern.Length)
            {
                int relativeOffset = data.AsSpan(searchOffset).IndexOf(pattern);
                if (relativeOffset < 0)
                    break;

                int currentOffset = searchOffset + relativeOffset;
                if (matchOffset >= 0)
                {
                    throw new InvalidDataException(
                        $"The executable contains more than one {tableName} range table. " +
                        "The target is ambiguous; no output was written.");
                }

                matchOffset = currentOffset;
                searchOffset = currentOffset + 1;
            }

            return matchOffset;
        }

        /// <summary>
        /// Locates the font-creation code, either original or already patched.
        /// Returns -1 when neither form is present.
        /// </summary>
        private static int FindFontPatch(byte[] data, out bool alreadyPatched)
        {
            int originalOffset = FindUniqueMasked(data, OriginalFontPattern, "original font-creation code");
            int patchedOffset = FindUniqueMasked(data, PatchedFontPattern, "patched font-creation code");
            alreadyPatched = patchedOffset >= 0;

            if (originalOffset >= 0 && patchedOffset >= 0)
            {
                throw new InvalidDataException(
                    "The executable contains both the original and the patched font-creation code. " +
                    "It may be a hybrid or unsupported executable; no output was written.");
            }

            // Both table references must point to the same LOGFONT array; otherwise this is not the expected code.
            if (originalOffset >= 0 &&
                !data.AsSpan(originalOffset + FontTableOffsetA, 4)
                    .SequenceEqual(data.AsSpan(originalOffset + FontTableOffsetB, 4)))
            {
                throw new InvalidDataException(
                    "The font-creation code does not reference a single LOGFONT table; no output was written.");
            }

            return originalOffset >= 0 ? originalOffset : patchedOffset;
        }

        private static int FindUniqueMasked(byte[] data, byte?[] pattern, string description)
        {
            int matchOffset = -1;

            for (int offset = 0; offset <= data.Length - pattern.Length; offset++)
            {
                if (!MatchesAt(data, offset, pattern))
                    continue;

                if (matchOffset >= 0)
                {
                    throw new InvalidDataException(
                        $"The executable contains more than one {description}. " +
                        "The target is ambiguous; no output was written.");
                }

                matchOffset = offset;
            }

            return matchOffset;
        }

        private static bool MatchesAt(byte[] data, int offset, byte?[] pattern)
        {
            for (int i = 0; i < pattern.Length; i++)
            {
                if (pattern[i] is byte expected && data[offset + i] != expected)
                    return false;
            }

            return true;
        }

        private static byte?[] ParsePattern(string text)
        {
            string[] tokens = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var pattern = new byte?[tokens.Length];

            for (int i = 0; i < tokens.Length; i++)
                pattern[i] = tokens[i] == "??" ? null : Convert.ToByte(tokens[i], 16);

            return pattern;
        }

        private static void ValidatePortableExecutable(byte[] data, string path)
        {
            // DOS header: "MZ" and e_lfanew at offset 0x3C.
            if (data.Length < 0x40 ||
                data[0] != (byte)'M' ||
                data[1] != (byte)'Z')
            {
                throw new InvalidDataException(
                    $"'{path}' is not a valid Windows executable (missing MZ header).");
            }

            int peOffset = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(0x3C, 4));
            if (peOffset < 0 || peOffset > data.Length - 4)
            {
                throw new InvalidDataException(
                    $"'{path}' has an invalid PE header offset.");
            }

            // PE signature: "PE\0\0".
            if (data[peOffset] != (byte)'P' ||
                data[peOffset + 1] != (byte)'E' ||
                data[peOffset + 2] != 0 ||
                data[peOffset + 3] != 0)
            {
                throw new InvalidDataException(
                    $"'{path}' does not contain a valid PE signature.");
            }
        }

        private static void WriteAtomically(string outputPath, byte[] data)
        {
            string? directory = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            string temporaryPath = outputPath + "." + Guid.NewGuid().ToString("N") + ".tmp";

            try
            {
                File.WriteAllBytes(temporaryPath, data);
                File.Move(temporaryPath, outputPath, true);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }
        }
    }
}
